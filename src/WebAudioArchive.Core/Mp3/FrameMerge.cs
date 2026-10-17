namespace WebAudioArchive.Core.Mp3;

/// <summary>A frame as the merge sees it: its identity (a hash of its bytes) and its estimated UTC from its recorder.</summary>
public readonly record struct MergeFrame(ulong Hash, DateTimeOffset EstUtc);

public enum FrameSource
{
    Primary,
    Secondary,
}

/// <summary>Frames [Start, Start + Count) of one side's list, in stream order.</summary>
public sealed record FrameBlock(FrameSource Source, int Start, int Count);

/// <summary>A stretch neither side holds, on the primary's clock.</summary>
public sealed record MissingSpan(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => (End - Start).TotalSeconds;
}

public sealed record FrameMergeResult(IReadOnlyList<FrameBlock> Blocks, IReadOnlyList<MissingSpan> Missing);

/// <summary>
/// Merges two recorders' frames of the same stream into the stream as captured: every frame either holds, once, in order.
/// Recorders A and B receive byte-identical frames, offset by however many frames apart they connected, cut an hour or
/// reconnected (measured 2026-10-16), so frames are matched by their bytes, not by clock. Matching runs are anchored on
/// <see cref="AnchorFrames"/> consecutive frames because single frames repeat (silence). Estimated times only narrow the
/// search, order two stretches that each side alone holds, and size what neither holds. No audio is decoded.
/// </summary>
public static class FrameMerge
{
    public const int AnchorFrames = 8;

    /// <summary>Matching frames whose estimated times differ by more than this are not the same moment.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>Within one recorder, adjacent frames further apart than a frame plus this have a gap between them.</summary>
    public const double JumpSeconds = 0.5;

    public const double Mpeg1FrameSeconds = 1152.0 / 44100;

    readonly record struct Run(int P, int S, int Count, double Delta);

    /// <summary>One emitted frame: its index in either side's list (null when that side does not hold it).</summary>
    readonly record struct Emitted(int? P, int? S, double Delta);

    public static FrameMergeResult Merge(IReadOnlyList<MergeFrame> primary, IReadOnlyList<MergeFrame> secondary, double frameSeconds = Mpeg1FrameSeconds)
    {
        var runs = Chain(primary, secondary);
        var emitted = new List<Emitted>(primary.Count + secondary.Count / 8);
        var prevP = 0;
        var prevS = 0;
        var prevDelta = runs.Count > 0 ? runs[0].Delta : 0;
        foreach (var r in runs.Append(new Run(primary.Count, secondary.Count, 0, runs.Count > 0 ? runs[^1].Delta : 0)))
        {
            Junction(primary, secondary, prevP, r.P, prevS, r.S, prevDelta, r.Delta, frameSeconds, emitted);
            for (var k = 0; k < r.Count; k++) emitted.Add(new Emitted(r.P + k, r.S + k, r.Delta));
            prevP = r.P + r.Count;
            prevS = r.S + r.Count;
            if (r.Count > 0) prevDelta = r.Delta;
        }

        var missing = Missing(primary, secondary, emitted, frameSeconds);
        missing.AddRange(runs.SelectMany(r => SharedBreaks(primary, secondary, r, frameSeconds)));
        missing.Sort((x, y) => x.Start.CompareTo(y.Start));
        return new FrameMergeResult(Blocks(emitted), missing);
    }

    /// <summary>
    /// Breaks inside a matched run: both recorders lost the same frames, so they hold the frames either side as
    /// neighbours. Each recorder's estimated times then run ahead of its frame count by the length of the break, though
    /// each may show the jump a little earlier or later than the true break. Time missing from both is the smaller of
    /// the two excesses, placed where both have jumped (within a second or so of the true break).
    /// </summary>
    static IEnumerable<MissingSpan> SharedBreaks(IReadOnlyList<MergeFrame> p, IReadOnlyList<MergeFrame> s, Run r, double frameSeconds)
    {
        double reported = 0;
        for (var k = 1; k < r.Count; k++)
        {
            var excessP = (p[r.P + k].EstUtc - p[r.P].EstUtc).TotalSeconds - k * frameSeconds;
            var excessS = (s[r.S + k].EstUtc - s[r.S].EstUtc).TotalSeconds - k * frameSeconds;
            var shared = Math.Min(excessP, excessS);
            if (shared - reported <= JumpSeconds) continue;

            // Frame k is after the break on both clocks; the missing stretch ends where its audio begins.
            var end = p[r.P + k].EstUtc;
            yield return new MissingSpan(end.AddSeconds(-(shared - reported)), end);
            reported = shared;
        }
    }

