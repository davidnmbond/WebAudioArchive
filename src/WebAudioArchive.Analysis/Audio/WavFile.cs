using System.Buffers.Binary;
using System.Text;

namespace WebAudioArchive.Analysis.Audio;

public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, int FormatTag, long DataOffset, long DataLength)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    public long Frames => BlockAlign == 0 ? 0 : DataLength / BlockAlign;

    public double Seconds => SampleRate == 0 ? 0 : (double)Frames / SampleRate;
}

/// <summary>Minimal RIFF WAVE support for the 16 kHz mono PCM that whisper takes. Chunks other than fmt and data are skipped.</summary>
public static class WavFile
{
    public static WavInfo ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        var header = new byte[12];
        if (fs.Read(header) != 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
            throw new InvalidDataException($"{path}: not a RIFF WAVE file");

        int? rate = null, channels = null, bits = null, tag = null;
        var chunk = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            fs.ReadExactly(chunk);
            var id = Encoding.ASCII.GetString(chunk, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            var start = fs.Position;
            if (id == "fmt ")
            {
                var fmt = new byte[16];
                fs.ReadExactly(fmt);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
            }
            else if (id == "data")
            {
                if (rate is null || channels is null || bits is null || tag is null)
                    throw new InvalidDataException($"{path}: data chunk before fmt chunk");
                var length = Math.Min(size, fs.Length - start);
                return new WavInfo(rate.Value, channels.Value, bits.Value, tag.Value, start, length);
            }

            fs.Position = start + size + (size % 2);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    /// <summary>A complete WAV (header and samples) for [startSeconds, endSeconds) of a 16-bit PCM file, clamped to its data.</summary>
    public static byte[] Slice(string path, WavInfo info, double startSeconds, double endSeconds)
    {
        if (info.FormatTag != 1 || info.BitsPerSample != 16)
            throw new InvalidDataException($"{path}: only 16-bit PCM WAV can be sliced (format {info.FormatTag}, {info.BitsPerSample} bits)");
        var first = Math.Clamp((long)Math.Round(startSeconds * info.SampleRate), 0, info.Frames);
        var last = Math.Clamp((long)Math.Round(endSeconds * info.SampleRate), first, info.Frames);
        var data = new byte[checked((int)((last - first) * info.BlockAlign))];
        using (var fs = File.OpenRead(path))
        {
            fs.Position = info.DataOffset + first * info.BlockAlign;
            fs.ReadExactly(data);
        }

        return Build(info.SampleRate, info.Channels, data);
    }

    public static byte[] Build(int sampleRate, int channels, ReadOnlySpan<byte> pcm16)
    {
        var bytes = new byte[44 + pcm16.Length];
        var s = bytes.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(36 + pcm16.Length));
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)pcm16.Length);
        pcm16.CopyTo(s[44..]);
        return bytes;
    }

    /// <summary>Writes interleaved float samples (clamped to -1..1) as 16-bit PCM. Used by tests and the benchmark.</summary>
    public static void WritePcm16(string path, int sampleRate, int channels, ReadOnlySpan<float> interleaved)
    {
        var pcm = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
        {
            var v = (short)Math.Round(Math.Clamp(interleaved[i], -1f, 1f) * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), v);
        }

        File.WriteAllBytes(path, Build(sampleRate, channels, pcm));
    }

    public static short[] ReadPcm16(string path, out WavInfo info)
    {
        info = ReadInfo(path);
        if (info.FormatTag != 1 || info.BitsPerSample != 16) throw new InvalidDataException($"{path}: not 16-bit PCM");
        var data = new byte[checked((int)info.DataLength)];
        using (var fs = File.OpenRead(path))
        {
            fs.Position = info.DataOffset;
            fs.ReadExactly(data);
        }

        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2));
        return samples;
    }
}
