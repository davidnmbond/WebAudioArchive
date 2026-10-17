using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>A hold, recheck or deletion was refused; the message says why. Nothing was moved or deleted.</summary>
public sealed class HoldingException(string message) : Exception(message);

/// <summary>
/// One original segment in held/. Status is held (waiting for its recheck), ready (rechecked, may be deleted once a
/// manager confirms) or failed (Reason says why). File is relative to the recorder's station master folder, as in
/// <see cref="SourceSegment.RelativeFile"/>; MergedFiles are the merged hours that hold its frames.
/// </summary>
public sealed record HeldRecord(
    string Recorder,
    string Station,
    string File,
    string Sha256,
    long Bytes,
    DateTimeOffset HeldUtc,
    IReadOnlyList<string> MergedFiles,
    string Status,
    DateTimeOffset? CheckedUtc,
    string? Reason);

/// <summary>
/// Issue #104: originals whose every frame is in a verified merged copy move to &lt;recorderRoot&gt;/&lt;station&gt;/held, are
/// rechecked after <see cref="HoldPeriod"/>, and are deleted only when a manager confirms. This is the only code that
/// removes broadcast audio, so every step refuses, and says why, when anything does not check out.
/// </summary>
public static class Holding
{
    public static readonly TimeSpan HoldPeriod = TimeSpan.FromDays(7);

    /// <summary>A ready record may be deleted only this long after its last recheck; after that a recheck is due first.</summary>
    public static readonly TimeSpan ReadyValidFor = TimeSpan.FromHours(24);

    public const string Held = "held";
    public const string Ready = "ready";
    public const string Failed = "failed";

