namespace WebAudioArchive.Tests.Support;

/// <summary>Synthetic MPEG-1 Layer III frames: 128 kbps, 44.1 kHz, stereo. Payload bytes stay below 0x80 so they never form a sync word.</summary>
public static class Mp3TestData
{
    public static byte[] Frame(int seq)
    {
        var padded = seq % 3 == 0;
        var length = padded ? 418 : 417;
        var f = new byte[length];
        f[0] = 0xFF;
        f[1] = 0xFB;
        f[2] = padded ? (byte)0x92 : (byte)0x90;
        f[3] = 0x00;
        f[4] = (byte)((seq >> 21) & 0x7F);
        f[5] = (byte)((seq >> 14) & 0x7F);
        f[6] = (byte)((seq >> 7) & 0x7F);
        f[7] = (byte)(seq & 0x7F);
        for (var i = 8; i < length; i++) f[i] = (byte)((seq + i) & 0x7F);
        return f;
    }

    public static byte[] Frames(int count, int startSeq = 0)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < count; i++) ms.Write(Frame(startSeq + i));
        return ms.ToArray();
    }

    /// <summary>
    /// Like <see cref="Frame"/> but padded the way a real 128 kbps, 44.1 kHz encoder pads (417 bytes plus a padding byte
    /// in 42300 of every 44100 frames), so a run of them averages exactly 16000 bytes per second.
    /// </summary>
    public static byte[] CbrFrame(int seq)
    {
        var padded = (long)(seq + 1) * 42300 / 44100 > (long)seq * 42300 / 44100;
        var f = Frame(seq);
        if (padded == (f.Length == 418)) return f;
        var g = new byte[padded ? 418 : 417];
        f.AsSpan(0, Math.Min(f.Length, g.Length)).CopyTo(g);
        if (padded) g[^1] = (byte)((seq + g.Length - 1) & 0x7F);
        g[2] = padded ? (byte)0x92 : (byte)0x90;
        return g;
    }

    public static byte[] CbrFrames(int count, int startSeq = 0)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < count; i++) ms.Write(CbrFrame(startSeq + i));
        return ms.ToArray();
    }

    public static int SeqOf(ReadOnlySpan<byte> frame) =>
        (frame[4] << 21) | (frame[5] << 14) | (frame[6] << 7) | frame[7];
}
