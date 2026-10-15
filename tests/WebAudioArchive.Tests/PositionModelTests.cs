extern alias web;

using WebAudioArchive.Core.Recording;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class PositionModelTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    static CatalogSegment Seg(double wallSeconds, double audioSeconds, params SegmentGap[] gaps) => new(
        "a", "burst", "/x/150000.mp3", T0, T0.AddSeconds(wallSeconds), (long)(audioSeconds * 16000), audioSeconds, 128,
        gaps, "hour-boundary", IsOpen: false, FileLength: (long)(audioSeconds * 16000), LastWriteUtc: T0.UtcDateTime);

    [Fact]
    public void Without_a_burst_byte_offset_is_seconds_since_start_times_16000()
    {
        var s = Seg(3600, 3600);

        Assert.Equal(0, s.BurstSeconds, 6);
        Assert.Equal(T0, PositionModel.AudioStartUtc(s));
        Assert.Equal(0, PositionModel.Offset(s, T0));
        Assert.Equal(16000 * 600, PositionModel.Offset(s, T0.AddMinutes(10)));
        Assert.Equal(T0.AddMinutes(10), PositionModel.TimeAt(s, 16000 * 600));
    }

    [Fact]
    public void With_a_burst_byte_zero_is_burst_seconds_before_start()
    {
        // Revma: 214 s of audio in 209 s of wall time, so byte 0 holds audio from 5 s before the first arrival.
        var s = Seg(209, 214);

        Assert.Equal(5, s.BurstSeconds, 6);
        Assert.Equal(T0.AddSeconds(-5), PositionModel.AudioStartUtc(s));
        Assert.Equal(16000 * 5, PositionModel.Offset(s, T0));
        Assert.Equal(16000 * 65, PositionModel.Offset(s, T0.AddSeconds(60)));
        Assert.Equal(T0.AddSeconds(60), PositionModel.TimeAt(s, 16000 * 65));
    }

    [Fact]
    public void Offsets_are_clamped_to_the_file()
    {
        var s = Seg(100, 105);

        Assert.Equal(0, PositionModel.Offset(s, T0.AddSeconds(-60)));
        Assert.Equal(s.Bytes, PositionModel.Offset(s, T0.AddSeconds(500)));
    }

    [Fact]
    public void A_gap_inside_a_segment_does_not_advance_the_bytes()
    {
        // 100 s span with a 10 s gap at 40..50 and no replay: 90 s of audio.
        var s = Seg(100, 90, new SegmentGap(T0.AddSeconds(40), T0.AddSeconds(50), 10, "eof"));

        Assert.Equal(16000 * 40, PositionModel.Offset(s, T0.AddSeconds(40)));
        Assert.Equal(16000 * 40, PositionModel.Offset(s, T0.AddSeconds(45)));
        Assert.Equal(16000 * 50, PositionModel.Offset(s, T0.AddSeconds(60)));
        Assert.Equal(T0.AddSeconds(60), PositionModel.TimeAt(s, 16000 * 50));
    }
}
