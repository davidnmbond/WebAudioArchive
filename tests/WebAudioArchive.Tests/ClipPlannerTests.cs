extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class ClipPlannerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-plan-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = T0.AddHours(1);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    List<RecorderSegments> Catalog(params string[] ids) =>
        ids.Select(id => new RecorderSegments(id, SegmentCatalog.Scan(Root(id), "burst", id, NullLogger.Instance))).ToList();

    static ClipRequest Window(DateTimeOffset from, DateTimeOffset to, string recorder = "auto", int marginBefore = 0, int marginAfter = 0) =>
        new(from, to, TimeSpan.FromMinutes(marginBefore), TimeSpan.FromMinutes(marginAfter), recorder);

    static ClipPlan Plan(ClipRequest request, List<RecorderSegments> catalog, DateTimeOffset? now = null) =>
        ClipPlanner.Plan(request, catalog, now ?? Now, ClipPlanner.DefaultMaxClip);

    static byte[] Read(ClipPiece p)
    {
        var all = File.ReadAllBytes(p.Segment.AudioPath);
        return all[(int)p.StartByte..(int)p.EndByte];
    }

    static byte[] Clip(ClipPlan plan) => plan.Pieces.SelectMany(Read).ToArray();

    // Three back-to-back segments as the recorder writes them over hour boundaries: the first starts on a connect
    // (5 s Revma burst), the others start at the boundary with no burst, and frame numbers run on across the joins.
    (WrittenSegment S1, WrittenSegment S2, WrittenSegment S3) ThreeHours(string id = "a")
    {
        var s1 = SegmentFiles.Closed(Root(id), "burst", T0, 60, firstSeq: 0, burstSeconds: 5);
        var s2 = SegmentFiles.Closed(Root(id), "burst", T0.AddMinutes(1), 60, firstSeq: s1.FirstSeq + s1.Frames);
        var s3 = SegmentFiles.Closed(Root(id), "burst", T0.AddMinutes(2), 60, firstSeq: s2.FirstSeq + s2.Frames);
        return (s1, s2, s3);
    }

    [Fact]
    public void A_window_over_three_whole_segments_is_their_exact_concatenation()
    {
        var (s1, s2, s3) = ThreeHours();

        var plan = Plan(Window(T0.AddSeconds(-5), T0.AddMinutes(3)), Catalog("a"));

        Assert.Equal("a", plan.Recorder);
        Assert.Equal(3, plan.Pieces.Count);
        Assert.Equal(s1.Bytes.Concat(s2.Bytes).Concat(s3.Bytes).ToArray(), Clip(plan));
        Assert.InRange((plan.Pieces[0].StartUtc - T0.AddSeconds(-5)).TotalSeconds, -0.03, 0.03);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("jumps"));
    }

    [Fact]
    public void A_window_across_a_join_cuts_on_frame_boundaries_and_keeps_every_frame_between()
    {
        var (s1, s2, _) = ThreeHours();

        var plan = Plan(Window(T0.AddSeconds(30), T0.AddSeconds(90)), Catalog("a"));

        // 30 s after the first arrival is 35 s into the file (5 s burst); 30 s into s2 has no burst.
        Assert.InRange(s1.BurstSeconds, 4.98, 5.02);
        Assert.Equal(0, s2.BurstSeconds, 1);
        var start = SegmentFiles.Boundaries(s1.Bytes).Last(b => b <= s1.Estimate(T0.AddSeconds(30)));
        var end = SegmentFiles.Boundaries(s2.Bytes).First(b => b >= s2.Estimate(T0.AddSeconds(90)));
        var expected = s1.Bytes[(int)start..].Concat(s2.Bytes[..(int)end]).ToArray();
        var clip = Clip(plan);
        Assert.Equal(expected, clip);

        var seqs = SegmentFiles.Seqs(clip);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
        Assert.InRange((plan.Pieces[0].StartUtc - T0.AddSeconds(30)).TotalSeconds, -0.03, 0);
        Assert.InRange((plan.Pieces[^1].EndUtc - T0.AddSeconds(90)).TotalSeconds, 0, 0.03);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.013)]
    [InlineData(1.0)]
    [InlineData(17.31)]
    [InlineData(59.99)]
    public void Cuts_at_many_offsets_are_whole_frames(double secondsIn)
    {
        ThreeHours();

        var plan = Plan(Window(T0.AddSeconds(secondsIn), T0.AddSeconds(secondsIn + 7.7)), Catalog("a"));

        var seqs = SegmentFiles.Seqs(Clip(plan));
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
        // Snapping out to whole frames adds at most one frame (about 0.026 s) at each end.
        Assert.InRange(plan.Pieces.Sum(p => p.EndByte - p.StartByte) / 16000.0, 7.69, 7.7 + 0.06);
    }

    [Fact]
    public void Margins_widen_the_window_first()
    {
        ThreeHours();

        var plan = Plan(Window(T0.AddSeconds(70), T0.AddSeconds(80), marginBefore: 1, marginAfter: 1), Catalog("a"));

        Assert.Equal(T0.AddSeconds(10), plan.WindowFrom);
        Assert.Equal(T0.AddSeconds(140), plan.WindowTo);
        Assert.Equal(3, plan.Pieces.Count);
    }

    [Fact]
    public void A_gap_inside_the_window_is_reported_and_the_clip_continues_across_it()
    {
        var gap = new SegmentGap(T0.AddSeconds(20), T0.AddSeconds(25), 5, "end-of-stream");
        var s = SegmentFiles.Closed(Root("a"), "burst", T0, 60, firstSeq: 0, gaps: [gap]);

        var plan = Plan(Window(T0.AddSeconds(10), T0.AddSeconds(40)), Catalog("a"));

        var piece = Assert.Single(plan.Pieces);
        Assert.Contains(plan.Warnings, w => w.Contains("10:00:20") && w.Contains("10:00:25") && w.Contains("UTC") && w.Contains("jumps"));
        // 30 s window minus the 5 s gap: 25 s of audio, starting 10 s in.
        Assert.Equal(SegmentFiles.Boundaries(s.Bytes).Last(b => b <= 10 * 16000), piece.StartByte);
        Assert.Equal(SegmentFiles.Boundaries(s.Bytes).First(b => b >= 35 * 16000), piece.EndByte);
        Assert.Contains(plan.Warnings, w => w.Contains("covers 25"));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("shutdown")]
    public void A_join_after_a_crash_or_shutdown_is_a_break_with_a_warning(string endedBy)
    {
        var s1 = SegmentFiles.Closed(Root("a"), "burst", T0, 40, firstSeq: 0, endedBy: endedBy);
        var s2 = SegmentFiles.Closed(Root("a"), "burst", T0.AddSeconds(50), 70, firstSeq: 50_000, burstSeconds: 5);

        var plan = Plan(Window(T0.AddSeconds(30), T0.AddSeconds(60)), Catalog("a"));

        Assert.Equal(2, plan.Pieces.Count);
        Assert.Equal(s1.Bytes.Length, plan.Pieces[0].EndByte);
        Assert.Equal(0, plan.Pieces[1].StartByte); // the restarted connection's burst starts at 10:00:45
        Assert.Contains(plan.Warnings, w => w.Contains(endedBy) && w.Contains("10:00:40"));
        Assert.Contains(plan.Warnings, w => w.Contains("10:00:40") && w.Contains("10:00:45") && w.Contains("jumps"));
        Assert.Equal(s2.FirstSeq, SegmentFiles.Seqs(Read(plan.Pieces[1]))[0]);
    }

    [Fact]
    public void Overlapping_replay_after_a_restart_is_not_played_twice()
    {
        SegmentFiles.Closed(Root("a"), "burst", T0, 40, firstSeq: 0, endedBy: "shutdown");
        // Restarted 2 s later; its 5 s burst reaches back to 10:00:37, before the previous segment ended.
        SegmentFiles.Closed(Root("a"), "burst", T0.AddSeconds(42), 60, firstSeq: 50_000, burstSeconds: 5);

        var plan = Plan(Window(T0.AddSeconds(30), T0.AddSeconds(60)), Catalog("a"));

        Assert.Equal(2, plan.Pieces.Count);
        Assert.InRange((plan.Pieces[1].StartUtc - T0.AddSeconds(40)).TotalSeconds, -0.03, 0.03);
        Assert.True(plan.Pieces[1].StartByte > 0);
    }

    [Fact]
    public void Auto_picks_the_recorder_with_better_coverage_and_a_forced_choice_says_the_other_was_better()
    {
        SegmentFiles.Closed(Root("a"), "burst", T0, 60, firstSeq: 0,
            gaps: [new SegmentGap(T0.AddSeconds(20), T0.AddSeconds(30), 10, "eof")]);
        SegmentFiles.Closed(Root("b"), "burst", T0, 60, firstSeq: 0);
        var catalog = Catalog("a", "b");

        var auto = Plan(Window(T0.AddSeconds(10), T0.AddSeconds(50)), catalog);
        Assert.Equal("b", auto.Recorder);
        Assert.Empty(auto.Warnings);

        var forced = Plan(Window(T0.AddSeconds(10), T0.AddSeconds(50), recorder: "a"), catalog);
        Assert.Equal("a", forced.Recorder);
        Assert.Contains(forced.Warnings, w => w.Contains("Recorder B") && w.Contains("more"));
    }

    [Fact]
    public void Ties_go_to_A()
    {
        SegmentFiles.Closed(Root("a"), "burst", T0, 60, firstSeq: 0);
        SegmentFiles.Closed(Root("b"), "burst", T0, 60, firstSeq: 0);

        var plan = Plan(Window(T0.AddSeconds(10), T0.AddSeconds(50)), Catalog("a", "b"));

        Assert.Equal("a", plan.Recorder);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_fraction_of_a_second_more_coverage_is_still_a_tie_that_goes_to_A()
    {
        // As seen live across 16:00: B's segments started and rolled a few milliseconds apart from A's.
        SegmentFiles.Closed(Root("a"), "burst", T0.AddSeconds(0.3), 60, firstSeq: 0);
        SegmentFiles.Closed(Root("b"), "burst", T0, 60, firstSeq: 0);

        Assert.Equal("a", Plan(Window(T0, T0.AddSeconds(50)), Catalog("a", "b")).Recorder);
    }

    [Fact]
    public void When_both_recorders_have_gaps_but_the_other_holds_nothing_new_the_clip_comes_from_one()
    {
        SegmentFiles.Closed(Root("a"), "burst", T0, 60, firstSeq: 0,
            gaps: [new SegmentGap(T0.AddSeconds(20), T0.AddSeconds(30), 10, "eof")]);
        SegmentFiles.Closed(Root("b"), "burst", T0, 60, firstSeq: 0,
            gaps: [new SegmentGap(T0.AddSeconds(35), T0.AddSeconds(38), 3, "eof")]);

        var plan = Plan(Window(T0.AddSeconds(10), T0.AddSeconds(50)), Catalog("a", "b"));

        Assert.Equal("b", plan.Recorder);
        // These synthetic frames run on across the gaps, so A's frames are all in B: joining adds nothing and the clip is B's alone.
        Assert.All(plan.Pieces, p => Assert.Equal("b", p.Segment.Recorder));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("joins recorders"));
    }

    [Fact]
    public void A_window_with_no_recording_is_a_clear_not_found_error()
    {
        ThreeHours();

        var ex = Assert.Throws<ClipException>(() => Plan(Window(T0.AddMinutes(30), T0.AddMinutes(31)), Catalog("a")));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("No recording", ex.Message);
        Assert.Contains("10:30:00 UTC", ex.Message);
    }

    [Fact]
    public void Windows_partly_before_the_first_or_after_the_last_recording_are_warned()
    {
        ThreeHours();

        var before = Plan(Window(T0.AddMinutes(-2), T0.AddSeconds(10)), Catalog("a"));
        Assert.Contains(before.Warnings, w => w.Contains("before the first recording") && w.Contains("09:59:55 UTC"));

        var after = Plan(Window(T0.AddSeconds(170), T0.AddMinutes(5)), Catalog("a"));
        Assert.Contains(after.Warnings, w => w.Contains("after the last recording") && w.Contains("10:03:00 UTC"));
    }

    [Fact]
    public void Invalid_windows_are_rejected_with_400()
    {
        ThreeHours();
        var catalog = Catalog("a");

        Assert.Equal(400, Assert.Throws<ClipException>(() => Plan(Window(T0.AddSeconds(30), T0.AddSeconds(30)), catalog)).StatusCode);
        Assert.Equal(400, Assert.Throws<ClipException>(() => Plan(Window(T0.AddSeconds(30), T0.AddSeconds(10)), catalog)).StatusCode);
        var tooLong = Assert.Throws<ClipException>(() => Plan(Window(T0, T0.AddHours(4).AddSeconds(1)), catalog, now: T0.AddDays(1)));
        Assert.Equal(400, tooLong.StatusCode);
        Assert.Contains("4 hours", tooLong.Message);
        var future = Assert.Throws<ClipException>(() => Plan(Window(Now.AddMinutes(-1), Now.AddMinutes(1)), catalog));
        Assert.Equal(400, future.StatusCode);
        Assert.Contains("future", future.Message);
        Assert.Equal(400, Assert.Throws<ClipException>(() => Plan(Window(T0, T0.AddSeconds(10), recorder: "c"), catalog)).StatusCode);
    }

    [Fact]
    public void Margins_that_would_pass_the_maximum_are_reduced_with_a_warning()
    {
        ThreeHours();

        var plan = Plan(Window(T0.AddMinutes(-235), T0.AddMinutes(1), marginBefore: 5, marginAfter: 5), Catalog("a"), now: T0.AddDays(1));

        Assert.Equal(TimeSpan.FromHours(4), plan.WindowTo - plan.WindowFrom);
        Assert.Contains(plan.Warnings, w => w.Contains("Margins") && w.Contains("4 hours"));
    }

    [Fact]
    public void An_open_part_is_used_up_to_its_last_whole_frame_with_a_note()
    {
        var s1 = SegmentFiles.Closed(Root("a"), "burst", T0, 60, firstSeq: 0, burstSeconds: 5);
        var open = SegmentFiles.Open(Root("a"), "burst", T0.AddMinutes(1), T0.AddMinutes(2), firstSeq: s1.FirstSeq + s1.Frames, burstSeconds: 0);
        // The writer is half way through a frame.
        File.WriteAllBytes(open.AudioPath, open.Bytes.Concat(Mp3TestData.Frame(99_999)[..100]).ToArray());
        File.SetLastWriteTimeUtc(open.AudioPath, T0.AddMinutes(2).UtcDateTime);

        var plan = Plan(Window(T0.AddSeconds(50), T0.AddMinutes(2)), Catalog("a"), now: T0.AddMinutes(2).AddSeconds(1));

        Assert.Equal(2, plan.Pieces.Count);
        Assert.Equal(open.Bytes.Length, plan.Pieces[1].EndByte);
        Assert.Contains(plan.Notes, n => n.Contains("being recorded"));
        Assert.Contains(plan.Notes, n => n.Contains("about a second"));
        SegmentFiles.Seqs(Clip(plan));
    }
}
