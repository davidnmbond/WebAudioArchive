using System.Globalization;

namespace WebAudioArchive.Web.Clips;

/// <summary>A clip request error with the HTTP status it maps to (400 bad input, 404 nothing recorded).</summary>
public sealed class ClipException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>A wanted window, before margins. <c>Recorder</c> is "auto" or a configured recorder id.</summary>
public sealed record ClipRequest(DateTimeOffset From, DateTimeOffset To, TimeSpan MarginBefore, TimeSpan MarginAfter, string Recorder);

/// <summary>A byte range [StartByte, EndByte) of one segment, both on frame boundaries, and the wall times it holds.</summary>
public sealed record ClipPiece(CatalogSegment Segment, long StartByte, long EndByte, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record ClipPlan(
    string Recorder,
    DateTimeOffset RequestedFrom,
    DateTimeOffset RequestedTo,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    IReadOnlyList<ClipPiece> Pieces,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, double> CoverageSeconds,
    IReadOnlyList<ClipGap>? StitchedGaps = null)
{
    /// <summary>Recorder id of a clip joined from two recorders (issue #12).</summary>
    public const string Joined = "a+b";

    public bool IsStitched => StitchedGaps is not null;

    public DateTimeOffset StartUtc => Pieces[0].StartUtc;
    public DateTimeOffset EndUtc => Pieces[^1].EndUtc;
}

/// <summary>
/// Chooses a recorder for a window and lists the byte ranges to join. A gap or a break is joined straight across (no
/// silence inserted) and reported as a warning with its UTC times. A chosen recorder is never mixed with another; with
/// Auto, when neither recorder covers the window, the clip joins both at frames matched by their bytes (issue #12).
/// </summary>
public static class ClipPlanner
{
    public static readonly TimeSpan DefaultMaxClip = TimeSpan.FromHours(4);
    public static readonly TimeSpan MaxMargin = TimeSpan.FromMinutes(60);

    /// <summary>Joins or uncovered stretches shorter than this are frame rounding, not breaks.</summary>
    const double BreakSeconds = 1.5;

    public const string AccuracyNote =
        "Positions are estimated from the stream bitrate and then cut on whole MP3 frames, so the clip's start and end are accurate to about a second.";

    public static ClipPlan Plan(ClipRequest request, IReadOnlyList<RecorderSegments> recorders, DateTimeOffset now, TimeSpan maxClip)
    {
        var warnings = new List<string>();
        var notes = new List<string>();
        var (from, to) = Window(request, now, maxClip, warnings);

        if (request.Recorder != "auto" && recorders.All(r => r.Recorder != request.Recorder))
            throw new ClipException(400, $"Unknown recorder '{request.Recorder}'. Use auto or one of: {string.Join(", ", recorders.Select(r => r.Recorder))}.");

        var coverage = recorders.ToDictionary(r => r.Recorder, r => WindowCoverage.Seconds(r.Segments, from, to));
        var candidates = recorders
            .Select(r => (r.Recorder, Segments: r.Segments.Where(s => PositionModel.AudioStartUtc(s) < to && s.EndUtc > from).ToList()))
            .ToList();

        var chosen = request.Recorder == "auto"
            ? BestCovered(candidates, coverage)
            : candidates.First(c => c.Recorder == request.Recorder);

        if (chosen.Recorder is null || chosen.Segments.Count == 0)
            throw new ClipException(404, NothingRecorded(request.Recorder, from, to, recorders));

        var pieces = Pieces(chosen.Segments, from, to);
        if (pieces.Count == 0)
            throw new ClipException(404, NothingRecorded(request.Recorder, from, to, recorders));

        var windowSeconds = (to - from).TotalSeconds;
        var covered = coverage[chosen.Recorder];
        string? nothingNew = null;
        // A restart or a gap inside the window is joined by estimated time, so a few frames can repeat or go missing
        // there; the other recorder, matched frame by frame, makes the join exact or fills it.
        var breaks = pieces.Any(p => p.Segment.SpanGaps.Any(g => g.EndUtc > from && g.StartUtc < to))
            || pieces.SkipLast(1).Any(p => p.Segment.EndedBy is not null and not "hour-boundary");
        if (request.Recorder == "auto" && (covered < windowSeconds - BreakSeconds || breaks))
        {
            // Every other source with audio here, best covered first, each filling what the earlier ones lack.
            var others = candidates.Where(c => c.Recorder != chosen.Recorder && c.Segments.Count > 0).OrderByDescending(c => coverage[c.Recorder]).ToList();
            if (others.Count > 0)
            {
                var stitched = ClipStitcher.Stitch(chosen.Segments, [.. others.Select(o => (IReadOnlyList<CatalogSegment>)o.Segments)], from, to);
                if (stitched is not null && stitched.SecondarySeconds > 0)
                {
                    var used = new List<(string Id, double Seconds)> { (chosen.Recorder, stitched.PrimarySeconds) };
                    used.AddRange(others.Select((o, i) => (o.Recorder, stitched.SourceSeconds![i + 1])).Where(u => u.Item2 > 0));
                    return Stitched(request, from, to, used, stitched, coverage, notes);
                }

                var otherNames = SourceNames.Many(others.Select(o => o.Recorder));
                if (stitched is not null && breaks)
                    return Exact(request, from, to, chosen.Recorder, otherNames, chosen.Segments, stitched, coverage, notes);
                if (covered < windowSeconds - BreakSeconds)
                    nothingNew = $"Neither source covers the whole window, and {otherNames} holds nothing {SourceNames.Short(chosen.Recorder)} lacks here, so the clip comes from {SourceNames.Short(chosen.Recorder)}.";
            }
        }

        var all = recorders.First(r => r.Recorder == chosen.Recorder).Segments;
        WarnEdges(chosen.Recorder, all, pieces, from, to, warnings);
        WarnJoins(chosen.Recorder, pieces, warnings);
        WarnGaps(chosen.Recorder, chosen.Segments, from, to, warnings);

        if (covered < windowSeconds - BreakSeconds)
            warnings.Add($"{SourceNames.WhoCap(chosen.Recorder)} covers {covered:0.0} of {windowSeconds:0.0} seconds ({covered / windowSeconds:0%}) of the window.");

        foreach (var (other, seconds) in coverage)
        {
            if (other != chosen.Recorder && seconds > covered + BreakSeconds)
                warnings.Add($"{SourceNames.WhoCap(other)} would have covered more of this window ({seconds:0.0} s against {covered:0.0} s).");
        }

        if (nothingNew is not null) warnings.Add(nothingNew);

        AddCommonNotes(pieces, notes);
        return new ClipPlan(chosen.Recorder, request.From, request.To, from, to, pieces, warnings, notes, coverage);
    }

    static ClipPlan Stitched(
        ClipRequest request, DateTimeOffset from, DateTimeOffset to, List<(string Id, double Seconds)> used, StitchedClip stitched,
        Dictionary<string, double> coverage, List<string> notes)
    {
        var parts = string.Join(", ", used.OrderBy(kv => kv.Id, StringComparer.Ordinal).Select(kv => $"{kv.Seconds:0.0} s from {SourceNames.Short(kv.Id)}"));
        var who = SourceNames.Many(used.Select(u => u.Id));
        var none = used.Any(u => SourceNames.IsMerged(u.Id)) ? "any source" : "either recorder";
        var warnings = new List<string>
        {
            $"No single source runs unbroken through the window, so this clip joins {who} at whole MP3 frames matched by their bytes " +
            $"({parts}; {stitched.Switches} switches). Nothing is repeated.",
        };

        foreach (var g in stitched.Gaps)
        {
            warnings.Add(g.Reason == ClipStitcher.MissingReason
                ? $"No audio in {none} from {Utc(g.StartUtc)} to {Utc(g.EndUtc)} ({g.Seconds:0.0} s): the clip jumps across it with no silence inserted."
                : $"No audio in {none} from {Utc(g.StartUtc)} to {Utc(g.EndUtc)} ({g.Seconds:0.0} s), at the edge of the window.");
        }

        AddCommonNotes(stitched.Pieces, notes);
        return new ClipPlan(ClipPlan.Joined, request.From, request.To, from, to, stitched.Pieces, warnings, notes, coverage, stitched.Gaps);
    }

    /// <summary>
    /// One recorder alone, but with its restarts joined frame by frame: the other recorder held nothing new, and matching
    /// against it showed exactly which replayed frames to drop.
    /// </summary>
    static ClipPlan Exact(
        ClipRequest request, DateTimeOffset from, DateTimeOffset to, string recorder, string other, List<CatalogSegment> segments,
        StitchedClip stitched, Dictionary<string, double> coverage, List<string> notes)
    {
        var warnings = new List<string>();
        var none = SourceNames.IsMerged(recorder) || other.Contains("merged", StringComparison.Ordinal) ? "any source" : "either recorder";
        foreach (var s in segments.Where(s => s.EndedBy is not null and not "hour-boundary" && s.EndUtc > from && s.EndUtc < to))
        {
            warnings.Add($"{SourceNames.WhoCap(recorder)} restarted at {Utc(s.EndUtc)} ({s.EndedBy}); the join was matched frame by frame against " +
                         $"{other}, so no audio is repeated there.");
        }

        foreach (var g in stitched.Gaps)
            warnings.Add($"No audio in {none} from {Utc(g.StartUtc)} to {Utc(g.EndUtc)} ({g.Seconds:0.0} s): the clip jumps across it with no silence inserted.");

        AddCommonNotes(stitched.Pieces, notes);
        return new ClipPlan(recorder, request.From, request.To, from, to, stitched.Pieces, warnings, notes, coverage, stitched.Gaps);
    }

    static void AddCommonNotes(IReadOnlyList<ClipPiece> pieces, List<string> notes)
    {
        var open = pieces.Select(p => p.Segment).FirstOrDefault(s => s.IsOpen);
        if (open is not null)
            notes.Add($"Includes the segment being recorded now (started {Utc(open.StartUtc)}); any gaps in it are not known until it closes at the end of the hour.");
        notes.Add(AccuracyNote);
    }

    /// <summary>
    /// The recorder covering most of the window. Differences under <see cref="BreakSeconds"/> (the two connections never
    /// start or roll at exactly the same instant) count as a tie, and ties go to the first configured recorder (A).
    /// </summary>
    static (string Recorder, List<CatalogSegment> Segments) BestCovered(
        List<(string Recorder, List<CatalogSegment> Segments)> candidates, Dictionary<string, double> coverage)
    {
        (string Recorder, List<CatalogSegment> Segments) best = default;
        foreach (var c in candidates.Where(c => c.Segments.Count > 0))
        {
            if (best.Recorder is null || coverage[c.Recorder] > coverage[best.Recorder] + BreakSeconds) best = c;
        }

        return best;
    }

    static (DateTimeOffset From, DateTimeOffset To) Window(ClipRequest r, DateTimeOffset now, TimeSpan maxClip, List<string> warnings)
    {
        if (r.From >= r.To)
            throw new ClipException(400, $"From ({Utc(r.From)}) must be before To ({Utc(r.To)}).");
        if (r.To > now.AddSeconds(2))
            throw new ClipException(400, $"The window ends in the future ({Utc(r.To)}; it is now {Utc(now)}).");
        if (r.To - r.From > maxClip)
            throw new ClipException(400, $"The window is {(r.To - r.From).TotalHours:0.##} hours long; the maximum clip length is {maxClip.TotalHours:0.##} hours.");
        if (r.MarginBefore < TimeSpan.Zero || r.MarginAfter < TimeSpan.Zero || r.MarginBefore > MaxMargin || r.MarginAfter > MaxMargin)
            throw new ClipException(400, $"Margins must be between 0 and {MaxMargin.TotalMinutes:0} minutes.");

        var before = r.MarginBefore;
        var after = r.MarginAfter;
        var spare = maxClip - (r.To - r.From);
        if (before + after > spare)
        {
            var scale = spare / (before + after);
            before = TimeSpan.FromTicks((long)(before.Ticks * scale));
            after = spare - before;
            warnings.Add($"Margins were reduced to {before.TotalMinutes:0.#} and {after.TotalMinutes:0.#} minutes to keep the clip within the {maxClip.TotalHours:0.##} hours maximum.");
        }

        var from = r.From - before;
        var to = r.To + after;
        if (to > now)
        {
            if (after > TimeSpan.Zero) warnings.Add($"The margin after was cut short at the current time, {Utc(now)}.");
            to = now;
        }

        return (from, to);
    }

    static List<ClipPiece> Pieces(List<CatalogSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        var pieces = new List<ClipPiece>();
        DateTimeOffset? previousEnd = null;
        foreach (var s in segments)
        {
            var audioStart = PositionModel.AudioStartUtc(s);
            var pieceFrom = Max(from, audioStart);
            // A restarted connection replays a few seconds that the previous segment already holds; skip them.
            if (previousEnd is not null) pieceFrom = Max(pieceFrom, previousEnd.Value);
            var pieceTo = Min(to, s.EndUtc);
            if (pieceTo <= pieceFrom) continue;

            using var fs = SegmentCatalog.OpenShared(s.AudioPath);
            var length = Math.Min(s.Bytes, fs.Length);
            var wholeEnd = FrameSnap.Floor(fs, length, length);
            var startByte = pieceFrom <= audioStart ? FrameSnap.Floor(fs, length, 0) : FrameSnap.Floor(fs, length, PositionModel.Offset(s, pieceFrom));
            var endByte = pieceTo >= s.EndUtc ? wholeEnd : Math.Min(wholeEnd, FrameSnap.Ceil(fs, length, PositionModel.Offset(s, pieceTo)));
            if (endByte <= startByte) continue;

            var startUtc = startByte == 0 ? audioStart : PositionModel.TimeAt(s, startByte);
            var endUtc = endByte == wholeEnd && pieceTo >= s.EndUtc ? s.EndUtc : PositionModel.TimeAt(s, endByte);
            pieces.Add(new ClipPiece(s, startByte, endByte, startUtc, endUtc));
            previousEnd = s.EndUtc;
        }

        return pieces;
    }

    static void WarnEdges(string id, IReadOnlyList<CatalogSegment> all, List<ClipPiece> pieces, DateTimeOffset from, DateTimeOffset to, List<string> warnings)
    {
        var firstAudio = PositionModel.AudioStartUtc(all[0]);
        var lastAudio = all.Max(s => s.EndUtc);
        if (pieces[0].StartUtc > from.AddSeconds(BreakSeconds))
        {
            warnings.Add(firstAudio >= pieces[0].StartUtc.AddSeconds(-BreakSeconds)
                ? $"The window starts before the first recording of {SourceNames.Who(id)} (first audio at {Utc(firstAudio)}); the clip starts there."
                : $"No audio in {SourceNames.Who(id)} from {Utc(from)} to {Utc(pieces[0].StartUtc)}; the clip starts at {Utc(pieces[0].StartUtc)}.");
        }

        if (pieces[^1].EndUtc < to.AddSeconds(-BreakSeconds))
        {
            warnings.Add(lastAudio <= pieces[^1].EndUtc.AddSeconds(BreakSeconds)
                ? $"The window ends after the last recording of {SourceNames.Who(id)} (last audio at {Utc(lastAudio)}); the clip ends there."
                : $"No audio in {SourceNames.Who(id)} from {Utc(pieces[^1].EndUtc)} to {Utc(to)}; the clip ends at {Utc(pieces[^1].EndUtc)}.");
        }
    }

    static void WarnJoins(string id, List<ClipPiece> pieces, List<string> warnings)
    {
        for (var i = 1; i < pieces.Count; i++)
        {
            var prev = pieces[i - 1];
            var next = pieces[i];
            if (prev.Segment.EndedBy is not null and not "hour-boundary")
            {
                warnings.Add($"{SourceNames.WhoCap(id)}'s segment ended by {prev.Segment.EndedBy} at {Utc(prev.Segment.EndUtc)} and the next one starts at " +
                             $"{Utc(next.Segment.StartUtc)}: the audio breaks at this join.");
            }

            var missing = (next.StartUtc - prev.EndUtc).TotalSeconds;
            if (missing > BreakSeconds)
            {
                warnings.Add($"No audio in {SourceNames.Who(id)} from {Utc(prev.EndUtc)} to {Utc(next.StartUtc)} ({missing:0.0} s): " +
                             "the clip jumps across it with no silence inserted.");
            }
        }
    }

    static void WarnGaps(string id, List<CatalogSegment> segments, DateTimeOffset from, DateTimeOffset to, List<string> warnings)
    {
        foreach (var g in segments.SelectMany(s => s.SpanGaps).Where(g => g.EndUtc > from && g.StartUtc < to).OrderBy(g => g.StartUtc))
        {
            warnings.Add($"Gap in {SourceNames.Who(id)} from {Utc(g.StartUtc)} to {Utc(g.EndUtc)} ({(g.EndUtc - g.StartUtc).TotalSeconds:0.0} s, {g.Reason}): " +
                         "no audio was received, and the audio jumps at this point (no silence is inserted).");
        }
    }

    static string NothingRecorded(string recorder, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<RecorderSegments> recorders)
    {
        var who = recorder == "auto" ? "any recorder" : SourceNames.Who(recorder);
        var known = recorders.Select(r => r.Segments.Count == 0
            ? $"{SourceNames.Who(r.Recorder)} has no recordings"
            : $"{SourceNames.Who(r.Recorder)} has audio from {Utc(PositionModel.AudioStartUtc(r.Segments[0]))} to {Utc(r.Segments.Max(s => s.EndUtc))}");
        return $"No recording in {who} covers {Utc(from)} to {Utc(to)} ({string.Join("; ", known)}).";
    }

    /// <summary>"2026-10-15 15:15:00 UTC". Every time shown to a person goes through this.</summary>
    public static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