    /// <summary>Matching runs, increasing in both lists, each anchored on <see cref="AnchorFrames"/> equal frames.</summary>
    static List<Run> Chain(IReadOnlyList<MergeFrame> p, IReadOnlyList<MergeFrame> s)
    {
        var runs = new List<Run>();
        if (p.Count < AnchorFrames || s.Count < AnchorFrames) return runs;

        var index = new Dictionary<ulong, List<int>>();
        for (var j = 0; j + AnchorFrames <= s.Count; j++)
        {
            var key = Key(s, j);
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(j);
        }

        int lastP = 0, lastS = 0;
        int? offset = null;
        double? delta = null;
        var i = 0;
        while (i + AnchorFrames <= p.Count)
        {
            if (!index.TryGetValue(Key(p, i), out var candidates)) { i++; continue; }

            var best = -1;
            var bestScore = double.MaxValue;
            foreach (var j in candidates)
            {
                if (j < lastS) continue;
                var skew = (s[j].EstUtc - p[i].EstUtc).TotalSeconds;
                if (Math.Abs(skew - (delta ?? 0)) > MaxClockSkew.TotalSeconds + (delta is null ? 0 : JumpSeconds)) continue;
                var score = j - i == offset ? -1 : Math.Abs(skew - (delta ?? skew));
                if (score < bestScore) { best = j; bestScore = score; }
            }

            if (best < 0) { i++; continue; }

            var count = 0;
            while (i + count < p.Count && best + count < s.Count && p[i + count].Hash == s[best + count].Hash) count++;
            if (count < AnchorFrames) { i++; continue; }

            var start = i;
            var sStart = best;
            while (start > lastP && sStart > lastS && p[start - 1].Hash == s[sStart - 1].Hash) { start--; sStart--; count++; }

            var d = (s[sStart].EstUtc - p[start].EstUtc).TotalSeconds;
            runs.Add(new Run(start, sStart, count, d));
            lastP = start + count;
            lastS = sStart + count;
            offset = sStart - start;
            delta = d;
            i = lastP;
        }

        return runs;
    }

    static ulong Key(IReadOnlyList<MergeFrame> frames, int at)
    {
        var h = 14695981039346656037UL;
        for (var k = 0; k < AnchorFrames; k++) h = (h ^ frames[at + k].Hash) * 1099511628211UL;
        return h;
    }

    /// <summary>
    /// The frames between two runs (or before the first, or after the last): [p0, p1) only the primary holds and
    /// [s0, s1) only the secondary holds. A side's frames that repeat its own frames just before the join are a replay
    /// (a server resends its buffer on reconnect) and are dropped; the rest is audio the other side lacks. Estimated
    /// times are not used to decide this: at a break they can be off by a second or more (measured with the position
    /// model), which would turn real audio into a "replay". When both sides hold something, the two stretches go in the
    /// order of their times.
    /// </summary>
    static void Junction(
        IReadOnlyList<MergeFrame> p, IReadOnlyList<MergeFrame> s, int p0, int p1, int s0, int s1,
        double deltaBefore, double deltaAfter, double frameSeconds, List<Emitted> emitted)
    {
        var pCount = p1 - p0;
        var sCount = s1 - s0;
        if (pCount == 0 && sCount == 0) return;

        var dropP = Replay(p, p0, pCount);
        var dropS = Replay(s, s0, sCount);
        p0 += dropP; pCount -= dropP;
        s0 += dropS; sCount -= dropS;

        var delta = s0 == 0 ? deltaAfter : deltaBefore;
        var pFirst = pCount > 0 ? p[p0].EstUtc.AddSeconds(0) : DateTimeOffset.MaxValue;
        var sFirst = sCount > 0 ? s[s0].EstUtc.AddSeconds(-delta) : DateTimeOffset.MaxValue;
        var secondaryFirst = sFirst < pFirst;

        void EmitP() { for (var k = 0; k < pCount; k++) emitted.Add(new Emitted(p0 + k, null, delta)); }
        void EmitS() { for (var k = 0; k < sCount; k++) emitted.Add(new Emitted(null, s0 + k, delta)); }

        if (secondaryFirst) { EmitS(); EmitP(); }
        else { EmitP(); EmitS(); }
    }

