namespace WebAudioArchive.Core.Mp3;

public readonly record struct Mp3FrameHeader(
    int MpegVersion,
    int BitrateKbps,
    int SampleRateHz,
    int Channels,
    bool Padding,
    int FrameLength,
    int SamplesPerFrame)
{
    public const int HeaderSize = 4;

    static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    static readonly int[] RatesV1 = [44100, 48000, 32000];
    static readonly int[] RatesV2 = [22050, 24000, 16000];
    static readonly int[] RatesV25 = [11025, 12000, 8000];

    public string Codec => MpegVersion switch
    {
        1 => "MPEG-1 Layer III",
        2 => "MPEG-2 Layer III",
        _ => "MPEG-2.5 Layer III",
    };

    public double FrameSeconds => (double)SamplesPerFrame / SampleRateHz;

    public static bool TryParse(ReadOnlySpan<byte> b, out Mp3FrameHeader header)
    {
        header = default;
        if (b.Length < HeaderSize || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return false;

        var versionBits = (b[1] >> 3) & 3;
        var layerBits = (b[1] >> 1) & 3;
        if (versionBits == 1 || layerBits != 1) return false;

        var bitrateIndex = b[2] >> 4;
        var rateIndex = (b[2] >> 2) & 3;
        if (bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3) return false;

        var v1 = versionBits == 3;
        var bitrate = (v1 ? BitratesV1 : BitratesV2)[bitrateIndex];
        var rate = (versionBits == 3 ? RatesV1 : versionBits == 2 ? RatesV2 : RatesV25)[rateIndex];
        var padding = ((b[2] >> 1) & 1) == 1;
        var length = (v1 ? 144 : 72) * bitrate * 1000 / rate + (padding ? 1 : 0);
        var version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25;
        var channels = (b[3] >> 6) == 3 ? 1 : 2;

        header = new Mp3FrameHeader(version, bitrate, rate, channels, padding, length, v1 ? 1152 : 576);
        return true;
    }
}
