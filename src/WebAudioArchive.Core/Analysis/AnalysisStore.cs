using System.Globalization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>
/// Read-only helpers over the analysis folder for readers such as the web app. Damaged files are skipped. Nothing here
/// takes a path from a caller: folders are found by listing the analysis tree itself.
/// </summary>
public static class AnalysisStore
{
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

    public static IReadOnlyList<TranscriptDocument> Transcripts(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        SegmentDirs(paths, fromUtc, toUtc)
            .Select(d => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(d, AnalysisPaths.TranscriptFile), out _))
            .OfType<TranscriptDocument>()
            .Where(t => t.AudioFromUtc < toUtc && t.AudioToUtc > fromUtc)
            .ToList();

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
