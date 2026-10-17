using System.Buffers.Binary;
using System.Reflection;
using GroovyCodecs.Mp3;
using GroovyCodecs.Types;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// 128 kbps MP3 files made from synthetic signals with GroovyMp3 (a managed port of LAME, test project only), in a temp
/// folder, never committed. Nothing to install. LAME's default input scale of 0.95 is set to 1.0 so a planted full-scale
/// run stays at full scale (measured: with 0.95 a hard-clipped tone decodes at a peak of 0.968 and no sample reaches 0.98).
/// </summary>
public static class TestMp3
{
    public static Task EncodeMp3Async(string mp3Path, float[] interleaved, int channels, int rate = 44100)
    {
        File.WriteAllBytes(mp3Path, Encode(interleaved, channels, rate));
        return Task.CompletedTask;
    }

    public static byte[] Encode(float[] interleaved, int channels, int rate = 44100)
    {
        var format = new AudioFormat
        {
            SampleRate = rate,
            Channels = (short)channels,
            BitsPerSample = 16,
            BlockAlign = (short)(channels * 2),
            AverageBytesPerSecond = rate * channels * 2,
            BigEndian = false,
            IsFloatingPoint = false,
        };
        var encoder = new Mp3Encoder(format, 128, channels == 1 ? Mp3Encoder.CHANNEL_MODE_MONO : Mp3Encoder.CHANNEL_MODE_JOINT_STEREO, Mp3Encoder.QUALITY_MIDDLE, false);
        SetFullScale(encoder);

        var pcm = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(interleaved[i], -1f, 1f) * 32767));

        using var mp3 = new MemoryStream();
        var buffer = new byte[encoder.MP3BufferSize];
        for (var offset = 0; offset < pcm.Length; offset += encoder.PCMBufferSize)
        {
            var n = encoder.EncodeBuffer(pcm, offset, Math.Min(encoder.PCMBufferSize, pcm.Length - offset), buffer);
            mp3.Write(buffer, 0, n);
        }

        mp3.Write(buffer, 0, encoder.EncodeFinish(buffer));
        encoder.Close();
        return mp3.ToArray();
    }

    static void SetFullScale(Mp3Encoder encoder)
    {
        var flags = typeof(Mp3Encoder).GetField("gfp", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(encoder);
        var scale = flags?.GetType().GetField("scale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(scale is not null, "GroovyMp3's internal LAME flags (gfp.scale) were not found; this helper is written for GroovyMp3 0.1.3.");
        scale!.SetValue(flags, 1f);
    }
}
