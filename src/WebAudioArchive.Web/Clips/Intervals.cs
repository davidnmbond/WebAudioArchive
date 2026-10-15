namespace WebAudioArchive.Web.Clips;

public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => End > Start ? (End - Start).TotalSeconds : 0;
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
