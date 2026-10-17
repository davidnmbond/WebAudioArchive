using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// After segment jobs finish: rebuilds the UTC day timelines they touch and the show-analysis.json of every show
/// whose window (show plus margins) overlaps them and has ended. A show is complete when no planned job overlaps its
/// window any more; only complete shows are enriched.
/// </summary>
public sealed class ShowProcessor(AnalysisPaths paths, ScheduleProvider schedule, IReadOnlyList<string> preference, TimeSpan margin, IClock clock, ILogger log)
{
    public IReadOnlyList<ShowAnalysis> Update(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<SourceSpan> pending)
    {
        var now = clock.UtcNow;
        for (var d = DateOnly.FromDateTime(fromUtc.UtcDateTime); d <= DateOnly.FromDateTime(toUtc.UtcDateTime); d = d.AddDays(1))
            DayTimeline.RebuildDay(paths, d, preference, now);

        var results = new List<ShowAnalysis>();
        foreach (var show in schedule.Occurrences(fromUtc - margin, toUtc + margin))
        {
            var windowFrom = show.StartUtc - margin;
            var windowTo = show.EndUtc + margin;
            if (windowTo > now) continue;

            var segments = DayTimeline.ReadSegments(paths, windowFrom, windowTo);
            var events = DayTimeline.Merge(segments, preference).Where(e => e.StartUtc < windowTo && (e.EndUtc > windowFrom || e.StartUtc >= windowFrom)).ToList(); // by overlap: the builder clips durations to the show
            var transcripts = segments
                .Select(s => s.Scope.StartsWith("segment:", StringComparison.Ordinal) ? s.Scope["segment:".Length..] : null)
                .OfType<string>()
                .Select(job => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(paths.SegmentDir(job), AnalysisPaths.TranscriptFile), out _))
                .OfType<TranscriptDocument>()
                .ToList();
            var lines = TranscriptSlicer.Slice(transcripts, windowFrom, windowTo, preference);
            // One source per stretch of audio actually analysed (the coverage splits a recording at timeline gaps), not per segment.
            var sources = segments
                .SelectMany(s => s.Coverage.Select(c => new ShowSource(c.Recorder, s.Scope.StartsWith("segment:", StringComparison.Ordinal) ? s.Scope["segment:".Length..] : s.Scope, c.FromUtc, c.ToUtc)))
                .OrderBy(s => s.FromUtc)
                .ToList();
            var complete = !pending.Any(p => p.FromUtc < windowTo && p.ToUtc > windowFrom);

            var doc = ShowAnalysisBuilder.Build(show, margin, events, lines, sources, complete, now);
            AnalysisJson.Write(Path.Combine(paths.ShowDir(show.Id, show.StartUtc), AnalysisPaths.ShowAnalysisFile), doc);
            results.Add(doc);
            log.LogInformation("Show {Show} ({Title}) analysed: {Complete}", show.Id, show.Title, complete ? "complete" : "partial");
        }

        return results;
    }
}
