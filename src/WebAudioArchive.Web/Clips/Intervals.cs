namespace WebAudioArchive.Web.Clips;

public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => End > Start ? (End - Start).TotalSeconds : 0;
}

/// <summary>What all recorders together hold of a window: the seconds covered and the stretches that none of them has, UTC.</summary>
public sealed record CombinedCoverage(double CoveredSeconds, double WindowSeconds, IReadOnlyList<Interval> Missing)
{
    public double Fraction => WindowSeconds <= 0 ? 0 : Math.Min(1, CoveredSeconds / WindowSeconds);
}

/// <summary>
/// Coverage arithmetic: segment spans minus sidecar gaps, merged so overlaps are not counted twice. A span starts at the
/// segment's first audio (its start minus the Revma burst), because that audio is really there.
/// </summary>
public static class WindowCoverage
{
    public static List<Interval> Covered(IEnumerable<CatalogSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        var spans = new List<Interval>();
        foreach (var s in segments)
        {
            var cursor = Max(PositionModel.AudioStartUtc(s), from);
            var end = Min(s.EndUtc, to);
            foreach (var g in s.SpanGaps)
            {
                if (g.StartUtc > cursor) spans.Add(new Interval(cursor, Min(g.StartUtc, end)));
                cursor = Max(cursor, g.EndUtc);
            }

            if (end > cursor) spans.Add(new Interval(cursor, end));
        }

        return Merge(spans.Where(i => i.End > i.Start));
    }

    public static double Seconds(IEnumerable<CatalogSegment> segments, DateTimeOffset from, DateTimeOffset to) =>
        Covered(segments, from, to).Sum(i => i.Seconds);

    /// <summary>
    /// The union of every recorder's coverage of [from, to): a stretch counts when any recorder has it. Missing lists what
    /// none has, leaving out stretches shorter than <paramref name="minMissingSeconds"/>.
    /// </summary>
    public static CombinedCoverage Combined(IEnumerable<RecorderSegments> recorders, DateTimeOffset from, DateTimeOffset to, double minMissingSeconds)
    {
        var covered = Merge(recorders.SelectMany(r =>
            Covered(r.Segments.Where(s => s.EndUtc > from && PositionModel.AudioStartUtc(s) < to), from, to)));
        return new CombinedCoverage(covered.Sum(i => i.Seconds), to > from ? (to - from).TotalSeconds : 0, Uncovered(covered, from, to, minMissingSeconds));
    }

    /// <summary>The stretches of [from, to) not in <paramref name="covered"/>, leaving out ones shorter than <paramref name="minSeconds"/>.</summary>
    public static IReadOnlyList<Interval> Uncovered(IReadOnlyList<Interval> covered, DateTimeOffset from, DateTimeOffset to, double minSeconds)
    {
        var gaps = new List<Interval>();
        var cursor = from;
        foreach (var c in covered.OrderBy(c => c.Start))
        {
            if (c.Start > cursor) gaps.Add(new Interval(cursor, Min(c.Start, to)));
            if (c.End > cursor) cursor = c.End;
        }

        if (to > cursor) gaps.Add(new Interval(cursor, to));
        return gaps.Where(g => g.Seconds >= minSeconds).ToList();
    }

    public static List<Interval> Merge(IEnumerable<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (var i in intervals.OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Max(merged[^1].End, i.End) };
            else
                merged.Add(i);
        }

        return merged;
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
