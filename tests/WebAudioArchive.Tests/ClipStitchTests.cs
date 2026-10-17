extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #12: when neither recorder covers a window, an automatic clip joins A and B at frame boundaries found by
/// matching their bytes, never repeating audio, and reports only what neither recorder has.
/// </summary>
public sealed class ClipStitchTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-stitch-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = T0.AddHours(1);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    List<RecorderSegments> Catalog() =>
        new[] { "a", "b" }.Select(id => new RecorderSegments(id, SegmentCatalog.Scan(Root(id), "burst", id, NullLogger.Instance))).ToList();

    static SegmentGap Gap(int fromSecond, int toSecond) => new(T0.AddSeconds(fromSecond), T0.AddSeconds(toSecond), toSecond - fromSecond, "end-of-stream");

    void Recorder(string id, double startOffsetSeconds, params SegmentGap[] gaps) =>
        SegmentFiles.Live(Root(id), "burst", Epoch, T0.AddSeconds(startOffsetSeconds), T0.AddMinutes(30), gaps);

    static ClipPlan Plan(int fromSecond, int toSecond, List<RecorderSegments> catalog, string recorder = "auto") =>
        ClipPlanner.Plan(new ClipRequest(T0.AddSeconds(fromSecond), T0.AddSeconds(toSecond), TimeSpan.Zero, TimeSpan.Zero, recorder),
            catalog, Now, ClipPlanner.DefaultMaxClip);

    /// <summary>Stream positions of the frames in the clip, in order.</summary>
    static List<int> Seqs(ClipPlan plan) =>
        SegmentFiles.Seqs([.. plan.Pieces.SelectMany(p => File.ReadAllBytes(p.Segment.AudioPath)[(int)p.StartByte..(int)p.EndByte])]);

    static int SeqAt(int second) => SegmentFiles.FramesFor((T0.AddSeconds(second) - Epoch).TotalSeconds);

    [Fact]
    public void Gaps_at_different_times_are_filled_from_the_other_recorder_with_every_frame_once()
    {
        Recorder("a", 0, Gap(200, 230));
        Recorder("b", 0.3, Gap(400, 430));

        var plan = Plan(100, 500, Catalog());

        Assert.Equal("a+b", plan.Recorder);
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs); // consecutive: nothing repeated, nothing lost
        Assert.InRange(seqs[0] - SeqAt(100), -40, 40);
        Assert.InRange(seqs[^1] - SeqAt(500), -40, 40);
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "a");
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "b");
        Assert.Empty(ClipEvents.Gaps(plan));
        Assert.Empty(ClipEvents.Restarts(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("joins recorders A and B"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Gap in recorder"));
    }

    [Fact]
    public void Overlapping_gaps_leave_one_gap_for_what_neither_recorder_has()
    {
        Recorder("a", 0, Gap(200, 230));
        Recorder("b", 0.2, Gap(210, 240));

        var plan = Plan(100, 300, Catalog());

        Assert.Equal("a+b", plan.Recorder);
        var seqs = Seqs(plan);
        Assert.Equal(seqs.Distinct().Order(), seqs); // in order and never repeated
        var gap = Assert.Single(ClipEvents.Gaps(plan));
        Assert.InRange((gap.StartUtc - T0.AddSeconds(210)).TotalSeconds, -1, 1);
        Assert.InRange((gap.EndUtc - T0.AddSeconds(230)).TotalSeconds, -1, 1);
        Assert.Contains(plan.Warnings, w => w.Contains("No audio in either recorder"));
        var missing = Enumerable.Range(seqs[0], seqs[^1] - seqs[0] + 1).Except(seqs).ToList();
        Assert.InRange(missing.Count, SegmentFiles.FramesFor(20) - 2, SegmentFiles.FramesFor(20) + 2);
    }

    [Fact]
    public void A_restart_whose_replay_covers_the_loss_is_joined_frame_by_frame_from_one_recorder()
    {
        // B stops at 300 s and reconnects at 303 s; the server replays 5 s, so B lost nothing but holds 2 s twice.
        // A has a gap later in the window, so B covers more and is chosen.
        Recorder("a", 0, Gap(330, 340));
        SegmentFiles.Live(Root("b"), "burst", Epoch, T0, T0.AddSeconds(300), endedBy: "shutdown");
        SegmentFiles.Live(Root("b"), "burst", Epoch, T0.AddSeconds(303), T0.AddMinutes(30), replaySeconds: 5);

        var plan = Plan(250, 350, Catalog());

        Assert.Equal("b", plan.Recorder);
        Assert.All(plan.Pieces, p => Assert.Equal("b", p.Segment.Recorder));
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs); // the replayed 2 s appear once
        Assert.Empty(ClipEvents.Gaps(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("matched frame by frame"));
    }

    [Fact]
    public void A_chosen_recorder_is_never_mixed()
    {
        Recorder("a", 0, Gap(200, 230));
        Recorder("b", 0.3, Gap(400, 430));

        var plan = Plan(100, 500, Catalog(), recorder: "a");

        Assert.Equal("a", plan.Recorder);
        Assert.All(plan.Pieces, p => Assert.Equal("a", p.Segment.Recorder));
        Assert.Contains(plan.Warnings, w => w.Contains("Gap in recorder A"));
    }

    [Fact]
    public void A_window_one_recorder_covers_is_not_joined()
    {
        Recorder("a", 0, Gap(200, 230));
        Recorder("b", 0.3, Gap(400, 430));

        var plan = Plan(250, 350, Catalog());

        Assert.Equal("a", plan.Recorder);
        Assert.Empty(ClipEvents.Gaps(plan));
    }
}
