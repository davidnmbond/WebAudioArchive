extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;
using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

/// <summary>Coverage of scheduled shows, the clip's gaps and restarts as data, and the show page's timeline plan.</summary>
public sealed class ShowCoverageTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-shows-" + Guid.NewGuid().ToString("N"));

    string A => Path.Combine(_root, "a");
    string B => Path.Combine(_root, "b");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    IReadOnlyList<RecorderSegments> Catalog() =>
    [
        new("a", SegmentCatalog.Scan(A, "burst", "a", NullLogger.Instance)),
        new("b", SegmentCatalog.Scan(B, "burst", "b", NullLogger.Instance)),
    ];

    static ShowOccurrence Show(int fromMinute, int toMinute, string title = "Backseat Aux") =>
        new("burst", title, T0.AddMinutes(fromMinute), T0.AddMinutes(toMinute));

    [Fact]
    public void A_show_fully_recorded_by_both_recorders_is_full_with_both_named()
    {
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(-2), 14 * 60, firstSeq: 0, burstSeconds: 5);
        SegmentFiles.Closed(B, "burst", T0.AddMinutes(-1), 13 * 60, firstSeq: 0, burstSeconds: 5);

        var c = ShowCoverage.Classify(Show(0, 10), Catalog(), T0.AddHours(1));

        Assert.Equal(ShowState.Full, c.State);
        Assert.Equal(["a", "b"], c.FullRecorders.Select(r => r.Recorder));
        Assert.All(c.Recorders, r => Assert.Empty(r.Gaps));
        Assert.Equal("show-full", c.CssClass);
        Assert.Contains("Recorded in full by A and B", c.Summary);
    }

    [Fact]
    public void A_gap_in_one_recorder_is_listed_and_the_other_recorder_keeps_the_show_full()
    {
        var gap = new SegmentGap(T0.AddMinutes(4), T0.AddMinutes(4).AddSeconds(30), 30, "end-of-stream");
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0, gaps: [gap]);
        SegmentFiles.Closed(B, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0);

        var c = ShowCoverage.Classify(Show(0, 10), Catalog(), T0.AddHours(1));

        Assert.Equal(ShowState.Full, c.State);
        var a = c.Recorders.Single(r => r.Recorder == "a");
        Assert.False(a.IsFull);
        Assert.Equal(570, a.CoveredSeconds, 1);
        var g = Assert.Single(a.Gaps);
        Assert.Equal((gap.StartUtc, gap.EndUtc), (g.Start, g.End));
        Assert.Equal(["b"], c.FullRecorders.Select(r => r.Recorder));
    }

    [Fact]
    public void Gaps_in_both_recorders_at_different_times_still_make_the_show_recorded_by_the_two_together()
    {
        var gapA = new SegmentGap(T0.AddMinutes(3), T0.AddMinutes(3).AddSeconds(40), 40, "end-of-stream");
        var gapB = new SegmentGap(T0.AddMinutes(7), T0.AddMinutes(7).AddSeconds(20), 20, "end-of-stream");
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0, gaps: [gapA]);
        SegmentFiles.Closed(B, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0, gaps: [gapB]);

        var c = ShowCoverage.Classify(Show(0, 10), Catalog(), T0.AddHours(1));

        Assert.Equal(ShowState.Full, c.State);
        Assert.Empty(c.FullRecorders);
        Assert.True(c.NeedsBoth);
        Assert.Empty(c.Combined.Missing);
        Assert.Equal(600, c.Combined.CoveredSeconds, 1);
        Assert.Equal("show-full", c.CssClass);
        Assert.Contains("Recorded in full by A and B together", c.Summary);
        Assert.Contains("no single recorder has all of it", c.Summary);
    }

    [Fact]
    public void A_stretch_missing_from_both_recorders_is_partial_and_listed_once()
    {
        var gapA = new SegmentGap(T0.AddMinutes(4), T0.AddMinutes(5), 60, "end-of-stream");
        var gapB = new SegmentGap(T0.AddMinutes(4).AddSeconds(30), T0.AddMinutes(5).AddSeconds(30), 60, "end-of-stream");
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0, gaps: [gapA]);
        SegmentFiles.Closed(B, "burst", T0.AddMinutes(-1), 12 * 60, firstSeq: 0, gaps: [gapB]);

        var c = ShowCoverage.Classify(Show(0, 10), Catalog(), T0.AddHours(1));

        Assert.Equal(ShowState.Partial, c.State);
        Assert.False(c.NeedsBoth);
        var both = Assert.Single(c.Combined.Missing); // only the overlap of the two gaps is missing from both
        Assert.Equal((T0.AddMinutes(4).AddSeconds(30), T0.AddMinutes(5)), (both.Start, both.End));
        Assert.Equal(570, c.Combined.CoveredSeconds, 1);
    }

    [Fact]
    public void Partly_recorded_and_not_recorded_shows_are_told_apart()
    {
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(5), 10 * 60, firstSeq: 0);

        var partial = ShowCoverage.Classify(Show(0, 10), Catalog(), T0.AddHours(1));
        Assert.Equal(ShowState.Partial, partial.State);
        Assert.Equal("show-partial", partial.CssClass);
        var gap = Assert.Single(partial.Recorders.Single(r => r.Recorder == "a").Gaps);
        Assert.Equal(T0, gap.Start);
        Assert.InRange((gap.End - T0.AddMinutes(5)).TotalSeconds, -0.05, 0.05); // within a frame of the first audio
        Assert.Single(partial.Recorders.Single(r => r.Recorder == "b").Gaps); // B has nothing: the whole show is its gap
        var both = Assert.Single(partial.Combined.Missing); // A and B together still lack the first five minutes
        Assert.Equal(T0, both.Start);
        Assert.Contains("A and B together 50%", partial.Summary);

        var missing = ShowCoverage.Classify(Show(-60, -50), Catalog(), T0.AddHours(1));
        Assert.Equal(ShowState.NotRecorded, missing.State);
        Assert.Equal("show-missing", missing.CssClass);
    }

    [Fact]
    public void A_show_on_air_counts_the_open_part_up_to_now_and_a_later_one_is_in_the_future()
    {
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(-10), 10 * 60, firstSeq: 0, burstSeconds: 5);
        SegmentFiles.Open(A, "burst", T0, T0.AddMinutes(25), firstSeq: 100_000, burstSeconds: 0);
        var now = T0.AddMinutes(25).AddSeconds(1);

        var live = ShowCoverage.Classify(Show(0, 60), Catalog(), now);
        Assert.Equal(ShowState.InProgress, live.State);
        Assert.Equal(now, live.MeasuredTo);
        var a = live.Recorders.Single(r => r.Recorder == "a");
        Assert.True(a.IncludesOpenSegment);
        Assert.True(a.IsFull);
        Assert.Contains("being recorded by A", live.Summary);
        Assert.Equal("show-live", live.CssClass);

        // The show that ended at T0 is not "being recorded now", though the open .part rolled at T0 is estimated to start a moment earlier.
        SegmentFiles.Open(B, "burst", T0, T0.AddMinutes(25), firstSeq: 0, burstSeconds: 0.5);
        var before = ShowCoverage.Classify(Show(-10, 0), Catalog(), now);
        Assert.All(before.Recorders, r => Assert.False(r.IncludesOpenSegment));

        var future = ShowCoverage.Classify(Show(60, 120), Catalog(), now);
        Assert.Equal(ShowState.Future, future.State);
        Assert.Equal("show-future", future.CssClass);
        Assert.Contains("not yet broadcast", future.Summary);
    }

    ClipService Clips(DateTimeOffset now)
    {
        var settings = TestSettings.Create(("a", A), ("b", B)) with { CacheDir = Path.Combine(_root, "cache") };
        var time = new ManualTimeProvider(now);
        return new ClipService(settings, new SegmentCatalog(settings, time, NullLogger<SegmentCatalog>.Instance), time, NullLogger<ClipService>.Instance);
    }

    [Fact]
    public void The_show_timeline_shades_the_show_inside_its_margins_and_marks_gaps_and_restarts()
    {
        // Recorder A: 18:50 to 19:06 ended by a reconnect, then 19:06:10 to 19:20; a 20 s sidecar gap at 19:08.
        var s1 = SegmentFiles.Closed(A, "burst", T0.AddMinutes(-10), 16 * 60, firstSeq: 0, burstSeconds: 5, endedBy: "reconnect");
        var gap = new SegmentGap(T0.AddMinutes(8), T0.AddMinutes(8).AddSeconds(20), 20, "end-of-stream");
        SegmentFiles.Closed(A, "burst", T0.AddMinutes(6).AddSeconds(10), 13 * 60 + 50, firstSeq: s1.Frames, gaps: [gap]);
        var now = T0.AddHours(1);
        var show = Show(0, 10);

        var request = ShowTimeline.Request(show, 5, "auto", now);
        Assert.Equal((T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5)), (request.From, request.To, request.MarginBefore));
        var clip = Clips(now).Prepare(null, request);
        var t = ShowTimeline.Build(show, clip, now);

        // The window is 18:55 to 19:15: the show proper starts 5 minutes in.
        Assert.Equal(T0.AddMinutes(-5), clip.Plan.WindowFrom);
        Assert.Equal(300, t.RangeStart!.Value.TotalSeconds, 1);
        // Audio is missing for 10 s at the restart and 20 s in the gap, both before the show's end, so the end moves 30 s earlier.
        Assert.Equal(900 - 30, t.RangeEnd!.Value.TotalSeconds, 1);

        Assert.Equal(["margin", "show", "margin"], t.Segments.Select(s => s.Kind));
        Assert.Equal(t.RangeStart, t.Segments[1].Start);
        Assert.Equal(t.RangeEnd, t.Segments[1].End);
        Assert.Equal(TimeSpan.Zero, t.Segments[0].Start);
        Assert.Equal(clip.File.Seconds, t.Segments[2].End.TotalSeconds, 3);

        Assert.Equal(["show-start", "show-end"], t.Markers.Where(m => m.Kind == "show").Select(m => m.Id));
        var restart = Assert.Single(t.Markers, m => m.Kind == "restart");
        Assert.Contains("19:06:00 UTC", restart.Label);
        Assert.Equal(660, restart.Position.TotalSeconds, 1);
        var gaps = t.Markers.Where(m => m.Kind == "gap").ToList();
        Assert.Equal(2, gaps.Count);
        var join = clip.Gaps()[0];
        Assert.InRange((join.StartUtc - T0.AddMinutes(6)).TotalSeconds, -0.05, 0.05);
        Assert.InRange((join.EndUtc - T0.AddMinutes(6).AddSeconds(10)).TotalSeconds, -0.05, 0.05);
        Assert.Contains(gaps, m => m.Label!.StartsWith("Gap 19:06:00 UTC to 19:06:", StringComparison.Ordinal));
        var sidecar = Assert.Single(gaps, m => m.Label!.Contains("19:08:00 UTC to 19:08:20 UTC"));
        Assert.Equal(780 - 10, sidecar.Position.TotalSeconds, 1);

        // The same facts as structured data, as the API returns them.
        Assert.Equal(["no audio between segments", "end-of-stream"], clip.Gaps().Select(g => g.Reason));
        Assert.Equal("reconnect", Assert.Single(clip.Restarts()).EndedBy);
    }

    [Fact]
    public void A_show_on_air_plays_up_to_now_and_a_future_show_cannot_be_planned()
    {
        SegmentFiles.Open(A, "burst", T0.AddMinutes(-10), T0.AddMinutes(20), firstSeq: 0, burstSeconds: 5);
        var now = T0.AddMinutes(20);
        var show = Show(0, 60);

        var request = ShowTimeline.Request(show, 5, "a", now);
        Assert.Equal(now, request.To);
        var clip = Clips(now).Prepare(null, request);
        var t = ShowTimeline.Build(show, clip, now);
        Assert.Null(t.RangeEnd);
        Assert.Contains(t.Notes, n => n.Contains("still on air"));
        Assert.DoesNotContain(t.Markers, m => m.Id == "show-end");

        var ex = Assert.Throws<ClipException>(() => ShowTimeline.Request(Show(60, 120), 5, "auto", now));
        Assert.Contains("not been broadcast yet", ex.Message);
        Assert.Throws<ClipException>(() => ShowTimeline.Request(show, 61, "auto", now));
    }

    [Fact]
    public void The_clip_clock_maps_utc_to_clip_time_across_a_jump()
    {
        var timeline = new List<(double, DateTimeOffset)> { (0, T0), (100, T0.AddSeconds(130)) };
        var clock = new ClipClock(timeline, 200);

        Assert.Equal(50, clock.ToClip(T0.AddSeconds(50)).TotalSeconds, 6);
        Assert.Equal(100, clock.ToClip(T0.AddSeconds(115)).TotalSeconds, 6); // inside the 30 s jump: at the jump
        Assert.Equal(110, clock.ToClip(T0.AddSeconds(140)).TotalSeconds, 6);
        Assert.Equal(0, clock.ToClip(T0.AddSeconds(-5)).TotalSeconds, 6);
        Assert.Equal(200, clock.ToClip(T0.AddHours(1)).TotalSeconds, 6);
        Assert.Equal(T0.AddSeconds(140), clock.ToUtc(TimeSpan.FromSeconds(110)));
    }
}
