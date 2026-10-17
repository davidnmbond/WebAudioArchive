using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WavFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("waa-wav-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Write_then_read_round_trips_16_bit_pcm()
    {
        var path = Path.Combine(_dir, "a.wav");
        var signal = Signals.Sine(1, rate: 16000);

        WavFile.WritePcm16(path, 16000, 1, signal);
        var samples = WavFile.ReadPcm16(path, out var info);

        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(1.0, info.Seconds, 6);
        Assert.Equal(16000, samples.Length);
        Assert.Equal((short)Math.Round(signal[1000] * 32767), samples[1000]);
    }

    [Fact]
    public void Extra_chunks_before_data_are_skipped()
    {
        var path = Path.Combine(_dir, "list.wav");
        var plain = WavFile.Build(16000, 1, new byte[3200]);
        // Insert a LIST chunk (odd length, padded) after fmt, as some encoders write.
        var list = "LIST"u8.ToArray().Concat(BitConverter.GetBytes(5)).Concat("INFOx"u8.ToArray()).Concat(new byte[] { 0 }).ToArray();
        var withList = plain.Take(36).Concat(list).Concat(plain.Skip(36)).ToArray();
        BitConverter.GetBytes(withList.Length - 8).CopyTo(withList, 4);
        File.WriteAllBytes(path, withList);

        var info = WavFile.ReadInfo(path);

        Assert.Equal(36 + list.Length + 8, info.DataOffset);
        Assert.Equal(3200, info.DataLength);
    }

    [Fact]
    public void Slice_returns_a_valid_wav_of_the_range_and_clamps_to_the_data()
    {
        var path = Path.Combine(_dir, "b.wav");
        WavFile.WritePcm16(path, 16000, 1, Signals.Sine(10, rate: 16000));
        var info = WavFile.ReadInfo(path);

        var slice = WavFile.Slice(path, info, 2.0, 3.5);
        var slicePath = Path.Combine(_dir, "slice.wav");
        File.WriteAllBytes(slicePath, slice);
        var sliceInfo = WavFile.ReadInfo(slicePath);

        Assert.Equal(1.5, sliceInfo.Seconds, 6);
        Assert.Equal(1.0, WavFile.ReadInfo(WriteBytes(WavFile.Slice(path, info, 9.0, 99.0))).Seconds, 6);
        Assert.Equal(0.0, WavFile.ReadInfo(WriteBytes(WavFile.Slice(path, info, 12.0, 13.0))).Seconds, 6);
    }

    [Fact]
    public void Not_a_wav_is_refused()
    {
        var path = Path.Combine(_dir, "x.wav");
        File.WriteAllBytes(path, "ID3 not a wav file at all"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WavFile.ReadInfo(path));
    }

    [Fact]
    public void Signals_have_the_levels_they_claim()
    {
        Assert.Equal(-45, Signals.Dbfs(Signals.Noise(5, -45, 1)), 0);
        Assert.Equal(-9.03, Signals.Dbfs(Signals.Sine(1)), 1);
        var speech = Signals.SpeechLike(30, 7);
        Assert.Equal(30 * Signals.Rate, speech.Length);
    }

    string WriteBytes(byte[] bytes)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
