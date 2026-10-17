using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Schedule;

namespace WebAudioArchive.Analysis.Shows;

public static class ShowAnalysisBuilder
{
    public static ShowAnalysis Build(ShowOccurrence show, TimeSpan margin, IReadOnlyList<AnalysisEvent> events, IReadOnlyList<TimedLine> lines,
        IReadOnlyList<ShowSource> sources, bool complete, DateTimeOffset now)
    {
        var inShow = events.Where(e => e.StartUtc >= show.StartUtc && e.StartUtc < show.EndUtc).ToList();
        var allSwears = inShow.Where(e => e.Type == EventTypes.Swear).ToList();
        // Suppressed runs are review-only (probable hallucination): kept out of every count and score, only tallied.
        static bool IsSuppressed(AnalysisEvent e) => e.Evidence?.GetValueOrDefault("suppressed") is { } s && s != "no";
        var swears = allSwears.Where(e => !IsSuppressed(e)).ToList();
        int Tier(string tier) => swears.Count(e => e.Evidence?.GetValueOrDefault("tier") == tier);
        var clipping = inShow.Where(e => e.Type == EventTypes.Clipping).ToList();
        var deadAir = inShow.Where(e => e.Type == EventTypes.DeadAir).ToList();
        var fillers = inShow.Count(e => e.Type == EventTypes.Filler);
        var speechMinutes = lines.Sum(l => Overlap(l.StartUtc, l.EndUtc, show.StartUtc, show.EndUtc)) / 60;
        var analysed = Union(sources.Select(s => (s.FromUtc, s.ToUtc)), show.StartUtc, show.EndUtc);

        var notes = new List<string> { "Counts are detections from speech to text and audio levels, not proven events; check each against the audio." };
        notes.Add(speechMinutes >= 1 ? "The filler rate is an estimate (whisper leaves out some fillers)." : "Less than a minute of speech was transcribed, so no filler rate is given.");
        if (analysed < show.Duration.TotalSeconds * 0.98)
            notes.Add($"Only {analysed / 60:0.#} of {show.Duration.TotalMinutes:0} minutes were analysed; the rest was not recorded or not analysed yet.");
        if (allSwears.Any(IsSuppressed)) notes.Add($"{allSwears.Count(IsSuppressed)} possible swear run(s) in text that looks invented (repeats or known hallucinations) were set aside for review and are not counted.");
        if (!complete) notes.Add("Analysis of this show is not finished; figures will change.");

        var counts = new ShowCounts(
            Tier(SwearTiers.Mild), Tier(SwearTiers.Strong), Tier(SwearTiers.Severe),
            swears.Count(e => e.Evidence?.GetValueOrDefault("beforeWatershed") == "true"),
            clipping.Count, Math.Round(clipping.Sum(e => (e.EndUtc - e.StartUtc).TotalMilliseconds), 3),
            deadAir.Count, Math.Round(deadAir.Sum(e => (e.EndUtc - e.StartUtc).TotalSeconds), 3),
            fillers, Math.Round(speechMinutes, 6), speechMinutes >= 1 ? Math.Round(fillers / speechMinutes, 6) : null,
            Math.Round(analysed, 3), allSwears.Count(IsSuppressed));

        return new ShowAnalysis(ShowAnalysis.CurrentSchema, show.Id, show.Station, show.Title, show.StartUtc, show.EndUtc,
            show.StartUtc - margin, show.EndUtc + margin, now, complete, sources, counts, notes);
    }

    static double Overlap(DateTimeOffset a0, DateTimeOffset a1, DateTimeOffset b0, DateTimeOffset b1)
    {
        var from = a0 > b0 ? a0 : b0;
        var to = a1 < b1 ? a1 : b1;
        return to > from ? (to - from).TotalSeconds : 0;
    }

    static double Union(IEnumerable<(DateTimeOffset From, DateTimeOffset To)> spans, DateTimeOffset from, DateTimeOffset to)
    {
        double total = 0;
        DateTimeOffset? runFrom = null, runTo = null;
        foreach (var s in spans.Select(s => (From: s.From < from ? from : s.From, To: s.To > to ? to : s.To)).Where(s => s.To > s.From).OrderBy(s => s.From))
        {
            if (runTo is null || s.From > runTo)
            {
                if (runTo is not null) total += (runTo.Value - runFrom!.Value).TotalSeconds;
                (runFrom, runTo) = (s.From, s.To);
            }
            else if (s.To > runTo)
            {
                runTo = s.To;
            }
        }

        if (runTo is not null) total += (runTo.Value - runFrom!.Value).TotalSeconds;
        return total;
    }
}