    static bool Continuous(IReadOnlyList<MergeFrame> f, int a, int b, double frameSeconds) =>
        (f[b].EstUtc - f[a].EstUtc).TotalSeconds - frameSeconds <= JumpSeconds;

    /// <summary>
    /// How many of the frames at [start, start + count) repeat the frames just before start (a server replays its
    /// buffer on reconnect). A run of one repeated frame (silence) is never treated as a replay.
    /// </summary>
    static int Replay(IReadOnlyList<MergeFrame> f, int start, int count)
    {
        for (var m = Math.Min(count, start); m >= 2; m--)
        {
            var same = true;
            var varied = false;
            for (var k = 0; k < m && same; k++)
            {
                same = f[start + k].Hash == f[start - m + k].Hash;
                varied |= f[start + k].Hash != f[start].Hash;
            }

            if (same && varied) return m;
        }

        return 0;
    }

    static List<FrameBlock> Blocks(List<Emitted> emitted)
    {
        var blocks = new List<FrameBlock>();
        foreach (var e in emitted)
        {
            var source = e.P is not null ? FrameSource.Primary : FrameSource.Secondary;
            var index = e.P ?? e.S!.Value;
            if (blocks.Count > 0 && blocks[^1].Source == source && blocks[^1].Start + blocks[^1].Count == index)
                blocks[^1] = blocks[^1] with { Count = blocks[^1].Count + 1 };
            else
                blocks.Add(new FrameBlock(source, index, 1));
        }

        return blocks;
    }

    /// <summary>
    /// Between two emitted frames something is missing when neither side holds them as neighbours with no jump. Times
    /// are on the primary's clock (the secondary's shifted by the nearest run's measured difference).
    /// </summary>
    static List<MissingSpan> Missing(IReadOnlyList<MergeFrame> p, IReadOnlyList<MergeFrame> s, List<Emitted> emitted, double frameSeconds)
    {
        var missing = new List<MissingSpan>();
        for (var k = 1; k < emitted.Count; k++)
        {
            var x = emitted[k - 1];
            var y = emitted[k];
            var adjacentP = x.P is not null && y.P == x.P + 1;
            var adjacentS = x.S is not null && y.S == x.S + 1;
            if (adjacentP && adjacentS) continue; // inside a matched run: see SharedBreaks
            if ((adjacentP && Continuous(p, x.P!.Value, y.P!.Value, frameSeconds)) || (adjacentS && Continuous(s, x.S!.Value, y.S!.Value, frameSeconds)))
                continue;

            var start = Clock(p, s, x).AddSeconds(frameSeconds);
            var end = adjacentP ? start + (p[y.P!.Value].EstUtc - p[x.P!.Value].EstUtc).Add(TimeSpan.FromSeconds(-frameSeconds))
                : adjacentS ? start + (s[y.S!.Value].EstUtc - s[x.S!.Value].EstUtc).Add(TimeSpan.FromSeconds(-frameSeconds))
                : Clock(p, s, y);
            if ((end - start).TotalSeconds > JumpSeconds) missing.Add(new MissingSpan(start, end));
        }

        return missing;
    }

    static DateTimeOffset Clock(IReadOnlyList<MergeFrame> p, IReadOnlyList<MergeFrame> s, Emitted e) =>
        e.P is not null ? p[e.P.Value].EstUtc : s[e.S!.Value].EstUtc.AddSeconds(-e.Delta);
}
