using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Work;

/// <summary>A segment to analyse. FillIn is true for a less preferred recorder's segment queued because the preferred one has a hole there.</summary>
public sealed record SegmentJob(SourceSegment Source, bool FillIn);

/// <summary>
/// D7 and D8: every segment of the first recorder in the preference is a job; another recorder's segment is a job only
/// when better recorders cover less than 98% of its span (spans minus gaps). Jobs run newest first.
/// </summary>
public static class WorkPlanner
{
    public const double MinPreferredCover = 0.98;

    public static IReadOnlyList<SegmentJob> Plan(IReadOnlyList<SourceSegment> segments, IReadOnlyList<string> preference, DateTimeOffset? since)
    {
        var eligible = segments.Where(s => since is null || s.Meta.EndUtc > since.Value).ToList();
        var byHour = eligible.ToLookup(s => Hour(s.Meta.StartUtc));
        var jobs = new List<SegmentJob>();
        foreach (var s in eligible)
        {
            var rank = RecorderPreference.Rank(s.Recorder, preference);
            if (rank == 0)
            {
                jobs.Add(new SegmentJob(s, false));
                continue;
            }

            var candidates = new List<SourceSegment>();
            for (var h = Hour(s.Meta.StartUtc) - 1; h <= Hour(s.Meta.EndUtc); h++)
                candidates.AddRange(byHour[h].Where(o => RecorderPreference.Rank(o.Recorder, preference) < rank));
            if (CoveredFraction(s, candidates) < MinPreferredCover) jobs.Add(new SegmentJob(s, true));
        }

        return jobs
            .OrderByDescending(j => j.Source.Meta.StartUtc)
            .ThenBy(j => RecorderPreference.Rank(j.Source.Recorder, preference))
            .ToList();
    }

    /// <summary>The fraction of <paramref name="s"/>'s wall span that the others' spans, less their gaps, cover (overlaps counted once).</summary>
    public static double CoveredFraction(SourceSegment s, IEnumerable<SourceSegment> others)
    {
        var from = s.Meta.StartUtc;
        var to = s.Meta.EndUtc;
        var span = (to - from).TotalSeconds;
        if (span <= 0) return 1;

        var pieces = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var o in others)
        {
            if (o.Meta.EndUtc <= from || o.Meta.StartUtc >= to) continue;
            var cursor = o.Meta.StartUtc;
            foreach (var g in (o.Meta.Gaps ?? []).OrderBy(g => g.StartUtc))
            {
                if (g.StartUtc > cursor) pieces.Add((cursor, g.StartUtc < o.Meta.EndUtc ? g.StartUtc : o.Meta.EndUtc));
                if (g.EndUtc > cursor) cursor = g.EndUtc;
            }

            if (o.Meta.EndUtc > cursor) pieces.Add((cursor, o.Meta.EndUtc));
        }

        double covered = 0;
        DateTimeOffset? runFrom = null, runTo = null;
        foreach (var p in pieces
                     .Select(p => (From: p.From < from ? from : p.From, To: p.To > to ? to : p.To))
                     .Where(p => p.To > p.From)
                     .OrderBy(p => p.From))
        {
            if (runTo is null || p.From > runTo)
            {
                if (runTo is not null) covered += (runTo.Value - runFrom!.Value).TotalSeconds;
                (runFrom, runTo) = (p.From, p.To);
            }
            else if (p.To > runTo)
            {
                runTo = p.To;
            }
        }

        if (runTo is not null) covered += (runTo.Value - runFrom!.Value).TotalSeconds;
        return covered / span;
    }

    static long Hour(DateTimeOffset t) => t.UtcTicks / TimeSpan.TicksPerHour;
}
