using System.Buffers.Binary;
using System.Reflection;
using NAudio.Dsp;
using NLayer;
using WebAudioArchive.Analysis.Audio;

namespace WebAudioArchive.Analysis.Decode;

public sealed class DecodeException(string message) : Exception(message);

/// <summary>What one decode produced. <see cref="Decoder"/> (library versions) is recorded in every transcript.</summary>
public sealed record DecodedSegment(string SpeechWavPath, AudioFeatures Features, string Decoder, long Frames, int SilencedFrames, int RecoveredFrames);

/// <summary>
/// Copies a closed master segment into the job's scratch folder (the archive file is only ever opened for reading,
/// sharing read and write so nothing else is blocked), checks the copy against the sidecar's byte count, then decodes
/// the copy once, in managed code (decision D18): each frame's full-rate float PCM goes into the feature extractor for
/// the audio detectors (never written), and its 16 kHz mono resampling into a 16-bit WAV in scratch for whisper.
/// </summary>
public sealed class SegmentDecoder
{
    public const int SpeechRate = 16000;

    public static readonly string DecoderName =
        $"NLayer {Version(typeof(MpegFrameDecoder))} frame decoder, NAudio.Core {Version(typeof(WdlResampler))} WDL sinc resampler";

    /// <summary>Scratch needed for one segment: the copy, the speech WAV (32000 bytes a second) and 1 MB to spare.</summary>
    public static long ScratchBytesFor(long mp3Bytes, double audioSeconds) =>
        mp3Bytes + (long)Math.Ceiling(Math.Max(0, audioSeconds) + 10) * SpeechRate * 2 + 1_000_000;

    public async Task<DecodedSegment> DecodeAsync(string sourceAudio, long expectedBytes, int sampleRate, int channels, ScratchLease lease, CancellationToken ct)
    {
        if (sourceAudio.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            throw new DecodeException($"{sourceAudio} is an open recording (.part); only closed segments are analysed");

        // Copy at most one byte more than the sidecar says, so a file that is still growing cannot fill scratch.
        var copy = lease.PathFor("source.mp3");
        long copied = 0;
        await using (var src = new FileStream(sourceAudio, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
        await using (var dst = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            while (copied <= expectedBytes)
            {
                var n = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expectedBytes + 1 - copied)), ct);
                if (n == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                copied += n;
            }
        }

        if (copied > expectedBytes)
            throw new DecodeException($"{sourceAudio} is longer than its sidecar says ({expectedBytes} bytes); it is retried when they agree");
        if (copied != expectedBytes)
            throw new DecodeException($"{sourceAudio} is {copied} bytes but its sidecar says {expectedBytes}; it is retried when they agree");

        var wav = lease.PathFor("speech.wav");
        return await Task.Run(() => Decode(sourceAudio, copy, wav, sampleRate, channels, ct), ct);
    }

    static DecodedSegment Decode(string sourceAudio, string copy, string wav, int sampleRate, int channels, CancellationToken ct)
    {
        using var input = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(wav, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        output.Write(WavFile.Build(SpeechRate, 1, []));

        var reader = new Mp3FrameReader(input);
        var features = new FeatureExtractor(sampleRate, channels);
        var resampler = new SpeechResampler(sampleRate, channels, SpeechRate);
        var pcm = new float[Mp3FrameReader.MaxFloatsPerFrame];
        var bytes = new byte[8192];
        long written = 0;
        int n;
        while ((n = reader.ReadFrame(pcm)) > 0)
        {
            if (reader.Frames == 1 && (reader.SampleRate != sampleRate || reader.Channels != channels))
                throw new DecodeException($"{sourceAudio} is {reader.SampleRate} Hz with {reader.Channels} channel(s) but its sidecar says {sampleRate} Hz with {channels}");
            if (reader.Frames % 256 == 0) ct.ThrowIfCancellationRequested();
            features.Push(pcm.AsSpan(0, n));
            written += WritePcm16(output, resampler.Push(pcm.AsSpan(0, n)), bytes);
        }

        if (reader.Frames == 0) throw new DecodeException($"{sourceAudio}: no MPEG audio frames found (not an MP3, or empty)");
        var total = (long)Math.Round(reader.Frames * (double)Mp3FrameReader.SamplesPerFrame * SpeechRate / sampleRate);
        written += WritePcm16(output, resampler.Flush(total), bytes);

        var header = new byte[4];
        output.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(36 + written * 2));
        output.Write(header);
        output.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(written * 2));
        output.Write(header);

        return new DecodedSegment(wav, features.Complete(), DecoderName, reader.Frames, reader.SilencedFrames, reader.RecoveredFrames);
    }

    static long WritePcm16(Stream output, ReadOnlySpan<float> mono, byte[] bytes)
    {
        for (var start = 0; start < mono.Length; start += bytes.Length / 2)
        {
            var count = Math.Min(mono.Length - start, bytes.Length / 2);
            for (var i = 0; i < count; i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(Math.Clamp(mono[start + i], -1f, 1f) * 32767));
            output.Write(bytes, 0, count * 2);
        }

        return mono.Length;
    }

    static string Version(Type t) =>
        (t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? t.Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];
}
