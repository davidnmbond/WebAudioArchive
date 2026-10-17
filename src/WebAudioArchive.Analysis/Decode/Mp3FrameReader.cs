using NLayer;

namespace WebAudioArchive.Analysis.Decode;

/// <summary>
/// Reads an MPEG-1 Layer III stream (the recorders' format) one frame at a time and decodes each frame with NLayer's
/// managed frame decoder. Every frame header found gives exactly 1152 samples per channel, so the decoded length is
/// always frames x 1152, the count the sidecar records. A frame that cannot be decoded is written as silence (its
/// place on the timeline is kept). NLayer 3.0.0's bit reservoir refuses a frame when its 8 KiB ring buffer wraps
/// exactly at a frame boundary (about once every 4.7 minutes at 128 kbps, measured on a real Burst hour); such a frame
/// is decoded exactly by a fresh decoder primed with the previous frames, whose output is thrown away.
/// NLayer's own MpegFile is not used: it skips those frames, shortening an hour by about 0.47 s.
/// </summary>
public sealed class Mp3FrameReader
{
    public const int SamplesPerFrame = 1152;

    /// <summary>The largest frame NLayer can return: 1152 stereo samples.</summary>
    public const int MaxFloatsPerFrame = SamplesPerFrame * 2;

    const int MaxFrameBytes = 1441 + 4;
    const int KeepFrames = 4;

    readonly Stream _source;
    readonly byte[] _buffer = new byte[1 << 16];
    readonly Queue<byte[]> _recent = new();
    readonly RawFrame _frame = new();
    int _have;
    int _pos;
    bool _eof;
    bool _inSync;
    MpegFrameDecoder _decoder = new();

    public Mp3FrameReader(Stream source) => _source = source;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public long Frames { get; private set; }

    /// <summary>Frames NLayer could not decode, written as silence (usually only the first, whose bit reservoir is in the previous file).</summary>
    public int SilencedFrames { get; private set; }

    /// <summary>Frames refused by NLayer's ring-buffer wrap and decoded by a primed fresh decoder.</summary>
    public int RecoveredFrames { get; private set; }

    public long SkippedBytes { get; private set; }

    /// <summary>Decodes the next frame into <paramref name="interleaved"/> (at least MaxFloatsPerFrame long). Returns the floats written, 0 at the end.</summary>
    public int ReadFrame(float[] interleaved)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interleaved.Length, MaxFloatsPerFrame);
        while (true)
        {
            Fill();
            var available = _have - _pos;
            if (available < 4) return 0;
            var span = _buffer.AsSpan(_pos, available);
            var length = RawFrame.LengthAt(span);
            // Out of sync, a header counts only when another header follows it (or the file ends), so stray 0xFFF bytes are skipped.
            var fits = length > 0 && length <= available;
            if (!fits || !(_inSync || (length + 4 <= available ? RawFrame.LengthAt(span[length..]) > 0 : _eof)))
            {
                if (fits || length == 0 || !_eof)
                {
                    _pos++;
                    SkippedBytes++;
                    _inSync = false;
                    continue;
                }

                return 0;
            }

            var bytes = span[..length].ToArray();
            _pos += length;
            _inSync = true;
            return Decode(bytes, interleaved);
        }
    }

    int Decode(byte[] bytes, float[] interleaved)
    {
        _frame.Load(bytes);
        var channels = _frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
        if (Frames == 0)
        {
            SampleRate = _frame.SampleRate;
            Channels = channels;
        }

        var expected = SamplesPerFrame * Channels;
        var got = channels == Channels ? TryDecode(_decoder, _frame, interleaved) : 0;
        if (got != expected && channels == Channels && _recent.Count > 0)
        {
            for (var prime = 2; prime <= KeepFrames && got != expected; prime++)
            {
                _decoder = new MpegFrameDecoder();
                var discard = new float[MaxFloatsPerFrame];
                foreach (var previous in _recent.Skip(Math.Max(0, _recent.Count - prime)))
                {
                    var p = new RawFrame();
                    p.Load(previous);
                    TryDecode(_decoder, p, discard);
                }

                _frame.Load(bytes);
                got = TryDecode(_decoder, _frame, interleaved);
            }

            if (got == expected) RecoveredFrames++;
        }

        _recent.Enqueue(bytes);
        if (_recent.Count > KeepFrames) _recent.Dequeue();
        if (got != expected)
        {
            Array.Clear(interleaved, 0, expected);
            SilencedFrames++;
        }

        Frames++;
        return expected;
    }

    static int TryDecode(MpegFrameDecoder decoder, RawFrame frame, float[] dest)
    {
        try
        {
            return decoder.DecodeFrame(frame, dest, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    void Fill()
    {
        if (_eof || _have - _pos >= 2 * MaxFrameBytes) return;
        Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _have - _pos);
        _have -= _pos;
        _pos = 0;
        while (_have < _buffer.Length)
        {
            var n = _source.Read(_buffer, _have, _buffer.Length - _have);
            if (n == 0)
            {
                _eof = true;
                break;
            }

            _have += n;
        }
    }

    /// <summary>One frame's bytes as NLayer's IMpegFrame: header fields, and the bits after the header and CRC.</summary>
    sealed class RawFrame : IMpegFrame
    {
        static readonly int[] Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        static readonly int[] Rates = [44100, 48000, 32000, 0];
        byte[] _data = [];
        int _offset;
        int _bits;
        ulong _bucket;

        /// <summary>The frame length for an MPEG-1 Layer III header at the start of <paramref name="b"/>, or 0.</summary>
        public static int LengthAt(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xFE) != 0xFA) return 0;
            var kbps = Kbps[b[2] >> 4];
            var rate = Rates[(b[2] >> 2) & 3];
            if (kbps == 0 || rate == 0) return 0;
            return 144 * kbps * 1000 / rate + ((b[2] >> 1) & 1);
        }

        public void Load(byte[] data)
        {
            _data = data;
            FrameLength = data.Length;
            BitRateIndex = data[2] >> 4;
            BitRate = Kbps[BitRateIndex] * 1000;
            SampleRateIndex = (data[2] >> 2) & 3;
            SampleRate = Rates[SampleRateIndex];
            HasCrc = (data[1] & 1) == 0;
            ChannelMode = (MpegChannelMode)(data[3] >> 6);
            ChannelModeExtension = (data[3] >> 4) & 3;
            IsCopyrighted = (data[3] & 8) != 0;
            Reset();
        }

        public int SampleRate { get; private set; }

        public int SampleRateIndex { get; private set; }

        public int FrameLength { get; private set; }

        public int BitRate { get; private set; }

        public MpegVersion Version => MpegVersion.Version1;

        public MpegLayer Layer => MpegLayer.LayerIII;

        public MpegChannelMode ChannelMode { get; private set; }

        public int ChannelModeExtension { get; private set; }

        public int SampleCount => SamplesPerFrame;

        public int BitRateIndex { get; private set; }

        public bool IsCopyrighted { get; private set; }

        public bool HasCrc { get; private set; }

        public bool IsCorrupted => false;

        public void Reset()
        {
            _offset = HasCrc ? 6 : 4;
            _bucket = 0;
            _bits = 0;
        }

        public int ReadBits(int bitCount)
        {
            while (_bits < bitCount)
            {
                if (_offset >= FrameLength) return -1;
                _bucket = (_bucket << 8) | _data[_offset++];
                _bits += 8;
            }

            var value = (int)((_bucket >> (_bits - bitCount)) & ((1UL << bitCount) - 1));
            _bits -= bitCount;
            return value;
        }
    }
}
