using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Analysis;

/// <summary>Audio at <see cref="AudioOffset"/> seconds into the segment file was broadcast at <see cref="Utc"/>; time advances 1:1 until the next anchor.</summary>
public sealed record TimelineAnchor(double AudioOffset, DateTimeOffset Utc);

/// <summary>
/// Maps seconds into a segment's audio to UTC and back, with the same model as the web app's position model: a segment
/// is one connection per stretch between its gaps; each connection starts with a short burst of audio from before its
/// first arrival (Revma sends about 5 s at connect) and then runs in real time. The measured surplus
/// (AudioSeconds minus the wall span less gaps) is shared equally between connections, so after a reconnect inside a
/// segment a time can be a few seconds out; otherwise it is good to about a second.
/// </summary>
public static class SegmentTimeline
{
    public static IReadOnlyList<TimelineAnchor> Anchors(SegmentMeta meta)
    {
        var start = meta.StartUtc;
        var end = meta.EndUtc < start ? start : meta.EndUtc;
        var gaps = (meta.Gaps ?? [])
            .Where(g => g.EndUtc > start && g.StartUtc < end)
            .OrderBy(g => g.StartUtc)
            .Select(g => (Start: g.StartUtc < start ? start : g.StartUtc, End: g.EndUtc > end ? end : g.EndUtc))
            .ToList();
        var gapSeconds = gaps.Sum(g => (g.End - g.Start).TotalSeconds);
        var burst = Math.Max(0, meta.AudioSeconds - ((end - start).TotalSeconds - gapSeconds));
        var lead = burst / (gaps.Count + 1);

        var anchors = new List<TimelineAnchor>(gaps.Count + 1);
        double offset = 0;
        var connectionStart = start;
        for (var i = 0; i <= gaps.Count; i++)
        {
            var connectionEnd = i < gaps.Count ? gaps[i].Start : end;
            if (connectionEnd < connectionStart) connectionEnd = connectionStart;
            anchors.Add(new TimelineAnchor(offset, connectionStart.AddSeconds(-lead)));
            offset += (connectionEnd - connectionStart).TotalSeconds + lead;
            if (i < gaps.Count && gaps[i].End > connectionStart) connectionStart = gaps[i].End;
        }

        return anchors;
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
