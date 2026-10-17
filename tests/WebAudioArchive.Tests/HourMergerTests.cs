using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #104: a merged hour is every frame recorders A and B hold for that UTC hour, once each, in stream order, taken
/// as byte ranges of their segments, with what neither holds reported as missing.
/// </summary>
public sealed class HourMergerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-hourmerge-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    IReadOnlyList<SourceSegment> Scan(string id) => SourceSegment.Scan(Root(id), "burst", id);

    static SegmentGap Gap(int fromSecond, int toSecond) => new(T0.AddSeconds(fromSecond), T0.AddSeconds(toSecond), toSecond - fromSecond, "end-of-stream");

    static int SeqAt(double second) => SegmentFiles.FramesFor((T0.AddSeconds(second) - Epoch).TotalSeconds);

    /// <summary>Hour segments for 09:00, 10:00 and 11:00 (and 12:00 when asked), each with the gaps that fall in it.</summary>
    void Hours(string id, int hours = 3, (int From, int To)? silence = null, params SegmentGap[] gaps)
    {
        for (var h = -1; h < hours - 1; h++)
        {
            var start = T0.AddHours(h);
            var end = start.AddHours(1);
            SegmentFiles.Live(Root(id), "burst", Epoch, start, end, [.. gaps.Where(g => g.StartUtc < end && g.EndUtc > start)], silence: silence);
        }
    }

    /// <summary>Moves every time in a recorder's sidecars by <paramref name="seconds"/>, as a recorder whose clock is off would write them.</summary>
    void Skew(string id, double seconds)
    {
        foreach (var json in Directory.EnumerateFiles(Root(id), "*.json", SearchOption.AllDirectories))
        {
            var meta = SegmentMetaStore.Read(json)!;
            SegmentMetaStore.Write(json, meta with
            {
                StartUtc = meta.StartUtc.AddSeconds(seconds),
                EndUtc = meta.EndUtc.AddSeconds(seconds),
                Gaps = [.. meta.Gaps.Select(g => g with { StartUtc = g.StartUtc.AddSeconds(seconds), EndUtc = g.EndUtc.AddSeconds(seconds) })],
            });
        }
    }

    /// <summary>Stream positions of the frames in the plan, in order, read from the pieces' bytes.</summary>
    static List<int> Seqs(MergedHourPlan plan) =>
        SegmentFiles.Seqs([.. plan.Pieces.SelectMany(p => File.ReadAllBytes(p.Source.AudioPath)[(int)p.StartByte..(int)p.EndByte])]);

    static void AssertConsecutive(List<int> seqs) => Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);

    [Fact]
    public void An_hour_both_recorders_hold_is_the_primary_alone_with_no_missing_audio()
    {
        Hours("a");
        Hours("b");

        var plan = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);

        Assert.NotNull(plan);
        Assert.Equal(T0, plan.Hour);
        var piece = Assert.Single(plan.Pieces);
        Assert.Equal("a", piece.Recorder); // a tie goes to A
        Assert.Equal("2026/10-15/100000.mp3", piece.Source.RelativeFile);
        Assert.Equal("100000", piece.Source.Stem);
        Assert.Equal(0, piece.StartByte);
        Assert.Equal(new FileInfo(piece.Source.AudioPath).Length, piece.EndByte);
        Assert.Equal(0, piece.FirstFrame);
        Assert.Equal(piece.Source.Meta.Frames, piece.Frames);
        Assert.InRange((piece.FromUtc - T0).TotalSeconds, -0.05, 0.05);
        Assert.InRange((piece.ToUtc - T0.AddHours(1)).TotalSeconds, -0.05, 0.05);
        Assert.Empty(plan.Missing);
        var seqs = Seqs(plan);
        AssertConsecutive(seqs);
        Assert.Equal(seqs.Count, plan.FrameHashes.Count);
    }

    [Fact]
    public void Gaps_in_each_recorder_are_filled_from_the_other_and_every_frame_appears_once()
    {
        Hours("a", gaps: Gap(200, 230));
        Hours("b", gaps: Gap(400, 430));

        var plan = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);

        Assert.NotNull(plan);
        var seqs = Seqs(plan);
        AssertConsecutive(seqs); // nothing repeated, nothing lost
        Assert.InRange(seqs[0] - SeqAt(0), -1, 1);
        Assert.InRange(seqs[^1] - SeqAt(3600), -2, 1);
        Assert.Contains(plan.Pieces, p => p.Recorder == "a");
        Assert.Contains(plan.Pieces, p => p.Recorder == "b");
        Assert.Empty(plan.Missing);
        Assert.Equal(seqs.Count, plan.FrameHashes.Count);
        Assert.Equal(seqs.Count, plan.Pieces.Sum(p => p.Frames));
    }

    [Fact]
    public void A_recorder_down_for_the_whole_hour_gives_the_other_recorders_frames_and_its_gaps()
    {
        // A recorded 09:00 to 10:00 and nothing after; its last 30 s are read but are not in the hour.
        SegmentFiles.Live(Root("a"), "burst", Epoch, T0.AddHours(-1), T0, endedBy: "crash");
        Hours("b", gaps: [Gap(1200, 1260), Gap(3580, 3600)]);

        var plan = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);

        Assert.NotNull(plan);
        Assert.All(plan.Pieces, p => Assert.Equal("b", p.Recorder));
        var seqs = Seqs(plan);
        Assert.Equal(seqs.Distinct().Order(), seqs);
        Assert.InRange(seqs[0] - SeqAt(0), -1, 1);
        Assert.Equal(2, plan.Missing.Count);
        Assert.InRange((plan.Missing[0].StartUtc - T0.AddSeconds(1200)).TotalSeconds, -1, 1);
        Assert.InRange((plan.Missing[0].EndUtc - T0.AddSeconds(1260)).TotalSeconds, -1, 1);
        Assert.InRange((plan.Missing[1].StartUtc - T0.AddSeconds(3580)).TotalSeconds, -1, 1);
        Assert.Equal(T0.AddHours(1), plan.Missing[1].EndUtc);
        Assert.All(plan.Missing, m => Assert.Equal(HourMerger.MissingReason, m.Reason));
        Assert.All(plan.Missing, m => Assert.Equal((m.EndUtc - m.StartUtc).TotalSeconds, m.Seconds, 3));
    }

    [Fact]
    public void An_hour_neither_recorder_holds_has_no_plan()
    {
        SegmentFiles.Live(Root("a"), "burst", Epoch, T0.AddHours(-1), T0);
        SegmentFiles.Live(Root("b"), "burst", Epoch, T0.AddHours(-1), T0);

        Assert.Null(HourMerger.Plan(T0, Scan("a"), Scan("b"), []));
    }

    [Fact]
    public void Two_segments_in_one_hour_and_one_crossing_the_boundary_are_merged_in_stream_order()
    {
        // A: 10:00 to 10:20 (stopped), then restarted at 10:20:03 with a 5 s replay, running past 11:00. A lost 300 to
        // 330 s. B: a segment from 09:30 to 10:30 crossing 10:00, then 10:30 onwards; B lost 2400 to 2500 s, so A is primary.
        SegmentFiles.Live(Root("a"), "burst", Epoch, T0.AddHours(-1), T0);
        SegmentFiles.Live(Root("a"), "burst", Epoch, T0, T0.AddSeconds(1200), [Gap(300, 330)], endedBy: "shutdown");
        SegmentFiles.Live(Root("a"), "burst", Epoch, T0.AddSeconds(1203), T0.AddHours(2), replaySeconds: 5, name: "102003-1");
        SegmentFiles.Live(Root("b"), "burst", Epoch, T0.AddMinutes(-30), T0.AddMinutes(30));
        SegmentFiles.Live(Root("b"), "burst", Epoch, T0.AddMinutes(30), T0.AddHours(2), [Gap(2400, 2500)]);

        var plan = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);

        Assert.NotNull(plan);
        Assert.Equal(
            ["a 2026/10-15/100000.mp3", "b 2026/10-15/093000.mp3", "a 2026/10-15/100000.mp3", "a 2026/10-15/102003-1.mp3"],
            plan.Pieces.Select(p => $"{p.Recorder} {p.Source.RelativeFile}"));
        Assert.Equal("102003-1", plan.Pieces[^1].Source.Stem);
        Assert.True(plan.Pieces[^1].FirstFrame > 0); // the replay at the restart is skipped
        var seqs = Seqs(plan);
        AssertConsecutive(seqs);
        Assert.InRange(seqs[0] - SeqAt(0), -1, 1);
        Assert.InRange(seqs[^1] - SeqAt(3600), -2, 1);
        Assert.Empty(plan.Missing);
        for (var i = 1; i < plan.Pieces.Count; i++)
            Assert.InRange((plan.Pieces[i].FromUtc - plan.Pieces[i - 1].ToUtc).TotalSeconds, -0.1, 0.1);
    }

    [Fact]
    public void Consecutive_hours_join_with_no_frame_repeated_or_lost_even_in_silence()
    {
        // Silence from 10:59:50 to 11:00:10 in the stream. A lost audio in the first hour and B in the second, so the
        // primary changes at the boundary, and B's clock runs 0.4 s ahead of A's, so the two recorders disagree about
        // which silent frame is the first of 11:00.
        var silence = (SeqAt(3590), SeqAt(3610));
        Hours("a", hours: 4, silence: silence, gaps: Gap(1000, 1100));
        Hours("b", hours: 4, silence: silence, gaps: Gap(4600, 4700));
        Skew("b", 0.4);

        var first = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);
        Assert.NotNull(first);
        var second = HourMerger.Plan(T0.AddHours(1), Scan("a"), Scan("b"), [.. first.FrameHashes.TakeLast(8)]);
        Assert.NotNull(second);

        Assert.All(first.Pieces, p => Assert.Equal("b", p.Recorder)); // B has more of the first hour
        Assert.Contains(second.Pieces, p => p.Recorder == "a");
        var one = Seqs(first);
        var two = Seqs(second);
        Assert.Equal(SegmentFiles.SilenceSeq, one[^1]); // the boundary falls inside the silence
        Assert.Equal(SegmentFiles.SilenceSeq, two[0]);
        var joined = one.Concat(two).ToList();
        var expected = Enumerable.Range(one[0], joined.Count)
            .Select(n => n >= silence.Item1 && n < silence.Item2 ? SegmentFiles.SilenceSeq : n);
        Assert.Equal(expected, joined);
        Assert.Equal(first.FrameHashes.Count + second.FrameHashes.Count, joined.Count);
    }

    [Fact]
    public void The_next_hour_starts_after_the_previous_hours_last_frame_when_that_hour_ended_on_another_clock()
    {
        // The first hour was merged from B alone, whose clock runs 0.4 s ahead, so it ended about 15 frames before
        // 11:00 by A's clock. The second hour, merged with A, must still start at the frame after it.
        Hours("a");
        Hours("b");
        Skew("b", 0.4);

        var first = HourMerger.Plan(T0, [], Scan("b"), []);
        Assert.NotNull(first);
        var second = HourMerger.Plan(T0.AddHours(1), Scan("a"), Scan("b"), [.. first.FrameHashes.TakeLast(8)]);
        Assert.NotNull(second);

        Assert.Equal("a", second.Pieces[0].Recorder);
        var one = Seqs(first);
        var two = Seqs(second);
        Assert.True(SeqAt(3600) - one[^1] > 10, "the first hour ended early by A's clock");
        AssertConsecutive([.. one, .. two]);
    }
}
