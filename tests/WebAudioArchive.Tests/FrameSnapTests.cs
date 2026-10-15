extern alias web;

using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class FrameSnapTests
{
    static readonly byte[] Data = Mp3TestData.Frames(200);
    static readonly List<long> Bounds = SegmentFiles.Boundaries(Data);

    static long ExpectedFloor(long est) => Bounds.Last(b => b <= est);
    static long ExpectedCeil(long est) => Bounds.First(b => b >= est);

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(416L)]
    [InlineData(417L)]
    [InlineData(418L)]
    [InlineData(5000L)]
    [InlineData(41_795L)]
    [InlineData(60_001L)]
    public void Floor_and_ceil_land_on_real_frame_boundaries_including_mid_frame_estimates(long estimate)
    {
        using var s = new MemoryStream(Data);

        Assert.Equal(ExpectedFloor(estimate), FrameSnap.Floor(s, Data.Length, estimate));
        Assert.Equal(ExpectedCeil(estimate), FrameSnap.Ceil(s, Data.Length, estimate));
    }

    [Fact]
    public void Every_offset_in_a_stretch_snaps_to_the_boundary_at_or_before_it()
    {
        using var s = new MemoryStream(Data);
        for (long est = 10_000; est < 12_000; est += 7)
            Assert.Equal(ExpectedFloor(est), FrameSnap.Floor(s, Data.Length, est));
    }

    [Fact]
    public void Estimates_past_the_end_give_the_end_of_the_last_whole_frame()
    {
        using var s = new MemoryStream(Data);
        Assert.Equal(Data.Length, FrameSnap.Floor(s, Data.Length, Data.Length + 10_000));
        Assert.Equal(Data.Length, FrameSnap.Ceil(s, Data.Length, Data.Length + 10_000));
    }

    [Fact]
    public void A_partly_written_last_frame_is_excluded()
    {
        // A .part file being appended to: the last frame is only half there.
        var growing = Data.AsSpan(0, Data.Length - 200).ToArray();
        using var s = new MemoryStream(growing);
        var lastWhole = Bounds.Last(b => b <= growing.Length);

        Assert.Equal(lastWhole, FrameSnap.Floor(s, growing.Length, growing.Length));
        Assert.Equal(lastWhole, FrameSnap.Ceil(s, growing.Length, growing.Length - 1));
    }

    [Fact]
    public void Resync_skips_junk_that_only_looks_like_a_header()
    {
        // A lone sync word inside a frame's payload must not be taken as a boundary: the chain check rejects it.
        var data = (byte[])Data.Clone();
        var inside = (int)Bounds[50] + 100;
        data[inside] = 0xFF;
        data[inside + 1] = 0xFB;
        data[inside + 2] = 0x90;
        data[inside + 3] = 0x00;
        using var s = new MemoryStream(data);

        Assert.Equal(Bounds[50], FrameSnap.Floor(s, data.Length, inside + 2));
        Assert.Equal(Bounds[51], FrameSnap.Ceil(s, data.Length, inside + 2));
    }

    [Fact]
    public void Walk_counts_whole_frames_and_rejects_a_torn_file()
    {
        using var whole = new MemoryStream(Data);
        var walk = Mp3Walker.Walk(whole);
        Assert.Equal(200, walk.Frames);
        Assert.Equal(200 * SegmentFiles.FrameSeconds, walk.Seconds, 6);

        using var torn = new MemoryStream(Data.AsSpan(0, Data.Length - 3).ToArray());
        Assert.Throws<ClipIntegrityException>(() => Mp3Walker.Walk(torn));
    }
}
