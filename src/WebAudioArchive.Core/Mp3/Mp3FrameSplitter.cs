namespace WebAudioArchive.Core.Mp3;

/// <summary>The span passed to the handler points into an internal buffer and is valid only during the call.</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> frame, in Mp3FrameHeader header);

public sealed class Mp3FrameSplitter
{
    byte[] _buf = new byte[16 * 1024];
    int _start;
    int _count;

    public long SkippedBytes { get; private set; }

    public void Push(ReadOnlySpan<byte> data, FrameHandler onFrame)
    {
        Append(data);
        while (TryTakeFrame(requireNext: true, onFrame)) { }
    }

    /// <summary>Emit a final whole frame if one is buffered, and count anything left over as skipped.</summary>
    public void Complete(FrameHandler onFrame)
    {
        while (TryTakeFrame(requireNext: false, onFrame)) { }
        SkippedBytes += _count;
        _start = 0;
        _count = 0;
    }

    // A candidate frame is accepted only when another valid header follows it, which rejects false sync words in junk.
    bool TryTakeFrame(bool requireNext, FrameHandler onFrame)
    {
        while (true)
        {
            var span = new ReadOnlySpan<byte>(_buf, _start, _count);
            var idx = FindSync(span);
            if (idx < 0)
            {
                var keep = span.Length > 0 && span[^1] == 0xFF ? 1 : 0;
                Skip(span.Length - keep);
                return false;
            }

            if (idx > 0)
            {
                Skip(idx);
                span = new ReadOnlySpan<byte>(_buf, _start, _count);
            }

            if (span.Length < Mp3FrameHeader.HeaderSize) return false;
            if (!Mp3FrameHeader.TryParse(span, out var header))
            {
                Skip(1);
                continue;
            }

            var need = header.FrameLength + (requireNext ? Mp3FrameHeader.HeaderSize : 0);
            if (span.Length < need) return false;
            if (requireNext && !Mp3FrameHeader.TryParse(span[header.FrameLength..], out _))
            {
                Skip(1);
                continue;
            }

            onFrame(span[..header.FrameLength], in header);
            Consume(header.FrameLength);
            return true;
        }
    }

    static int FindSync(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length - 1; i++)
            if (span[i] == 0xFF && (span[i + 1] & 0xE0) == 0xE0) return i;
        return -1;
    }

    void Skip(int n)
    {
        SkippedBytes += n;
        Consume(n);
    }

    void Consume(int n)
    {
        _start += n;
        _count -= n;
        if (_count == 0) _start = 0;
    }

    void Append(ReadOnlySpan<byte> data)
    {
        if (_start + _count + data.Length > _buf.Length)
        {
            if (_count + data.Length <= _buf.Length)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _count);
            }
            else
            {
                var bigger = new byte[Math.Max(_buf.Length * 2, _count + data.Length)];
                Buffer.BlockCopy(_buf, _start, bigger, 0, _count);
                _buf = bigger;
            }

            _start = 0;
        }

        data.CopyTo(_buf.AsSpan(_start + _count));
        _count += data.Length;
    }
}
