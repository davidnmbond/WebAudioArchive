using System.Globalization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>A transcript.json is larger than <see cref="AnalysisStore.MaxTranscriptBytes"/>.</summary>
public sealed class TranscriptTooLargeException(string path) : Exception("A transcript file is larger than the limit and was not loaded.")
{
    public string Path { get; } = path;
}

/// <summary>
/// Read-only helpers over the analysis folder for readers such as the web app. Damaged files are skipped. Nothing here
/// takes a path from a caller: folders are found by listing the analysis tree itself.
/// </summary>
public static class AnalysisStore
{
    /// <summary>The largest transcript.json a reader will load (a one hour segment is about 1 MB); a bigger file is refused, not parsed.</summary>
    public const long MaxTranscriptBytes = 32L * 1024 * 1024;

    /// <summary>The longest wall span of one segment assumed when choosing which job folders can overlap a range (segments are cut hourly; this is conservative).</summary>
    public static readonly TimeSpan MaxSegmentSpan = TimeSpan.FromHours(2);

    /// <summary>Segment result folders for segments filed from the day before <paramref name="fromUtc"/> to the day of <paramref name="toUtc"/>.</summary>
    public static IEnumerable<string> SegmentDirs(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!Directory.Exists(paths.SegmentsDir)) yield break;
        var first = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(toUtc.UtcDateTime);
        foreach (var recorder in Directory.EnumerateDirectories(paths.SegmentsDir).Order(StringComparer.Ordinal))
        {
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var dayDir = Path.Combine(recorder, d.ToString("yyyy", CultureInfo.InvariantCulture), d.ToString("MM-dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(dayDir)) continue;
                foreach (var job in Directory.EnumerateDirectories(dayDir).Order(StringComparer.Ordinal)) yield return job;
            }
        }
    }

    /// <summary>Loads one transcript.json, null when it is missing or damaged; throws <see cref="TranscriptTooLargeException"/> above <see cref="MaxTranscriptBytes"/>.</summary>
    public static TranscriptDocument? ReadTranscript(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > MaxTranscriptBytes) throw new TranscriptTooLargeException(path);
        return AnalysisJson.TryRead<TranscriptDocument>(path, out _);
    }

    /// <summary>
    /// Transcripts that can overlap [fromUtc, toUtc). A job folder is named for its segment's UTC start (HHmmss, inside its
    /// day folder), so folders that start after the range or more than <see cref="MaxSegmentSpan"/> before it are not opened at
    /// all. <paramref name="read"/> loads one file (a seam for caching and tests); the default is <see cref="ReadTranscript"/>.
    /// </summary>
    public static IReadOnlyList<TranscriptDocument> Transcripts(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc, Func<string, TranscriptDocument?>? read = null)
    {
        read ??= ReadTranscript;
        return SegmentDirs(paths, fromUtc, toUtc)
            .Where(d => MayOverlap(d, fromUtc, toUtc))
            .Select(d => read(Path.Combine(d, AnalysisPaths.TranscriptFile)))
            .OfType<TranscriptDocument>()
            .Where(t => t.AudioFromUtc < toUtc && t.AudioToUtc > fromUtc)
            .ToList();
    }

    /// <summary>False only when the folder name and its day folder prove the segment cannot overlap; a name that does not parse is opened.</summary>
    static bool MayOverlap(string jobDir, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var name = Path.GetFileName(jobDir);
        var dayDir = Path.GetDirectoryName(jobDir);
        var yearDir = dayDir is null ? null : Path.GetDirectoryName(dayDir);
        if (name.Length < 6 || dayDir is null || yearDir is null
            || !DateTime.TryParseExact($"{Path.GetFileName(yearDir)}-{Path.GetFileName(dayDir)} {name[..6]}", "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            return true;
        var s = new DateTimeOffset(start, TimeSpan.Zero);
        return s < toUtc && s > fromUtc - MaxSegmentSpan;
    }

    /// <summary>Events starting in [fromUtc, toUtc), from the merged day files (each event is filed in the day it starts).</summary>
    public static IReadOnlyList<AnalysisEvent> DayEvents(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var events = new List<AnalysisEvent>();
        for (var d = DateOnly.FromDateTime(fromUtc.UtcDateTime); d <= DateOnly.FromDateTime(toUtc.UtcDateTime); d = d.AddDays(1))
        {
            var doc = AnalysisJson.TryRead<EventsDocument>(paths.DayEvents(d), out _);
            if (doc is not null) events.AddRange(doc.Events.Where(e => e.StartUtc >= fromUtc && e.StartUtc < toUtc));
        }

        return events.OrderBy(e => e.StartUtc).ToList();
    }

    /// <summary>
    /// The folder of a show's analysis, found by listing the station's shows folder and matching the name exactly, so the id
    /// a caller gives only ever selects a folder that exists; it is never turned into a path. Null when there is none.
    /// </summary>
    public static string? FindShowDir(AnalysisPaths paths, string showId, DateTimeOffset startUtc)
    {
        var year = Path.Combine(paths.ShowsDir, startUtc.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture));
        if (!Directory.Exists(year)) return null;
        return Directory.EnumerateDirectories(year).FirstOrDefault(d => string.Equals(Path.GetFileName(d), showId, StringComparison.Ordinal));
    }
}
