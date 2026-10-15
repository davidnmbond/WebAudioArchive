using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Web.Clips;

/// <summary>
/// One master segment as the web app sees it. For a closed segment the values come from its sidecar. For the open
/// segment (.mp3.part, no sidecar yet) the start comes from the path, the end from the file's last write time, and
/// <see cref="AudioSeconds"/> from the byte count (constant bitrate), so the Revma start offset is measured, not assumed.
/// </summary>
public sealed record CatalogSegment(
    string Recorder,
    string Station,
    string AudioPath,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Bytes,
    double AudioSeconds,
    int BitrateKbps,
    IReadOnlyList<SegmentGap> Gaps,
    string? EndedBy,
    bool IsOpen,
    long FileLength,
    DateTime LastWriteUtc)
{
    public int BytesPerSecond => BitrateKbps * 125;

    public double WallSeconds => (EndUtc - StartUtc).TotalSeconds;

    /// <summary>Gaps clipped to the segment span, in time order.</summary>
    public IReadOnlyList<SegmentGap> SpanGaps { get; } = Gaps
        .Where(g => g.EndUtc > StartUtc && g.StartUtc < EndUtc)
        .OrderBy(g => g.StartUtc)
        .Select(g => g with { StartUtc = g.StartUtc < StartUtc ? StartUtc : g.StartUtc, EndUtc = g.EndUtc > EndUtc ? EndUtc : g.EndUtc })
        .ToList();

    public double GapSeconds => SpanGaps.Sum(g => (g.EndUtc - g.StartUtc).TotalSeconds);

    /// <summary>
    /// Audio held beyond the wall span: max(0, AudioSeconds - (span - gaps)). With no gaps this is the Revma start
    /// offset (about 5 s on a connect, 0 for a segment opened at an hour boundary).
    /// </summary>
    public double BurstSeconds => Math.Max(0, AudioSeconds - (WallSeconds - GapSeconds));
}

/// <summary>Reads the recorders' output folders. Read-only: nothing here ever writes, moves or deletes a data file.</summary>
public sealed class SegmentCatalog(WebSettings settings, TimeProvider time, ILogger<SegmentCatalog> log)
{
    public const int CacheSeconds = 5;

    readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<CatalogSegment> List)> _cache = new();

    public IReadOnlyList<CatalogSegment> Segments(string recorder)
    {
        var r = settings.Find(recorder) ?? throw new ArgumentException($"Unknown recorder '{recorder}'", nameof(recorder));
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(r.Id, out var hit) && now - hit.At < TimeSpan.FromSeconds(CacheSeconds) && now >= hit.At)
            return hit.List;

        var list = Scan(r.Root, settings.Station, r.Id, log);
        _cache[r.Id] = (now, list);
        return list;
    }

    public IReadOnlyList<RecorderSegments> All() =>
        settings.Recorders.Select(r => new RecorderSegments(r.Id, Segments(r.Id))).ToList();

    /// <summary>Every readable segment under &lt;root&gt;/&lt;station&gt;/master, in start order.</summary>
    public static IReadOnlyList<CatalogSegment> Scan(string root, string station, string recorder, ILogger log)
    {
        var master = Path.Combine(root, station, "master");
        var list = new List<CatalogSegment>();
        if (!Directory.Exists(master)) return list;

        foreach (var json in Directory.EnumerateFiles(master, "*.json", SearchOption.AllDirectories))
        {
            if (!json.EndsWith(".json", StringComparison.Ordinal)) continue;
            var seg = TryClosed(json, station, recorder, log);
            if (seg is not null) list.Add(seg);
        }

        foreach (var part in Directory.EnumerateFiles(master, "*.mp3.part", SearchOption.AllDirectories))
        {
            if (!part.EndsWith(".mp3.part", StringComparison.Ordinal)) continue;
            var seg = TryOpen(part, station, recorder, log);
            if (seg is not null) list.Add(seg);
        }

        list.Sort((x, y) => x.StartUtc.CompareTo(y.StartUtc));
        return list;
    }

    static CatalogSegment? TryClosed(string json, string station, string recorder, ILogger log)
    {
        SegmentMeta? meta;
        try
        {
            meta = SegmentMetaStore.Read(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            log.LogWarning("Skipping unreadable sidecar {Json}: {Error}", json, ex.Message);
            return null;
        }

        if (meta is null || string.IsNullOrEmpty(meta.File) || meta.StartUtc == default || meta.EndUtc < meta.StartUtc || meta.Bytes < 0)
        {
            log.LogWarning("Skipping incomplete sidecar {Json}", json);
            return null;
        }

        // The sidecar names its audio file; only a plain file name in the same folder is accepted.
        if (Path.GetFileName(meta.File) != meta.File || meta.File.Contains("..", StringComparison.Ordinal))
        {
            log.LogWarning("Skipping sidecar {Json}: file name {File} is not a plain name", json, meta.File);
            return null;
        }

        var audio = Path.Combine(Path.GetDirectoryName(json)!, meta.File);
        var info = new FileInfo(audio);
        if (!info.Exists)
        {
            log.LogWarning("Skipping sidecar {Json}: its audio file {Audio} does not exist", json, audio);
            return null;
        }

        var bitrate = meta.Format?.BitrateKbps is > 0 and var b ? b : ReadBitrate(audio) ?? 128;
        return new CatalogSegment(
            recorder, station, audio, meta.StartUtc.ToUniversalTime(), meta.EndUtc.ToUniversalTime(), Math.Min(meta.Bytes, info.Length),
            meta.AudioSeconds, bitrate, meta.Gaps ?? [], meta.EndedBy, IsOpen: false, info.Length, info.LastWriteTimeUtc);
    }

    static CatalogSegment? TryOpen(string part, string station, string recorder, ILogger log)
    {
        try
        {
            var start = StartFromPath(part[..^".part".Length]);
            if (start is null)
            {
                log.LogWarning("Skipping {Part}: its name is not a UTC start time", part);
                return null;
            }

            // Opened with ReadWrite sharing so the recorder can keep appending; the length is read from the open handle
            // because directory listings can lag for a file that is being written.
            long length;
            int? bitrate;
            using (var fs = OpenShared(part))
            {
                length = fs.Length;
                bitrate = ReadBitrate(fs);
            }

            if (bitrate is null)
            {
                log.LogInformation("Skipping {Part}: no complete frame yet", part);
                return null;
            }

            var lastWrite = File.GetLastWriteTimeUtc(part);
            var end = new DateTimeOffset(lastWrite, TimeSpan.Zero);
            if (end < start.Value) end = start.Value;
            var audioSeconds = (double)length / (bitrate.Value * 125);
            return new CatalogSegment(
                recorder, station, part, start.Value, end, length, audioSeconds, bitrate.Value, [], null, IsOpen: true, length, lastWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The recorder may have just renamed it to .mp3 at the hour boundary.
            log.LogInformation("Skipping {Part}: {Error}", part, ex.Message);
            return null;
        }
    }

    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);

    static int? ReadBitrate(string path)
    {
        try
        {
            using var fs = OpenShared(path);
            return ReadBitrate(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static int? ReadBitrate(Stream s)
    {
        Span<byte> head = stackalloc byte[Mp3FrameHeader.HeaderSize];
        s.Position = 0;
        return s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && Mp3FrameHeader.TryParse(head, out var h)
            ? h.BitrateKbps
            : null;
    }

    /// <summary>Same rule as the recorder's crash recovery: yyyy/MM-dd/HHmmss.mp3 or HHmmss-n.mp3, all UTC.</summary>
    public static DateTimeOffset? StartFromPath(string mp3Path)
    {
        var name = Path.GetFileName(mp3Path);
        var dayDir = Path.GetFileName(Path.GetDirectoryName(mp3Path));
        var yearDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mp3Path)));
        if (name is null || dayDir is null || yearDir is null || name.Length < 6 || dayDir.Length != 5) return null;
        return DateTimeOffset.TryParseExact(
            $"{yearDir}-{dayDir} {name[..6]}", "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
            ? start
            : null;
    }
}

public sealed record RecorderSegments(string Recorder, IReadOnlyList<CatalogSegment> Segments);
