using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Analysis;

/// <summary>Audio at <see cref="AudioOffset"/> seconds into the segment file was broadcast at <see cref="Utc"/>; time advances 1:1 until the next anchor.</summary>
public sealed record TimelineAnchor(double AudioOffset, DateTimeOffset Utc);

/// <summary>
/// Maps seconds into a segment's audio to UTC and back. This is the one position model: the web app's clip positions
/// (PositionModel) and analysis event times both use it. A segment is one connection per stretch between its gaps;
/// each connection runs in real time.
/// <list type="bullet">
/// <item>More audio than wall time (AudioSeconds above the span less gaps): each connection started with a burst of
/// audio from before its first arrival (Revma sends about 5 s at connect); the surplus is shared equally between
/// connections, so after a reconnect inside a segment a time can be a few seconds out.</item>
/// <item>Less audio than wall time, with gaps (issue #106): the stream stalled before a connection dropped, so the
/// sidecar's gap starts after the audio really stopped. The shortfall is taken from the ends of the connections that
/// end at a gap, shared equally and never more than a connection holds, so the hour still ends at its end. Measured on
/// 2026-10-16: 36 s short, which this model used to put at the end of the hour.</item>
/// <item>Less audio with no gap: nothing says where it went, so it stays at the end.</item>
/// </list>
/// </summary>
public static class SegmentTimeline
{
    /// <summary>Surplus or shortfall smaller than this is frame rounding.</summary>
    public const double RoundingSeconds = 0.05;

    public static IReadOnlyList<TimelineAnchor> Anchors(SegmentMeta meta) => Anchors(meta.StartUtc, meta.EndUtc, meta.Gaps ?? [], meta.AudioSeconds);

    public static IReadOnlyList<TimelineAnchor> Anchors(DateTimeOffset start, DateTimeOffset end, IEnumerable<SegmentGap> allGaps, double audioSeconds)
    {
        if (end < start) end = start;
        var gaps = allGaps
            .Where(g => g.EndUtc > start && g.StartUtc < end)
            .OrderBy(g => g.StartUtc)
            .Select(g => (Start: g.StartUtc < start ? start : g.StartUtc, End: g.EndUtc > end ? end : g.EndUtc))
            .ToList();

        var connections = new List<(DateTimeOffset Start, double Seconds)>(gaps.Count + 1);
        var connectionStart = start;
        for (var i = 0; i <= gaps.Count; i++)
        {
            var connectionEnd = i < gaps.Count ? gaps[i].Start : end;
            if (connectionEnd < connectionStart) connectionEnd = connectionStart;
            connections.Add((connectionStart, (connectionEnd - connectionStart).TotalSeconds));
            if (i < gaps.Count && gaps[i].End > connectionStart) connectionStart = gaps[i].End;
        }

        var surplus = audioSeconds - connections.Sum(c => c.Seconds);
        // Audio comes in whole frames (26 ms at 44.1 kHz) and wall times do not, so a difference under a couple of
        // frames is rounding, not a burst or a stall; ignoring it keeps the recorded gap times exact.
        if (Math.Abs(surplus) < RoundingSeconds) surplus = 0;
        var lead = surplus > 0 ? surplus / connections.Count : 0;
        var cut = Shortfall(connections, gaps.Count, surplus < 0 ? -surplus : 0);

        var anchors = new List<TimelineAnchor>(connections.Count);
        double offset = 0;
        for (var i = 0; i < connections.Count; i++)
        {
            anchors.Add(new TimelineAnchor(offset, connections[i].Start.AddSeconds(-lead)));
            offset += connections[i].Seconds - cut[i] + lead;
        }

        return anchors;
    }

    /// <summary>Seconds to take from the end of each connection that ends at a gap: equal shares, capped at its length.</summary>
    static double[] Shortfall(List<(DateTimeOffset Start, double Seconds)> connections, int endingAtGap, double missing)
    {
        var cut = new double[connections.Count];
        var open = Enumerable.Range(0, endingAtGap).ToList();
        while (missing > 1e-9 && open.Count > 0)
        {
            var share = missing / open.Count;
            foreach (var i in open.ToList())
            {
                var take = Math.Min(share, connections[i].Seconds - cut[i]);
                cut[i] += take;
                missing -= take;
                if (connections[i].Seconds - cut[i] <= 1e-9) open.Remove(i);
            }
        }

        return cut;
    }

    public static DateTimeOffset ToUtc(IReadOnlyList<TimelineAnchor> anchors, double audioOffset)
    {
        var a = anchors[0];
        foreach (var c in anchors)
            if (c.AudioOffset <= audioOffset) a = c;
        return a.Utc.AddTicks((long)Math.Round((audioOffset - a.AudioOffset) * TimeSpan.TicksPerSecond));
    }

    /// <summary>Seconds into the audio for a UTC time; a time inside a gap maps to where the audio jumps over it.</summary>
    public static double ToOffset(IReadOnlyList<TimelineAnchor> anchors, DateTimeOffset utc)
    {
        var index = 0;
        for (var i = 0; i < anchors.Count; i++)
            if (anchors[i].Utc <= utc) index = i;
        var a = anchors[index];
        var offset = a.AudioOffset + (utc - a.Utc).TotalSeconds;
        if (index + 1 < anchors.Count) offset = Math.Min(offset, anchors[index + 1].AudioOffset);
        return Math.Max(0, offset);
    }

    /// <summary>The UTC stretches the audio covers, one per connection (gaps excluded).</summary>
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Pieces(IReadOnlyList<TimelineAnchor> anchors, double audioSeconds)
    {
        var list = new List<(DateTimeOffset, DateTimeOffset)>(anchors.Count);
        for (var i = 0; i < anchors.Count; i++)
        {
            var endOffset = i + 1 < anchors.Count ? anchors[i + 1].AudioOffset : Math.Max(anchors[i].AudioOffset, audioSeconds);
            list.Add((anchors[i].Utc, anchors[i].Utc.AddSeconds(endOffset - anchors[i].AudioOffset)));
        }

        return list;
    }
}