    const string RecordSuffix = ".held.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    static readonly JsonSerializerOptions LogOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string HeldDir(string recorderRoot, string station, DateTimeOffset segmentStartUtc)
    {
        var u = segmentStartUtc.UtcDateTime;
        return Path.Combine(recorderRoot, station, "held", u.ToString("yyyy", CultureInfo.InvariantCulture), u.ToString("MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>The record file for a held audio file: &lt;stem&gt;.held.json beside it.</summary>
    public static string RecordPath(string heldMp3) =>
        Path.Combine(Path.GetDirectoryName(heldMp3)!, Path.GetFileNameWithoutExtension(heldMp3) + RecordSuffix);

    public static HeldRecord? ReadRecord(string heldJson) =>
        File.Exists(heldJson) ? JsonSerializer.Deserialize<HeldRecord>(File.ReadAllText(heldJson), Options) : null;

    /// <summary>
    /// True when the segment is whole frames from its first byte to its last and every frame of it is accounted for in
    /// <paramref name="mergedMp3s"/> (the merged hours from one before it to one after it), each of which must exist and
    /// still match the checksum in its sidecar's merge block. Frames are matched as the merge matched them, with
    /// <see cref="FrameMerge"/>: the merged hours in time order are the primary and the segment the secondary, so a frame
    /// repeated in a run (silence) counts once per occurrence, and a reconnect replay inside the segment, which the merged
    /// copy holds once, is not counted as missing. The segment is fully merged when the merge takes nothing from it.
    /// </summary>
    public static bool AllFramesMerged(SourceSegment s, IReadOnlyList<string> mergedMp3s, out string reason)
    {
        try
        {
            if (mergedMp3s.Count == 0)
            {
                reason = "no merged files were given";
                return false;
            }

            var mergedSegments = new List<SourceSegment>();
            foreach (var mp3 in mergedMp3s)
            {
                if (CheckMerged(mp3, fullHash: true) is { } bad)
                {
                    reason = bad;
                    return false;
                }

                var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
                mergedSegments.Add(new SourceSegment("merged", Path.GetDirectoryName(mp3)!, mp3, meta));
            }

            if (!File.Exists(s.AudioPath))
            {
                reason = $"{s.AudioPath} does not exist";
                return false;
            }

            var (source, wholeTo) = Frames(s);
            if (wholeTo != new FileInfo(s.AudioPath).Length)
            {
                reason = $"{s.Recorder} {s.RelativeFile} has bytes that are not a whole frame at byte {wholeTo}";
                return false;
            }

            var primary = new List<MergeFrame>();
            foreach (var m in mergedSegments.OrderBy(m => m.Meta.StartUtc)) primary.AddRange(Frames(m).Frames);

            var result = FrameMerge.Merge(primary, source);
            var missing = result.Blocks.Where(b => b.Source == FrameSource.Secondary).ToList();
            if (missing.Count > 0)
            {
                reason = $"{missing.Sum(b => b.Count)} frame(s) of {s.Recorder} {s.RelativeFile} are not in the merged files, the first is frame {missing.Min(b => b.Start)} of the segment";
                return false;
            }

            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reason = $"could not read the files: {ex.Message}";
            return false;
        }
    }

    /// <summary>The file's whole frames with their estimated UTC from its anchors, and the byte where its run of contiguous frames from byte 0 ends.</summary>
    static (List<MergeFrame> Frames, long WholeTo) Frames(SourceSegment s)
    {
        using var fs = OpenRead(s.AudioPath);
        var refs = FrameIndex.Read(fs, 0, fs.Length);
        var anchors = s.Anchors;
        var bytesPerSecond = s.BytesPerSecond;
        var frames = new List<MergeFrame>(refs.Count);
        long at = 0;
        var contiguous = true;
        foreach (var f in refs)
        {
            if (contiguous && f.Offset == at) at += f.Length;
            else contiguous = false;
            frames.Add(new MergeFrame(f.Hash, SegmentTimeline.ToUtc(anchors, (double)f.Offset / bytesPerSecond).ToUniversalTime()));
        }

        return (frames, at);
    }

    /// <summary>
    /// Moves a segment whose frames are all merged into <see cref="HeldDir"/>. The record is written first, then the
    /// audio moves, then its sidecar, so a stop part way leaves a record that <see cref="Repair"/> completes the move from.
    /// </summary>
    public static HeldRecord Hold(SourceSegment s, IReadOnlyList<string> mergedMp3s, DateTimeOffset now)
    {
        var stationDir = Path.GetDirectoryName(Path.GetFullPath(s.StationMasterDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(s.StationMasterDir)), "master", StringComparison.Ordinal))
            throw new HoldingException($"{s.AudioPath} is not under a station master folder");
        var station = Path.GetFileName(stationDir);
        var recorderRoot = Path.GetDirectoryName(stationDir)!;

        var heldDir = HeldDir(recorderRoot, station, s.Meta.StartUtc);
        var expected = Path.GetRelativePath(Path.Combine(stationDir, "held"), heldDir).Replace('\\', '/') + "/" + Path.GetFileName(s.AudioPath);
        if (s.RelativeFile != expected)
            throw new HoldingException($"{s.Recorder} {s.RelativeFile} is not in the folder for its start time ({expected}); not held");

        var json = Path.ChangeExtension(s.AudioPath, ".json");
        if (!File.Exists(json)) throw new HoldingException($"{s.Recorder} {s.RelativeFile} has no sidecar; not held");
        var length = new FileInfo(s.AudioPath).Length;
        if (length != s.Meta.Bytes)
            throw new HoldingException($"{s.Recorder} {s.RelativeFile} is {length} bytes but its sidecar says {s.Meta.Bytes}; not held");

        var sha = Sha256(s.AudioPath);
        if (!AllFramesMerged(s, mergedMp3s, out var reason)) throw new HoldingException($"not held: {reason}");
        if (Sha256(s.AudioPath) != sha) throw new HoldingException($"{s.Recorder} {s.RelativeFile} changed while it was checked; not held");

        var heldMp3 = Path.Combine(heldDir, Path.GetFileName(s.AudioPath));
        var heldJson = Path.ChangeExtension(heldMp3, ".json");
        var recordPath = RecordPath(heldMp3);
        foreach (var target in new[] { heldMp3, heldJson, recordPath })
            if (File.Exists(target)) throw new HoldingException($"{target} already exists; not held");

        var record = new HeldRecord(s.Recorder, station, s.RelativeFile, sha, length, now.ToUniversalTime(), [.. mergedMp3s], Held, null, null);
        Directory.CreateDirectory(heldDir);
        WriteRecord(recordPath, record);
        File.Move(s.AudioPath, heldMp3);
        File.Move(json, heldJson);
        return record;
    }

    /// <summary>
    /// Once <see cref="HoldPeriod"/> has passed since the hold, checks the held audio against its checksum and its frames
    /// against the merged files again, and stores ready or failed with the reason. Before then the record is returned as it is.
    /// </summary>
    public static HeldRecord Recheck(string heldJson, DateTimeOffset now)
    {
        var record = Load(heldJson);
        if (now < record.HeldUtc + HoldPeriod) return record;

        var reason = CheckHeld(heldJson, record);
        if (reason is null)
        {
            var heldMp3 = HeldMp3(heldJson, record);
            SegmentMeta? meta;
            try
            {
                meta = SegmentMetaStore.Read(Path.ChangeExtension(heldMp3, ".json"));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                meta = null;
            }

            if (meta is null) reason = $"the held sidecar for {record.File} is missing or unreadable";
            else
            {
                var segment = new SourceSegment(record.Recorder, Path.GetDirectoryName(heldMp3)!, heldMp3, meta);
                if (!AllFramesMerged(segment, record.MergedFiles, out var why)) reason = why;
            }
        }

        var updated = record with { Status = reason is null ? Ready : Failed, CheckedUtc = now.ToUniversalTime(), Reason = reason };
        WriteRecord(heldJson, updated);
        return updated;
    }

    /// <summary>
    /// Deletes a held segment a manager has confirmed: only when its record is ready, the held audio still matches its
    /// checksum, it was rechecked within <see cref="ReadyValidFor"/> and the merged files are still there. The deletion
    /// is logged before anything is removed, as one JSON line of {utc, confirmedBy, recorder, file, sha256, bytes}.
    /// Anything else throws and deletes nothing.
    /// </summary>
    public static void Delete(string heldJson, string confirmedBy, DateTimeOffset now, string deletionsLog)
    {
        if (string.IsNullOrWhiteSpace(confirmedBy)) throw new HoldingException("a deletion must name who confirmed it");
        var record = Load(heldJson);
        if (record.Status != Ready)
            throw new HoldingException($"{record.Recorder} {record.File} is {record.Status}, not ready; not deleted");

        if (record.CheckedUtc is not { } checkedUtc || now - checkedUtc > ReadyValidFor)
            throw new HoldingException($"{record.Recorder} {record.File}: recheck due (last checked {record.CheckedUtc:O}); not deleted");

        // A manager's hold mark (spec 3.12) will block deletion here once hold marks exist.

        if (CheckHeld(heldJson, record) is { } changed) throw new HoldingException($"not deleted: {changed}");
        foreach (var mp3 in record.MergedFiles)
            if (CheckMerged(mp3, fullHash: false) is { } bad) throw new HoldingException($"not deleted: {bad}");

        var heldMp3 = HeldMp3(heldJson, record);
        try
        {
            AppendLine(deletionsLog, JsonSerializer.Serialize(
                new DeletionEntry(now.ToUniversalTime(), confirmedBy, record.Recorder, record.File, record.Sha256, record.Bytes), LogOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HoldingException($"not deleted: the deletions log {deletionsLog} could not be written: {ex.Message}");
        }

        File.Delete(heldMp3);
        File.Delete(Path.ChangeExtension(heldMp3, ".json"));
        File.Delete(heldJson);
    }

    /// <summary>
    /// Finishes moves a stop interrupted. With a record, the move is completed into held/: the file still in master/
    /// joins the one already held (both still in master/ moves only if the audio matches the record). Without a record
    /// the move was never committed, so a file in held/ goes back to master/. A file is never overwritten.
    /// </summary>
    public static void Repair(string recorderRoot, string station)
    {
        var held = Path.Combine(recorderRoot, station, "held");
        var master = Path.Combine(recorderRoot, station, "master");
        if (!Directory.Exists(held)) return;

        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(held, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var stem = name.EndsWith(RecordSuffix, StringComparison.Ordinal) ? name[..^RecordSuffix.Length]
                : name.EndsWith(".mp3", StringComparison.Ordinal) ? name[..^4]
                : name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5]
                : null;
            if (stem is not null) stems.Add(Path.Combine(Path.GetRelativePath(held, Path.GetDirectoryName(file)!), stem));
        }

        foreach (var stem in stems)
        {
            var hMp3 = Path.Combine(held, stem + ".mp3");
            var hJson = Path.Combine(held, stem + ".json");
            var mMp3 = Path.Combine(master, stem + ".mp3");
            var mJson = Path.Combine(master, stem + ".json");
            var recordPath = Path.Combine(held, stem + RecordSuffix);

            HeldRecord? record;
            try
            {
                record = ReadRecord(recordPath);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue; // an unreadable record: leave everything where it is for a person to look at
            }

            if (record is not null)
            {
                var noneHeld = !File.Exists(hMp3) && !File.Exists(hJson);
                if (noneHeld)
                {
                    if (!File.Exists(mMp3) || !File.Exists(mJson) || Sha256(mMp3) != record.Sha256) continue;
                    MoveIfFree(mMp3, hMp3);
                    MoveIfFree(mJson, hJson);
                }
                else
                {
                    MoveIfFree(mMp3, hMp3);
                    MoveIfFree(mJson, hJson);
                }
            }
            else
            {
                MoveIfFree(hMp3, mMp3);
                MoveIfFree(hJson, mJson);
            }
        }
    }

    static HeldRecord Load(string heldJson)
    {
        try
        {
            return ReadRecord(heldJson) ?? throw new HoldingException($"{heldJson} is not a held record");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new HoldingException($"{heldJson} could not be read: {ex.Message}");
        }
    }

    sealed record DeletionEntry(DateTimeOffset Utc, string ConfirmedBy, string Recorder, string File, string Sha256, long Bytes);

    static void MoveIfFree(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
    }

    static string HeldMp3(string heldJson, HeldRecord record) =>
        Path.Combine(Path.GetDirectoryName(heldJson)!, Path.GetFileName(record.File));

    /// <summary>Null when the held audio is there with the record's length and checksum, otherwise why not.</summary>
    static string? CheckHeld(string heldJson, HeldRecord record)
    {
        var mp3 = HeldMp3(heldJson, record);
        if (Path.GetFileNameWithoutExtension(mp3) + RecordSuffix != Path.GetFileName(heldJson))
            return $"the record {heldJson} does not name its own audio ({record.File})";
        try
        {
            if (!File.Exists(mp3)) return $"the held file {record.File} is missing";
            var length = new FileInfo(mp3).Length;
            if (length != record.Bytes) return $"the held file {record.File} is {length} bytes, not {record.Bytes}; its checksum no longer matches";
            if (Sha256(mp3) != record.Sha256) return $"the held file {record.File} checksum no longer matches";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the held file {record.File} could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// Null when a merged file is there with a sidecar whose merge block matches it, otherwise why not. With
    /// <paramref name="fullHash"/> false only the length is compared, not the checksum.
    /// </summary>
    static string? CheckMerged(string mp3, bool fullHash)
    {
        try
        {
            if (!File.Exists(mp3)) return $"the merged file {mp3} is missing";
            SegmentMeta? meta;
            try
            {
                meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"));
            }
            catch (JsonException)
            {
                meta = null;
            }

            if (meta?.Merge is null) return $"the merged file {mp3} has no readable merge sidecar";
            var length = new FileInfo(mp3).Length;
            if (length != meta.Bytes) return $"the merged file {mp3} is {length} bytes but its sidecar says {meta.Bytes}";
            if (fullHash && Sha256(mp3) != meta.Merge.Sha256) return $"the merged file {mp3} no longer matches its sidecar checksum";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the merged file {mp3} could not be read: {ex.Message}";
        }
    }

    static void WriteRecord(string path, HeldRecord record)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>One write of the whole line, flushed to disk, through one handle held exclusively so a concurrent writer fails instead of racing. A torn last line from an earlier crash is ended first so this entry stays whole.</summary>
    static void AppendLine(string path, string line)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var prefix = "";
        if (fs.Length > 0)
        {
            fs.Position = fs.Length - 1;
            if (fs.ReadByte() != '\n') prefix = "\n";
        }

        fs.Position = fs.Length;
        var bytes = Encoding.UTF8.GetBytes(prefix + line + "\n");
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static string Sha256(string path)
    {
        using var fs = OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }
}
