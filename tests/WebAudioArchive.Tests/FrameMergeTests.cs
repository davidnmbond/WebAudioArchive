using WebAudioArchive.Core.Mp3;

namespace WebAudioArchive.Tests;

/// <summary>
/// Merging two recorders' frame lists into the stream as captured. Each test frame's hash is its position in the
/// station's stream, so the expected output is simply the positions either recorder holds, in order, once each.
/// </summary>
public sealed class FrameMergeTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 16, 14, 0, 0, TimeSpan.Zero);
    const double FrameSeconds = 1152.0 / 44100;

    /// <summary>Frames for stream positions, with the estimated UTC each recorder would give them (off by a little).</summary>
    static List<MergeFrame> Frames(IEnumerable<int> positions, double clockErrorSeconds = 0) =>
        [.. positions.Select(p => new MergeFrame((ulong)p + 1000, T0.AddSeconds(p * FrameSeconds + clockErrorSeconds)))];

    static IEnumerable<int> Range(int from, int toExclusive) => Enumerable.Range(from, toExclusive - from);

    static List<int> Stream(FrameMergeResult r, List<MergeFrame> a, List<MergeFrame> b) =>
        [.. r.Blocks.SelectMany(x => Enumerable.Range(x.Start, x.Count).Select(i => (int)((x.Source == FrameSource.Primary ? a : b)[i].Hash - 1000)))];

    [Fact]
    public void Identical_recorders_give_the_primary_alone()
    {
        var a = Frames(Range(0, 500));
        var b = Frames(Range(0, 500), 0.3);

        var r = FrameMerge.Merge(a, b);

        var block = Assert.Single(r.Blocks);
        Assert.Equal((FrameSource.Primary, 0, 500), (block.Source, block.Start, block.Count));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Where_the_secondary_starts_earlier_its_lead_comes_first_and_the_ends_are_joined_without_repeats()
    {
        var a = Frames(Range(10, 500));
        var b = Frames(Range(0, 490), -0.2);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 500), Stream(r, a, b));
        Assert.Equal(FrameSource.Secondary, r.Blocks[0].Source);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void A_gap_in_the_primary_is_filled_from_the_secondary()
    {
        var a = Frames(Range(0, 100).Concat(Range(195, 400)));
        var b = Frames(Range(0, 400), 0.5);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 400), Stream(r, a, b));
        Assert.Equal([FrameSource.Primary, FrameSource.Secondary, FrameSource.Primary], r.Blocks.Select(x => x.Source));
        Assert.Equal((100, 95), (r.Blocks[1].Start, r.Blocks[1].Count));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Gaps_in_each_recorder_at_different_times_are_each_filled_by_the_other()
    {
        var a = Frames(Range(0, 100).Concat(Range(150, 600)));
        var b = Frames(Range(0, 300).Concat(Range(380, 600)), 0.4);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 600), Stream(r, a, b));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Overlapping_gaps_leave_only_the_stretch_neither_has_and_report_it()
    {
        // A lost 100 to 199, B lost 121 to 249: B supplies 100 to 120, A supplies 200 to 249, 121 to 199 (2 s) is missing from both.
        var a = Frames(Range(0, 100).Concat(Range(200, 400)));
        var b = Frames(Range(0, 121).Concat(Range(250, 400)), 0.2);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 121).Concat(Range(200, 400)), Stream(r, a, b));
        var gap = Assert.Single(r.Missing);
        Assert.InRange((gap.Start - T0.AddSeconds(121 * FrameSeconds)).TotalSeconds, -0.1, 0.1);
        Assert.InRange((gap.End - T0.AddSeconds(200 * FrameSeconds)).TotalSeconds, -0.1, 0.1);
    }

    [Fact]
    public void Audio_the_secondary_lacks_is_kept_even_when_its_times_wrongly_look_continuous_across_its_break()
    {
        // B lost 200 to 299, but its estimated times place the break 38 frames late (as the position model can), so
        // across the true break B's times look continuous. A's frames 200 to 299 are real audio and must stay.
        var a = Frames(Range(0, 500));
        var b = Range(0, 200).Concat(Range(300, 500))
            .Select((p, i) => new MergeFrame((ulong)p + 1000, T0.AddSeconds((p < 300 || i >= 238 ? p : i) * FrameSeconds)))
            .ToList();

        var r = FrameMerge.Merge(b, a);

        Assert.Equal(Range(0, 500), Stream(r, b, a));
    }

    [Fact]
    public void A_break_both_recorders_share_is_reported_even_though_each_places_it_in_a_different_frame()
    {
        // Both lost 200 to 299 (2.6 s). Each recorder's times show the jump, but A's 10 frames late and B's 30 frames late.
        static List<MergeFrame> Shared(int lateBy, double err) =>
        [
            .. Range(0, 200).Concat(Range(300, 500)).Select((p, i) =>
                new MergeFrame((ulong)p + 1000, T0.AddSeconds((i >= 200 && i < 200 + lateBy ? i : p) * FrameSeconds + err))),
        ];
        var a = Shared(10, 0);
        var b = Shared(30, 0.2);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 200).Concat(Range(300, 500)), Stream(r, a, b));
        var gap = Assert.Single(r.Missing);
        Assert.InRange(gap.Seconds, 100 * FrameSeconds - 0.1, 100 * FrameSeconds + 0.1);
        Assert.InRange((gap.Start - T0.AddSeconds(200 * FrameSeconds)).TotalSeconds, -0.1, 1.0); // where it is: within a second
    }

    [Fact]
    public void A_replay_after_a_short_drop_is_not_repeated()
    {
        // A dropped and on reconnect the server replayed 97 to 99, which A already had.
        var a = Frames(Range(0, 100).Concat(Range(97, 400)));
        var b = Frames(Range(0, 400), 0.1);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(Range(0, 400), Stream(r, a, b));
    }

    [Fact]
    public void Repeated_identical_frames_such_as_silence_keep_their_length()
    {
        // 200 identical silence frames in the middle; B starts 7 frames later than A.
        static List<MergeFrame> WithSilence(IEnumerable<int> positions, double err) =>
            [.. positions.Select(p => new MergeFrame(p is >= 100 and < 300 ? 1ul : (ulong)p + 1000, T0.AddSeconds(p * FrameSeconds + err)))];
        var a = WithSilence(Range(0, 500), 0);
        var b = WithSilence(Range(7, 520), 0.3);

        var r = FrameMerge.Merge(a, b);

        Assert.Equal(520, r.Blocks.Sum(x => x.Count));
        Assert.Equal(FrameSource.Primary, r.Blocks[0].Source);
        Assert.Equal((FrameSource.Secondary, 493, 20), (r.Blocks[^1].Source, r.Blocks[^1].Start, r.Blocks[^1].Count));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void A_recorder_with_nothing_contributes_nothing()
    {
        var a = Frames(Range(0, 50));

        var r = FrameMerge.Merge(a, []);

        Assert.Equal((FrameSource.Primary, 0, 50), (r.Blocks[0].Source, r.Blocks[0].Start, r.Blocks[0].Count));
        Assert.Empty(FrameMerge.Merge([], []).Blocks);
    }

    [Fact]
    public void Frame_index_lists_whole_frames_with_their_offsets_and_a_hash_of_their_bytes()
    {
        var first = Support.Mp3TestData.CbrFrame(1);
        var second = Support.Mp3TestData.CbrFrame(2);
        var bytes = first.Concat(second).Concat(first).ToArray();

        var frames = FrameIndex.Read(new MemoryStream(bytes), 0, bytes.Length);

        Assert.Equal([0L, first.Length, first.Length + second.Length], frames.Select(f => f.Offset));
        Assert.Equal([first.Length, second.Length, first.Length], frames.Select(f => f.Length));
        Assert.Equal(frames[0].Hash, frames[2].Hash);
        Assert.NotEqual(frames[0].Hash, frames[1].Hash);
    }
}
