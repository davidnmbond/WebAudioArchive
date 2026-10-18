using WebAudioArchive.Core.Mp3;

namespace WebAudioArchive.Web.Clips;

/// <summary>A clip joined from two recorders: its pieces in stream order and what neither recorder has (UTC).</summary>
public sealed record StitchedClip(IReadOnlyList<ClipPiece> Pieces, IReadOnlyList<ClipGap> Gaps, double PrimarySeconds, double SecondarySeconds, int Switches, IReadOnlyList<double>? SourceSeconds = null);

/// <summary>
/// Joins two recorders' audio for a window (issue #12). Their frames are listed and merged by identity with
/// <see cref="FrameMerge"/>, so the clip switches recorder exactly where one has frames the other lacks, never repeats
/// audio, and never decodes anything: the pieces are byte ranges of whole frames from the master segments.
/// </summary>
public static class ClipStitcher
{
    /// <summary>Frames are read this far either side of the window so the two recorders can be lined up at its edges.</summary>
    static readonly TimeSpan Reach = TimeSpan.FromSeconds(30);

    public const string MissingReason = "not recorded by either recorder";

    readonly record struct SegmentFrame(CatalogSegment Segment, FrameRef Ref, DateTimeOffset EstUtc);

    /// <summary>
    /// Null only when no frame falls in the window. When the secondary adds nothing, the result is the primary alone, but
    /// with any replay at a restart removed frame by frame rather than by estimated time.
    /// </summary>
    public static StitchedClip? Stitch(IReadOnlyList<CatalogSegment> primary, IReadOnlyList<CatalogSegment> secondary, DateTimeOffset from, DateTimeOffset to) =>
        Stitch(primary, [secondary], from, to);

    /// <summary>
    /// As the two-source form, folded over every other source in the order given (best covered first): the primary is
    /// joined with the first, that result with the next, so each source fills what the earlier ones lack. Source 0 is
    /// the primary; <see cref="StitchedClip.SourceSeconds"/> is indexed by position in (primary, then others).
    /// </summary>
    public static StitchedClip? Stitch(IReadOnlyList<CatalogSegment> primary, IReadOnlyList<IReadOnlyList<CatalogSegment>> others, DateTimeOffset from, DateTimeOffset to)
    {
        var accumulated = Frames(primary, from - Reach, to + Reach).Select(f => (Frame: f, Source: 0)).ToList();
        FrameMergeResult? merged = null;
        for (var n = 0; n < others.Count; n++)
        {
            var s = Frames(others[n], from - Reach, to + Reach);
            var next = FrameMerge.Merge(
                [.. accumulated.Select(x => new MergeFrame(x.Frame.Ref.Hash, x.Frame.EstUtc))],
                [.. s.Select(f => new MergeFrame(f.Ref.Hash, f.EstUtc))]);
            var current = accumulated;
            accumulated = next.Blocks
                .SelectMany(b => Enumerable.Range(b.Start, b.Count).Select(i => b.Source == FrameSource.Primary ? current[i] : (s[i], n + 1)))
                .ToList();
            merged = next;
        }

        if (merged is null) return null;
        var ordered = accumulated.Select(x => (x.Frame, x.Source)).ToList();
        var first = ordered.FindIndex(x => x.Frame.EstUtc >= from);
        var last = ordered.FindLastIndex(x => x.Frame.EstUtc < to);
        if (first < 0 || last < first) return null;
        var window = ordered.GetRange(first, last - first + 1);

        var pieces = new List<ClipPiece>();
        var switches = 0;
        var bySource = new double[others.Count + 1];
        var start = 0;
        for (var k = 1; k <= window.Count; k++)
        {
            var prev = window[k - 1].Frame;
            var joins = k < window.Count
                && window[k].Frame.Segment == prev.Segment
                && window[k].Frame.Ref.Offset == prev.Ref.Offset + prev.Ref.Length;
            if (joins) continue;

            var head = window[start].Frame;
            var piece = new ClipPiece(head.Segment, head.Ref.Offset, prev.Ref.Offset + prev.Ref.Length, head.EstUtc,
                PositionModel.TimeAt(prev.Segment, prev.Ref.Offset + prev.Ref.Length));
            pieces.Add(piece);
            var seconds = (double)(piece.EndByte - piece.StartByte) / head.Segment.BytesPerSecond;
            bySource[window[start].Source] += seconds;
            if (k < window.Count && window[k].Source != window[k - 1].Source) switches++;
            start = k;
        }

        var gaps = new List<ClipGap>();
        if ((pieces[0].StartUtc - from).TotalSeconds > ClipEvents.MinGapSeconds)
            gaps.Add(new ClipGap(from, pieces[0].StartUtc, "not recorded"));
        foreach (var m in merged.Missing.Where(m => m.Seconds > ClipEvents.MinGapSeconds && m.End > from && m.Start < to))
            gaps.Add(new ClipGap(m.Start < from ? from : m.Start, m.End > to ? to : m.End, MissingReason));
        if ((to - pieces[^1].EndUtc).TotalSeconds > ClipEvents.MinGapSeconds)
            gaps.Add(new ClipGap(pieces[^1].EndUtc, to, "not recorded"));

        return new StitchedClip(pieces, gaps, bySource[0], bySource.Skip(1).Sum(), switches, bySource);
    }

    /// <summary>The whole frames of these segments around [from, to), in order, each with its estimated UTC.</summary>
    static List<SegmentFrame> Frames(IReadOnlyList<CatalogSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        var frames = new List<SegmentFrame>();
        foreach (var seg in segments.Where(x => PositionModel.AudioStartUtc(x) < to && x.EndUtc > from))
        {
            using var fs = SegmentCatalog.OpenShared(seg.AudioPath);
            var length = Math.Min(seg.Bytes, fs.Length);
            var startByte = from <= PositionModel.AudioStartUtc(seg) ? 0 : FrameSnap.Floor(fs, length, PositionModel.Offset(seg, from));
            var endByte = to >= seg.EndUtc ? length : Math.Min(length, FrameSnap.Ceil(fs, length, PositionModel.Offset(seg, to)));
            foreach (var f in FrameIndex.Read(fs, startByte, endByte))
                frames.Add(new SegmentFrame(seg, f, PositionModel.TimeAt(seg, f.Offset)));
        }

        return frames;
    }
}
