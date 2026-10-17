namespace WebAudioArchive.Web.Clips;

/// <summary>A stretch of the clip's window with no audio from the chosen recorder. Times UTC.</summary>
public sealed record ClipGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)
{
    public double Seconds => (EndUtc - StartUtc).TotalSeconds;
}

/// <summary>A segment inside the window that ended for a reason other than the hour boundary (a reconnect, a stop, a crash).</summary>
public sealed record ClipRestart(DateTimeOffset AtUtc, string EndedBy);

/// <summary>
/// The gaps and recorder restarts inside a clip's window, as data, for the API and the player's markers. The same facts
/// the planner puts into its warnings as text.
/// </summary>
public static class ClipEvents
{
    /// <summary>Missing stretches shorter than this are frame rounding or a roll at the hour, not gaps.</summary>
    public const double MinGapSeconds = 1.5;

    public static IReadOnlyList<ClipGap> Gaps(ClipPlan plan)
    {
        // A joined clip knows exactly what neither recorder holds; one recorder's own gaps were filled by the other.
        if (plan.StitchedGaps is not null) return plan.StitchedGaps;

        var from = plan.WindowFrom;
        var to = plan.WindowTo;
        var gaps = new List<ClipGap>();
        var pieces = plan.Pieces;

        if ((pieces[0].StartUtc - from).TotalSeconds > MinGapSeconds)
            gaps.Add(new ClipGap(from, pieces[0].StartUtc, "not recorded"));

        for (var i = 1; i < pieces.Count; i++)
        {
            if ((pieces[i].StartUtc - pieces[i - 1].EndUtc).TotalSeconds > MinGapSeconds)
                gaps.Add(new ClipGap(pieces[i - 1].EndUtc, pieces[i].StartUtc, "no audio between segments"));
        }

        foreach (var g in pieces.Select(p => p.Segment).Distinct().SelectMany(s => s.SpanGaps))
        {
            if (g.EndUtc <= from || g.StartUtc >= to) continue;
            gaps.Add(new ClipGap(g.StartUtc < from ? from : g.StartUtc, g.EndUtc > to ? to : g.EndUtc, g.Reason));
        }

        if ((to - pieces[^1].EndUtc).TotalSeconds > MinGapSeconds)
            gaps.Add(new ClipGap(pieces[^1].EndUtc, to, "not recorded"));

        return gaps.OrderBy(g => g.StartUtc).ToList();
    }

    /// <summary>Restarts of the recorder used; none for a clip joined from two recorders, where the other runs across them.</summary>
    public static IReadOnlyList<ClipRestart> Restarts(ClipPlan plan) => plan.Recorder == ClipPlan.Joined ? [] :
        plan.Pieces.Select(p => p.Segment).Distinct()
            .Where(s => s.EndedBy is not null and not "hour-boundary" && s.EndUtc > plan.WindowFrom && s.EndUtc < plan.WindowTo)
            .Select(s => new ClipRestart(s.EndUtc, s.EndedBy!))
            .OrderBy(r => r.AtUtc)
            .ToList();
}

/// <summary>
/// Maps between UTC wall time and seconds into a clip, using the clip's timeline (where each joined piece starts and
/// where the audio jumps across a gap). Without jumps this is just utc minus the clip's first audio. A time inside a
/// gap maps to the point where the audio jumps over it.
/// </summary>
public sealed class ClipClock(IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> timeline, double seconds)
{
    public double Seconds => seconds;

    public TimeSpan ToClip(DateTimeOffset utc)
    {
        if (timeline.Count == 0) return TimeSpan.Zero;
        if (utc <= timeline[0].Utc) return TimeSpan.Zero;
        var i = 0;
        for (var k = 0; k < timeline.Count; k++)
            if (timeline[k].Utc <= utc) i = k;

        var at = timeline[i].ClipSecond + (utc - timeline[i].Utc).TotalSeconds;
        if (i + 1 < timeline.Count) at = Math.Min(at, timeline[i + 1].ClipSecond);
        return TimeSpan.FromSeconds(Math.Clamp(at, 0, seconds));
    }

    public DateTimeOffset? ToUtc(TimeSpan position)
    {
        if (timeline.Count == 0) return null;
        var t = position.TotalSeconds;
        var p = timeline[0];
        foreach (var entry in timeline)
            if (entry.ClipSecond <= t) p = entry;
        return p.Utc.AddTicks((long)Math.Round((t - p.ClipSecond) * TimeSpan.TicksPerSecond));
    }
}
