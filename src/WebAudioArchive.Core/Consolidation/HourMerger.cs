using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>
/// Frames [FirstFrame, FirstFrame + Frames) of one source file, bytes [StartByte, EndByte): one run of a merged hour.
/// FirstFrame counts whole frames from the start of the file. Times are estimated on the source's own clock.
/// </summary>
public sealed record MergedPiece(
    string Recorder, SourceSegment Source, long StartByte, long EndByte, int FirstFrame, int Frames, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// What a merged hour is made of: its pieces in stream order, the stretches neither recorder holds (reason
/// <see cref="HourMerger.MissingReason"/>) and the hash of every frame in order, which the written file must match.
/// </summary>
public sealed record MergedHourPlan(DateTimeOffset Hour, IReadOnlyList<MergedPiece> Pieces, IReadOnlyList<SegmentGap> Missing, IReadOnlyList<ulong> FrameHashes);

/// <summary>
/// Plans one merged UTC hour from recorders A and B (issue #104). Their frames are listed and merged by identity with
/// <see cref="FrameMerge"/>, as clips joined from two recorders are (#12), so the hour holds every frame either recorder
/// has, once, in stream order, as byte ranges of whole frames from the sources. Nothing is decoded or written here.
/// </summary>
public static class HourMerger
{
    /// <summary>Missing stretches this short or shorter are not reported (the web app's ClipEvents.MinGapSeconds).</summary>
    public const double MinMissingSeconds = 1.5;

    public const string MissingReason = "not recorded by either recorder";

    /// <summary>Frames are read this far either side of the hour so the recorders line up at its edges and the previous hour's tail can be found.</summary>
    static readonly TimeSpan Reach = TimeSpan.FromSeconds(30);

    static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <param name="Index">Whole frames before this one in its file.</param>
    readonly record struct SourceFrame(SourceSegment Source, FrameRef Ref, int Index, DateTimeOffset EstUtc);

    /// <summary>
    /// Null when neither recorder holds a frame estimated to be in the hour. The primary is the recorder with more frames
    /// in the hour (A on a tie). The hour starts after <paramref name="previousTail"/> (the previous merged hour's
    /// <see cref="MergedHourWriter.Tail"/>, all of it) when that whole sequence occurs in the merged frames, otherwise at the first
    /// frame estimated at or after the hour; it ends at the last frame estimated before the next hour.
    /// </summary>
    public static MergedHourPlan? Plan(DateTimeOffset hour, IReadOnlyList<SourceSegment> a, IReadOnlyList<SourceSegment> b, IReadOnlyList<ulong> previousTail)
    {
        hour = hour.ToUniversalTime();
        var end = hour + OneHour;
        var fa = Frames(a, hour - Reach, end + Reach);
        var fb = Frames(b, hour - Reach, end + Reach);
        var inA = fa.Count(f => f.EstUtc >= hour && f.EstUtc < end);
        var inB = fb.Count(f => f.EstUtc >= hour && f.EstUtc < end);
        if (inA == 0 && inB == 0) return null;

        var aPrimary = inA >= inB;
        var (p, s) = aPrimary ? (fa, fb) : (fb, fa);
        var merged = FrameMerge.Merge(ToMerge(p), ToMerge(s));
        var ordered = Ordered(merged, p, s);
        var clock = BoundaryClock(ordered, aPrimary, fa, fb);

        var first = Start(ordered, clock, hour, previousTail);
        var last = clock.FindLastIndex(t => t < end);
        if (first < 0 || last < first) return null;
        var window = ordered.GetRange(first, last - first + 1);

        var pieces = new List<MergedPiece>();
        var start = 0;
        for (var k = 1; k <= window.Count; k++)
        {
            var prev = window[k - 1];
            var joins = k < window.Count
                && ReferenceEquals(window[k].Source, prev.Source)
                && window[k].Index == prev.Index + 1
                && window[k].Ref.Offset == prev.Ref.Offset + prev.Ref.Length;
            if (joins) continue;

            // A piece ends when its last frame does: timed from that frame's start, because the time of the byte after
            // it would jump over any gap in the source that follows.
            var head = window[start];
            pieces.Add(new MergedPiece(
                head.Source.Recorder, head.Source, head.Ref.Offset, prev.Ref.Offset + prev.Ref.Length, head.Index, k - start, head.EstUtc,
                prev.EstUtc.AddSeconds((double)prev.Ref.Length / prev.Source.BytesPerSecond)));
            start = k;
        }

        return new MergedHourPlan(hour, pieces, Missing(merged, hour, end, pieces), [.. window.Select(f => f.Ref.Hash)]);
    }

    /// <summary>
    /// The times the hour's edges are decided on. A frame's estimate depends on which recorder's file it is read from,
    /// and consecutive hours can have different primaries, so in a stretch of identical frames (silence) the two hours
    /// could each take or each skip a few of the same frames. The edges are therefore always judged on A's estimate when
    /// A holds the frame, else on B's, which is the same for a frame in both hours' plans. That order is the merge with A
    /// as primary; it is used only when it holds the same frames in the same order as the hour's own merge.
    /// </summary>
    static List<DateTimeOffset> BoundaryClock(List<SourceFrame> ordered, bool aPrimary, List<SourceFrame> fa, List<SourceFrame> fb)
    {
        if (!aPrimary && fa.Count > 0 && fb.Count > 0)
        {
            var byA = Ordered(FrameMerge.Merge(ToMerge(fa), ToMerge(fb)), fa, fb);
            if (byA.Count == ordered.Count && byA.Select(f => f.Ref.Hash).SequenceEqual(ordered.Select(f => f.Ref.Hash)))
                return [.. byA.Select(f => f.EstUtc)];
        }

        return [.. ordered.Select(f => f.EstUtc)];
    }

    /// <summary>
    /// The index of the hour's first frame. When the previous hour's tail occurs within reach of the boundary, the hour
    /// starts right after it; a tail of identical frames (silence) occurs at several places, and the one nearest the
    /// boundary by time is taken.
    /// </summary>
    static int Start(List<SourceFrame> ordered, List<DateTimeOffset> clock, DateTimeOffset hour, IReadOnlyList<ulong> previousTail)
    {
        var byTime = clock.FindIndex(t => t >= hour);
        var n = previousTail.Count;
        if (n == 0) return byTime;

        var tail = previousTail;
        var best = -1;
        for (var i = n; i < ordered.Count && clock[i - 1] < hour + Reach; i++)
        {
            var match = true;
            for (var k = 0; k < n && match; k++) match = ordered[i - n + k].Ref.Hash == tail[k];
            if (!match) continue;
            if (best < 0 || (byTime >= 0 && Math.Abs(i - byTime) < Math.Abs(best - byTime))) best = i;
        }

        return best >= 0 ? best : byTime;
    }

    /// <summary>
    /// What neither recorder holds in the hour: the merge's missing stretches longer than <see cref="MinMissingSeconds"/>
    /// between the first and last piece, and the hour's edges when no frame covers them.
    /// </summary>
    static List<SegmentGap> Missing(FrameMergeResult merged, DateTimeOffset hour, DateTimeOffset end, List<MergedPiece> pieces)
    {
        var from = pieces[0].FromUtc > hour ? pieces[0].FromUtc : hour;
        var to = pieces[^1].ToUtc < end ? pieces[^1].ToUtc : end;
        var missing = new List<SegmentGap>();
        void Add(DateTimeOffset start, DateTimeOffset stop)
        {
            var seconds = (stop - start).TotalSeconds;
            if (seconds > MinMissingSeconds) missing.Add(new SegmentGap(start, stop, seconds, MissingReason));
        }

        Add(hour, from);
        foreach (var m in merged.Missing)
            Add(m.Start.ToUniversalTime() < from ? from : m.Start.ToUniversalTime(), m.End.ToUniversalTime() > to ? to : m.End.ToUniversalTime());
        Add(to, end);
        missing.Sort((x, y) => x.StartUtc.CompareTo(y.StartUtc));
        return missing;
    }

    static List<MergeFrame> ToMerge(List<SourceFrame> frames) => [.. frames.Select(f => new MergeFrame(f.Ref.Hash, f.EstUtc))];

    static List<SourceFrame> Ordered(FrameMergeResult merged, List<SourceFrame> p, List<SourceFrame> s) =>
        [.. merged.Blocks.SelectMany(b => Enumerable.Range(b.Start, b.Count).Select(i => b.Source == FrameSource.Primary ? p[i] : s[i]))];

    /// <summary>
    /// The whole frames of these segments estimated in [from, to), in stream order, each with its index in its file. Whole
    /// files are listed (not just the window) because a piece names its first frame by index from the start of the file;
    /// the writer reads each whole file for its checksum anyway.
    /// </summary>
    static List<SourceFrame> Frames(IReadOnlyList<SourceSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        var frames = new List<SourceFrame>();
        foreach (var seg in segments.OrderBy(x => x.Meta.StartUtc).ThenBy(x => x.AudioPath, StringComparer.Ordinal))
        {
            var anchors = seg.Anchors;
            if (SegmentTimeline.ToUtc(anchors, 0) >= to || seg.Meta.EndUtc <= from) continue;

            var bytesPerSecond = seg.BytesPerSecond;
            using var fs = new FileStream(seg.AudioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var refs = FrameIndex.Read(fs, 0, Math.Min(seg.Meta.Bytes, fs.Length));
            for (var i = 0; i < refs.Count; i++)
            {
                var est = SegmentTimeline.ToUtc(anchors, (double)refs[i].Offset / bytesPerSecond).ToUniversalTime();
                if (est >= from && est < to) frames.Add(new SourceFrame(seg, refs[i], i, est));
            }
        }

        return frames;
    }
}
