using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class Mp3FrameSplitterTests
{
    static List<byte[]> Split(byte[] data, int chunk, out long skipped)
    {
        var frames = new List<byte[]>();
        var splitter = new Mp3FrameSplitter();
        FrameHandler handler = (ReadOnlySpan<byte> f, in Mp3FrameHeader hdr) => frames.Add(f.ToArray());
        for (var i = 0; i < data.Length; i += chunk)
            splitter.Push(data.AsSpan(i, Math.Min(chunk, data.Length - i)), handler);
        splitter.Complete(handler);
        skipped = splitter.SkippedBytes;
        return frames;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(417)]
    [InlineData(1000)]
    [InlineData(1_000_000)]
    public void Whole_frames_round_trip(int chunk)
    {
        var data = Mp3TestData.Frames(50);
        var frames = Split(data, chunk, out var skipped);

        Assert.Equal(50, frames.Count);
        for (var i = 0; i < 50; i++) Assert.Equal(Mp3TestData.Frame(i), frames[i]);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Resyncs_after_garbage_including_a_false_sync_word(int chunk)
    {
        // A valid-looking header followed by junk: must not be taken as a frame.
        var junk = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.Concat(Enumerable.Repeat((byte)0x11, 500)).ToArray();
        var data = junk.Concat(Mp3TestData.Frames(20)).ToArray();

        var frames = Split(data, chunk, out var skipped);

        Assert.Equal(20, frames.Count);
        Assert.Equal(Mp3TestData.Frame(0), frames[0]);
        Assert.Equal(junk.Length, skipped);
    }

    [Fact]
    public void Truncated_last_frame_is_dropped_and_counted()
    {
        var data = Mp3TestData.Frames(3).Concat(Mp3TestData.Frame(3).Take(100)).ToArray();

        var frames = Split(data, 1000, out var skipped);

        Assert.Equal(3, frames.Count);
        Assert.Equal(100, skipped);
    }

    [Fact]
    public void Header_fields_are_decoded()
    {
        Assert.True(Mp3FrameHeader.TryParse(Mp3TestData.Frame(1), out var h));
        Assert.Equal(1, h.MpegVersion);
        Assert.Equal(128, h.BitrateKbps);
        Assert.Equal(44100, h.SampleRateHz);
        Assert.Equal(2, h.Channels);
        Assert.Equal(417, h.FrameLength);
        Assert.Equal(1152, h.SamplesPerFrame);
        Assert.Equal("MPEG-1 Layer III", h.Codec);

        Assert.True(Mp3FrameHeader.TryParse(Mp3TestData.Frame(0), out var padded));
        Assert.Equal(418, padded.FrameLength);
    }

    [Fact]
    public void Non_layer3_header_is_rejected()
    {
        // 0xFD in byte 1 selects Layer II.
        Assert.False(Mp3FrameHeader.TryParse(new byte[] { 0xFF, 0xFD, 0x90, 0x00 }, out _));
    }
}
