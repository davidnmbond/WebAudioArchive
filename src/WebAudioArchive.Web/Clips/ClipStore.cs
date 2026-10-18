using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Mp3;

namespace WebAudioArchive.Web.Clips;

public sealed record ClipFile(string Id, long Bytes, double Seconds, string DownloadName);

/// <summary>One byte range [Start, End) of a segment file, as the media endpoint reads it.</summary>
public sealed record ClipManifestPiece(string Path, long Start, long End);

/// <summary>What is kept in the cache for a clip: its download name, length and the byte ranges it is made of.</summary>
public sealed record ClipManifest(int Schema, string DownloadName, double Seconds, long Bytes, IReadOnlyList<ClipManifestPiece> Pieces)
{
    public const int CurrentSchema = 1;
}

/// <summary>
/// Clips without copying audio. A clip is its plan's byte ranges of the segment files, joined end to end, so it is
/// served straight from those files (<see cref="ClipStream"/>, with range requests) and only a small manifest is kept in
/// the cache. Registering checks each join is on a frame boundary, so the clip is whole frames throughout (the piece
/// edges are snapped to frames by the planner). The id is a hash of the station, recorder and exact piece list,
/// including each file's size and last write time, so a .part that has grown gives a new clip while an unchanged
/// request gives the same one. Audio is never decoded or re-encoded.
/// </summary>
public sealed class ClipStore(string cacheDir, IReadOnlyList<string> allowedRoots, ILogger log)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string IdFor(string station, ClipPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(station).Append('\n').Append(plan.Recorder).Append('\n');
        sb.Append(plan.WindowFrom.UtcTicks).Append('\n').Append(plan.WindowTo.UtcTicks).Append('\n');
        foreach (var p in plan.Pieces)
        {
            sb.Append(p.Segment.AudioPath).Append('|').Append(p.Segment.FileLength).Append('|')
              .Append(p.Segment.LastWriteUtc.Ticks).Append('|').Append(p.StartByte).Append('|').Append(p.EndByte).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    public static string DownloadName(string station, ClipPlan plan) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{station}_{plan.WindowFrom.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{plan.WindowTo.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{plan.Recorder.ToUpperInvariant()}.mp3");

    /// <summary>Seconds of audio in the plan: each piece's bytes over its segment's byte rate (the stream is constant bitrate).</summary>
    public static double SecondsOf(ClipPlan plan) => plan.Pieces.Sum(p => (double)(p.EndByte - p.StartByte) / p.Segment.BytesPerSecond);

    string ManifestPath(string id) => Path.Combine(cacheDir, id + ".json");

    /// <summary>Checks the joins and keeps the manifest (or touches the one already kept). Throws <see cref="ClipIntegrityException"/> for a join that is not on a frame.</summary>
    public ClipFile Register(string station, ClipPlan plan)
    {
        var id = IdFor(station, plan);
        var file = new ClipFile(id, plan.Pieces.Sum(p => p.EndByte - p.StartByte), SecondsOf(plan), DownloadName(station, plan));
        var path = ManifestPath(id);
        if (File.Exists(path))
        {
            // Touch it so housekeeping, which removes the oldest, keeps a clip that is in use.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            return file;
        }

        foreach (var p in plan.Pieces) CheckJoins(p);
        var manifest = new ClipManifest(ClipManifest.CurrentSchema, file.DownloadName, file.Seconds, file.Bytes,
            plan.Pieces.Select(p => new ClipManifestPiece(p.Segment.AudioPath, p.StartByte, p.EndByte)).ToList());
        Directory.CreateDirectory(cacheDir);
        var tmp = Path.Combine(cacheDir, $"{id}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Json));
        File.Move(tmp, path, overwrite: true);
        log.LogInformation("Clip {Id}: {Pieces} pieces, {Bytes} bytes, {Seconds:0.0} s from recorder {Recorder}, streamed from the segments",
            id, plan.Pieces.Count, file.Bytes, file.Seconds, plan.Recorder);
        return file;
    }

    /// <summary>
    /// The clip's manifest and a stream over its pieces, or null for an unknown id, an unreadable manifest, or one naming
    /// a file outside the recorders' folders (it is the app's own file, but nothing outside them is ever read).
    /// </summary>
    public (ClipManifest Manifest, ClipStream Stream)? Open(string id)
    {
        if (!IsValidId(id)) return null;
        ClipManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(ManifestPath(id)), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        if (manifest?.Pieces is not { Count: > 0 } pieces || !pieces.All(p => IsAllowed(p.Path) && p.Start >= 0 && p.End > p.Start))
        {
            log.LogWarning("Clip {Id}'s manifest is incomplete or names a file outside the recorders' folders; not serving it", id);
            return null;
        }

        return (manifest, new ClipStream(pieces));
    }

    bool IsAllowed(string path)
    {
        if (!path.EndsWith(".mp3", StringComparison.Ordinal) && !path.EndsWith(".mp3.part", StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(path);
        return full == path && allowedRoots.Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>A valid frame header where the piece starts, and where it ends unless that is the end of the file.</summary>
    static void CheckJoins(ClipPiece p)
    {
        using var fs = SegmentCatalog.OpenShared(p.Segment.AudioPath);
        if (p.EndByte <= p.StartByte || p.EndByte > fs.Length)
            throw new ClipIntegrityException($"{p.Segment.AudioPath}: bytes {p.StartByte} to {p.EndByte} are not inside the file ({fs.Length} bytes)");
        if (!HeaderAt(fs, p.StartByte))
            throw new ClipIntegrityException($"{p.Segment.AudioPath}: no MP3 frame starts at byte {p.StartByte}");
        if (p.EndByte < fs.Length && !HeaderAt(fs, p.EndByte))
            throw new ClipIntegrityException($"{p.Segment.AudioPath}: no MP3 frame starts at byte {p.EndByte}, where the piece ends");
    }

    static bool HeaderAt(Stream s, long at)
    {
        Span<byte> head = stackalloc byte[Mp3FrameHeader.HeaderSize];
        s.Position = at;
        return s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && Mp3FrameHeader.TryParse(head, out _);
    }
}

/// <summary>
/// A read-only, seekable stream over a clip's pieces, opening each segment file only when it is read, with the same
/// sharing the recorder allows. A piece of the open segment is still found after the recorder renames its .mp3.part to
/// .mp3 at the hour (the bytes are the same).
/// </summary>
public sealed class ClipStream : Stream
{
    readonly IReadOnlyList<ClipManifestPiece> _pieces;
    readonly long[] _offsets;
    long _position;
    FileStream? _file;
    int _fileIndex = -1;

    public ClipStream(IReadOnlyList<ClipManifestPiece> pieces)
    {
        _pieces = pieces;
        _offsets = new long[pieces.Count + 1];
        for (var i = 0; i < pieces.Count; i++) _offsets[i + 1] = _offsets[i] + (pieces[i].End - pieces[i].Start);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _offsets[^1];

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!Locate(buffer.Length, out var file, out var want)) return 0;
        var n = file.Read(buffer[..want]);
        return Advanced(n);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Locate(buffer.Length, out var file, out var want)) return 0;
        var n = await file.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
        return Advanced(n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Positions the right segment file for the current position; false at the end of the clip.</summary>
    bool Locate(int room, out FileStream file, out int want)
    {
        file = null!;
        want = 0;
        if (_position >= Length || room == 0) return false;
        var i = Array.BinarySearch(_offsets, _position);
        if (i < 0) i = ~i - 1;
        if (i >= _pieces.Count) return false;
        var piece = _pieces[i];
        var within = _position - _offsets[i];
        if (_fileIndex != i)
        {
            _file?.Dispose();
            _file = OpenPiece(piece.Path);
            _fileIndex = i;
        }

        file = _file!;
        file.Position = piece.Start + within;
        want = (int)Math.Min(room, piece.End - piece.Start - within);
        return true;
    }

    int Advanced(int n)
    {
        if (n == 0) throw new IOException($"{_pieces[_fileIndex].Path} ended before byte {_pieces[_fileIndex].End}");
        _position += n;
        return n;
    }

    static FileStream OpenPiece(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException) when (path.EndsWith(".part", StringComparison.Ordinal))
        {
            return new FileStream(path[..^".part".Length], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Cache housekeeping: drop manifests older than the maximum age, clip files built before clips were streamed, and stale temporary files.</summary>
public static class ClipCache
{
    /// <summary>Returns how many clips were removed. Only files in the cache directory itself are touched.</summary>
    public static int Housekeep(string cacheDir, DateTimeOffset now, TimeSpan maxAge, ILogger log)
    {
        if (!Directory.Exists(cacheDir)) return 0;
        var dir = new DirectoryInfo(cacheDir);

        foreach (var tmp in dir.EnumerateFiles("*.tmp*"))
        {
            if (now.UtcDateTime - tmp.LastWriteTimeUtc > TimeSpan.FromHours(1)) Delete(tmp, log);
        }

        var removed = 0;
        foreach (var f in dir.EnumerateFiles().Where(f => ClipStore.IsValidId(Path.GetFileNameWithoutExtension(f.Name))))
        {
            var legacy = f.Extension == ".mp3";
            var old = f.Extension == ".json" && now.UtcDateTime - f.LastWriteTimeUtc > maxAge;
            if (!legacy && !old) continue;
            Delete(f, log);
            removed++;
        }

        if (removed > 0) log.LogInformation("Cache housekeeping removed {Count} clip files", removed);
        return removed;
    }

    static void Delete(FileInfo f, ILogger log)
    {
        try
        {
            if (f.Exists) f.Delete();
        }
        catch (IOException ex)
        {
            log.LogWarning("Could not remove {File} from the cache: {Error}", f.FullName, ex.Message);
        }
    }
}
