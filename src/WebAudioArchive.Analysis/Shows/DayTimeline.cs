using System.Globalization;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// events/&lt;yyyy&gt;/&lt;MM-dd&gt;.json: every segment's events for one UTC day, merged, with the preferred recorder's events
/// wherever it has audio (D7), each event filed in the day of its start. Rebuilt from the segment files at any time.
/// </summary>
public static class DayTimeline
{
    public static IReadOnlyList<EventsDocument> ReadSegments(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var docs = new List<EventsDocument>();
        if (!Directory.Exists(paths.SegmentsDir)) return docs;
        var first = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(toUtc.UtcDateTime);
        foreach (var recorder in Directory.EnumerateDirectories(paths.SegmentsDir))
        {
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var dayDir = Path.Combine(recorder, d.ToString("yyyy", CultureInfo.InvariantCulture), d.ToString("MM-dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(dayDir)) continue;
                foreach (var job in Directory.EnumerateDirectories(dayDir))
                {
                    var doc = AnalysisJson.TryRead<EventsDocument>(Path.Combine(job, AnalysisPaths.EventsFile), out _);
                    if (doc is not null && doc.FromUtc < toUtc && doc.ToUtc > fromUtc) docs.Add(doc);
                }
            }
        }

        return docs;
    }

    public static IReadOnlyList<AnalysisEvent> Merge(IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference)
    {
        var spans = segments.SelectMany(s => s.Coverage).ToList();
        return segments
            .SelectMany(s => s.Events)
            .Where(e => RecorderPreference.Keep(e.Recorder, e.StartUtc, spans, preference))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderBy(e => e.StartUtc)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ToList();
    }

    public static EventsDocument BuildDay(string station, DateOnly day, IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference, DateTimeOffset now)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var events = Merge(segments, preference).Where(e => e.StartUtc >= from && e.StartUtc < to).ToList();
        var versions = new Dictionary<string, string>
        {
            ["detectors"] = string.Join(' ', segments.Select(s => s.Versions.GetValueOrDefault("detectors")).OfType<string>().Distinct().Order(StringComparer.Ordinal)),
            ["segments"] = segments.Count.ToString(CultureInfo.InvariantCulture),
        };
        var coverage = segments.SelectMany(s => s.Coverage).Where(c => c.ToUtc > from && c.FromUtc < to).OrderBy(c => c.FromUtc).ToList();
        return new EventsDocument(EventsDocument.CurrentSchema, station, "day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            from, to, now, versions, coverage, null, events);
    }

    public static void RebuildDay(AnalysisPaths paths, DateOnly day, IReadOnlyList<string> preference, DateTimeOffset now)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var segments = ReadSegments(paths, from, from.AddDays(1));
        AnalysisJson.Write(paths.DayEvents(day), BuildDay(paths.Station, day, segments, preference, now));
    }
}
