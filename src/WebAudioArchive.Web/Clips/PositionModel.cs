namespace WebAudioArchive.Web.Clips;

/// <summary>
/// Maps UTC wall time to a byte offset inside a segment and back.
///
/// A segment is one or more connections (split by the gaps in its sidecar). Each connection starts with a short burst
/// of audio from before its first arrival (Revma sends about 5 s of buffered audio at connect) and then advances at the
/// stream bitrate (CBR: BitrateKbps * 125 bytes per second, 16000 for 128 kbps). With no gaps the burst is
/// AudioSeconds - (EndUtc - StartUtc), so the byte holding wall time T is (T - StartUtc + burst) * bytesPerSecond.
/// With gaps the measured surplus is shared equally between the connections, which is an approximation: after a
/// reconnect inside a segment a position can be off by a few seconds. Otherwise positions are good to about a second;
/// the cutter then snaps them to real MP3 frame boundaries.
/// </summary>
public static class PositionModel
{
    readonly record struct Connection(DateTimeOffset Start, DateTimeOffset End, double LeadSeconds, double BaseByte);

    static List<Connection> Connections(CatalogSegment s)
    {
        var gaps = s.SpanGaps;
        var lead = s.BurstSeconds / (gaps.Count + 1);
        var bps = (double)s.BytesPerSecond;
        var list = new List<Connection>(gaps.Count + 1);
        var start = s.StartUtc;
        double baseByte = 0;
        foreach (var g in gaps.Append(null))
        {
            var end = g?.StartUtc ?? s.EndUtc;
            if (end < start) end = start;
            list.Add(new Connection(start, end, lead, baseByte));
            baseByte += ((end - start).TotalSeconds + lead) * bps;
            if (g is not null) start = g.EndUtc > start ? g.EndUtc : start;
        }

        return list;
    }

    /// <summary>When the audio at byte 0 was broadcast: the first arrival minus the burst.</summary>
    public static DateTimeOffset AudioStartUtc(CatalogSegment s) => TimeAt(s, 0);

    /// <summary>Estimated byte offset (not yet frame aligned) of wall time <paramref name="t"/>, clamped to the file.</summary>
    public static long Offset(CatalogSegment s, DateTimeOffset t)
    {
        var bps = (double)s.BytesPerSecond;
        var conns = Connections(s);
        var c = conns[0];
        foreach (var candidate in conns)
            if (candidate.Start.AddSeconds(-candidate.LeadSeconds) <= t) c = candidate;

        var span = (c.End - c.Start).TotalSeconds + c.LeadSeconds;
        var into = Math.Clamp((t - c.Start).TotalSeconds + c.LeadSeconds, 0, span);
        var offset = (long)Math.Round(c.BaseByte + into * bps);
        return Math.Clamp(offset, 0, s.Bytes);
    }

    /// <summary>Wall time of the audio at byte <paramref name="offset"/>.</summary>
    public static DateTimeOffset TimeAt(CatalogSegment s, long offset)
    {
        var bps = (double)s.BytesPerSecond;
        var conns = Connections(s);
        var c = conns[0];
        foreach (var candidate in conns)
            if (candidate.BaseByte <= offset) c = candidate;

        var seconds = (offset - c.BaseByte) / bps - c.LeadSeconds;
        return c.Start.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }
}
