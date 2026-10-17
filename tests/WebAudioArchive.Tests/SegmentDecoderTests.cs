using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class SegmentDecoderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("waa-decode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string ScratchRoot => Path.Combine(_dir, "scratch");

    async Task<string> Mp3(float[] stereo, string name = "080000.mp3")
    {
        var p = Path.Combine(_dir, name);
        await TestMp3.EncodeMp3Async(p, stereo, 2);
        return p;
    }

    async Task<string> Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(p, bytes);
        return p;
    }

    static async Task<DecodedSegment> DecodeAsync(ScratchSpace scratch, string mp3, double seconds)
    {
        var length = new FileInfo(mp3).Length;
        using var lease = scratch.Acquire(Path.GetFileNameWithoutExtension(mp3), SegmentDecoder.ScratchBytesFor(length, seconds));
        return await new SegmentDecoder().DecodeAsync(mp3, length, 44100, 2, lease, CancellationToken.None);
    }

    [Fact]
    public async Task Decode_gives_16k_mono_speech_and_full_rate_features_from_a_scratch_copy()
    {
        var mp3 = await Mp3(Signals.Stereo(Signals.Concat(Signals.Sine(3), Signals.Silence(4), Signals.Sine(3))));
        var before = await File.ReadAllBytesAsync(mp3);
        var written = File.GetLastWriteTimeUtc(mp3);
        var scratch = new ScratchSpace(ScratchRoot, 512L * 1024 * 1024);
        var decoder = new SegmentDecoder();

        DecodedSegment decoded;
        using (var lease = scratch.Acquire("a/2026/10-16/080000", SegmentDecoder.ScratchBytesFor(before.Length, 10)))
        {
            decoded = await decoder.DecodeAsync(mp3, before.Length, 44100, 2, lease, CancellationToken.None);
            var info = WavFile.ReadInfo(decoded.SpeechWavPath);
            Assert.Equal(16000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.InRange(info.Seconds, 9.9, 10.1);
            Assert.Equal(decoded.Features.Seconds, info.Seconds, 3);
            Assert.StartsWith(scratch.Root, decoded.SpeechWavPath);
        }

        Assert.InRange(decoded.Features.Seconds, 9.9, 10.1);
        Assert.Equal(2, decoded.Features.Channels);
        Assert.Equal(AnalysisFixtures.CountFrames(mp3), decoded.Frames);
        Assert.Equal(decoded.Frames * Mp3FrameReader.SamplesPerFrame, decoded.Features.Frames);
        var dead = Assert.Single(DeadAirDetector.Detect(decoded.Features, new DeadAirOptions { MinSeconds = 3 }));
        Assert.InRange(dead.StartSeconds, 2.9, 3.15);
        Assert.InRange(dead.EndSeconds, 6.85, 7.1);
        Assert.StartsWith("NLayer 3.0.0", decoded.Decoder);

        Assert.Equal(before, await File.ReadAllBytesAsync(mp3));
        Assert.Equal(written, File.GetLastWriteTimeUtc(mp3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task Hard_clipping_survives_the_mp3_codec_where_it_was_planted()
    {
        var quiet = Signals.Sine(5, amp: 0.3);
        var loud = Signals.Sine(0.5, amp: 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
        var mp3 = await Mp3(Signals.Stereo(Signals.Concat(quiet, loud, Signals.Sine(4.5, amp: 0.3))));

        var decoded = await DecodeAsync(new ScratchSpace(ScratchRoot, 512L * 1024 * 1024), mp3, 10);
        var events = ClippingDetector.Detect(decoded.Features, new ClippingOptions { Threshold = 0.98 }).Events;

        Assert.NotEmpty(events);
        Assert.InRange(events[0].StartSeconds, 4.95, 5.1);
        Assert.All(events, e => Assert.InRange(e.StartSeconds, 4.9, 5.6));
    }

    [Fact]
    public async Task A_file_that_is_not_audio_fails_and_leaves_no_scratch()
    {
        var bad = Path.Combine(_dir, "bad.mp3");
        await File.WriteAllTextAsync(bad, string.Concat(Enumerable.Repeat("this is not audio. ", 300)));
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);

        var ex = await Assert.ThrowsAsync<DecodeException>(async () =>
        {
            using var lease = scratch.Acquire("bad", 1_000_000);
            await new SegmentDecoder().DecodeAsync(bad, new FileInfo(bad).Length, 44100, 2, lease, CancellationToken.None);
        });
        Assert.Contains("no MPEG audio frames", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task A_source_whose_size_disagrees_with_its_sidecar_is_refused()
    {
        var mp3 = await Mp3(Signals.Stereo(Signals.Sine(1)));
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);
        using var lease = scratch.Acquire("size", 1_000_000);

        var ex = await Assert.ThrowsAsync<DecodeException>(() =>
            new SegmentDecoder().DecodeAsync(mp3, new FileInfo(mp3).Length + 1, 44100, 2, lease, CancellationToken.None));
        Assert.Contains("sidecar says", ex.Message);
    }

    [Fact]
    public void The_scratch_budget_refuses_a_job_that_would_not_fit()
    {
        var scratch = new ScratchSpace(ScratchRoot, 1000);

        Assert.Throws<ScratchFullException>(() => scratch.Acquire("big", 2000));
        var first = scratch.Acquire("one", 500);
        Assert.Throws<ScratchFullException>(() => scratch.Acquire("two", 600));
        first.Dispose();
        scratch.Acquire("two", 600).Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public void Leftovers_from_a_crash_are_swept_at_start_up()
    {
        Directory.CreateDirectory(Path.Combine(ScratchRoot, "old-job-1a2b3c4d"));
        File.WriteAllText(Path.Combine(ScratchRoot, "old-job-1a2b3c4d", "speech.wav"), "x");
        File.WriteAllText(Path.Combine(ScratchRoot, "stray-1a2b3c4d"), "x");

        var scratch = new ScratchSpace(ScratchRoot, 1000);

        Assert.Equal(2, scratch.SweepStale());
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task A_long_stream_keeps_every_frame_through_the_reservoir_wrap()
    {
        // 330 s of the signal that, in the D18 spike, made NLayer's 8 KiB bit-reservoir ring wrap at frame boundaries
        // where the next frame needs the reservoir (NLayer's own MpegFile dropped 3 of its 12,635 frames).
        var random = new Random(7);
        var mono = new float[330 * Signals.Rate];
        for (var i = 0; i < mono.Length; i++)
        {
            var t = i / (double)Signals.Rate;
            var envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t);
            mono[i] = (float)(0.25 * envelope * (Math.Sin(2 * Math.PI * 180 * t) + 0.5 * Math.Sin(2 * Math.PI * 1300 * t)) + 0.05 * (random.NextDouble() - 0.5));
        }

        var mp3 = await Mp3(Signals.Stereo(mono), "long.mp3");
        var decoded = await DecodeAsync(new ScratchSpace(ScratchRoot, 512L * 1024 * 1024), mp3, 330);

        Assert.True(decoded.RecoveredFrames >= 1, "the stream never hit NLayer's reservoir wrap, so the recovery path was not exercised");
        Assert.Equal(0, decoded.SilencedFrames);
        Assert.Equal(AnalysisFixtures.CountFrames(mp3), decoded.Frames);
        Assert.Equal(decoded.Frames * Mp3FrameReader.SamplesPerFrame, decoded.Features.Frames);
        Assert.InRange(decoded.Features.Seconds, 330, 330.1);
    }

    [Fact]
    public async Task Stray_bytes_and_a_corrupt_frame_keep_the_timeline()
    {
        var clean = await File.ReadAllBytesAsync(await Mp3(Signals.Stereo(Signals.SpeechLike(10, 4)), "clean.mp3"));
        var scratch = new ScratchSpace(ScratchRoot, 512L * 1024 * 1024);
        var reference = await DecodeAsync(scratch, await Write("reference.mp3", clean), 10);

        // 1000 bytes of false frame headers (FF FB 90 00) in front: no second header follows any of them, so all are skipped.
        var junk = Enumerable.Repeat(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 250).SelectMany(b => b).ToArray();
        var damaged = junk.Concat(clean).ToArray();
        // and the audio data of the 101st frame zeroed, its header and side information kept (128 kbps frames are 417 or 418 bytes)
        var at = 0;
        for (var k = 0; k < 100; k++) at += 417 + ((clean[at + 2] >> 1) & 1);
        Array.Clear(damaged, junk.Length + at + 40, 300);

        var decoded = await DecodeAsync(scratch, await Write("damaged.mp3", damaged), 10);

        Assert.Equal(reference.Frames, decoded.Frames);
        Assert.Equal(reference.Features.Frames, decoded.Features.Frames);
    }

    [Fact]
    public void Sweeping_keeps_anything_that_is_not_a_scratch_job_folder()
    {
        Directory.CreateDirectory(Path.Combine(ScratchRoot, "job-1a2b3c4d"));
        Directory.CreateDirectory(Path.Combine(ScratchRoot, "photos"));
        Directory.CreateDirectory(Path.Combine(ScratchRoot, "job-nothex12"));
        File.WriteAllText(Path.Combine(ScratchRoot, "photos", "me.jpg"), "x");
        File.WriteAllText(Path.Combine(ScratchRoot, "notes.txt"), "x");

        var scratch = new ScratchSpace(ScratchRoot, 1000);

        Assert.Equal(1, scratch.SweepStale());
        Assert.False(Directory.Exists(Path.Combine(ScratchRoot, "job-1a2b3c4d")));
        Assert.True(File.Exists(Path.Combine(ScratchRoot, "photos", "me.jpg")));
        Assert.True(Directory.Exists(Path.Combine(ScratchRoot, "job-nothex12")));
        Assert.True(File.Exists(Path.Combine(ScratchRoot, "notes.txt")));
    }

    [Fact]
    public void A_scratch_root_that_equals_or_contains_a_protected_root_is_refused()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);

        var same = Assert.Throws<ArgumentException>(() => new ScratchSpace(data, 1000, [data]));
        Assert.Contains("scratch", same.Message);
        Assert.Throws<ArgumentException>(() => new ScratchSpace(_dir, 1000, [data]));
        Assert.Throws<ArgumentException>(() => new ScratchSpace(Path.Combine(_dir, "data") + Path.DirectorySeparatorChar, 1000, [data]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data));
        new ScratchSpace(ScratchRoot, 1000, [data]);
    }

    [Fact]
    public async Task A_source_longer_than_its_sidecar_is_copied_only_up_to_the_sidecar_and_refused()
    {
        var mp3 = await Mp3(Signals.Stereo(Signals.Sine(1)));
        var size = new FileInfo(mp3).Length;
        await File.AppendAllTextAsync(mp3, new string('x', 100_000));
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);
        using var lease = scratch.Acquire("grown", 1_000_000);

        var ex = await Assert.ThrowsAsync<DecodeException>(() => new SegmentDecoder().DecodeAsync(mp3, size, 44100, 2, lease, CancellationToken.None));

        Assert.Contains("longer than", ex.Message);
        Assert.True(new FileInfo(lease.PathFor("source.mp3")).Length <= size + 1);
    }

    [Fact]
    public async Task An_open_part_file_is_never_read()
    {
        var part = Path.Combine(_dir, "080000.mp3.part");
        File.Copy(await Mp3(Signals.Stereo(Signals.Sine(1))), part);
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);
        using var lease = scratch.Acquire("part", 1_000_000);

        var ex = await Assert.ThrowsAsync<DecodeException>(() => new SegmentDecoder().DecodeAsync(part, new FileInfo(part).Length, 44100, 2, lease, CancellationToken.None));

        Assert.Contains(".part", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(lease.Dir));
    }
}
