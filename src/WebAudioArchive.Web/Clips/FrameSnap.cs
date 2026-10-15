using WebAudioArchive.Core.Mp3;

namespace WebAudioArchive.Web.Clips;

/// <summary>
/// Snaps an estimated byte offset to a real MP3 frame boundary. It resyncs from a little before the estimate: a
/// position counts as a frame start only if a valid header there is followed by valid headers at the computed frame
/// lengths for 3 consecutive frames (or the chain ends exactly at the end of the data). It then walks whole frames
/// forward to the estimate. A frame that is not complete within <c>length</c> (a .part still being written) is never
/// counted, so a clip never starts or ends mid-frame.
/// </summary>
public static class FrameSnap
{
    /// <summary>The largest Layer III frame: MPEG-1, 320 kbps, 32 kHz, padded.</summary>
    public const int MaxFrameLength = 1441;

    const int ChainFrames = 3;

    /// <summary>The last frame boundary at or before <paramref name="estimate"/>.</summary>
    public static long Floor(Stream s, long length, long estimate)
    {
        var (boundary, _) = FloorWithNext(s, length, estimate);
        return boundary;
    }

    /// <summary>The first frame boundary at or after <paramref name="estimate"/> (or the end of the last whole frame).</summary>
    public static long Ceil(Stream s, long length, long estimate)
    {
        var (boundary, next) = FloorWithNext(s, length, estimate);
        return boundary >= Math.Min(estimate, length) || next is null ? boundary : next.Value;
    }

    static (long Boundary, long? Next) FloorWithNext(Stream s, long length, long estimate)
    {
        estimate = Math.Clamp(estimate, 0, length);
        var scanStart = Math.Max(0, estimate - 3L * MaxFrameLength);
        var readEnd = Math.Min(length, estimate + (ChainFrames + 1L) * MaxFrameLength + Mp3FrameHeader.HeaderSize);
        var buf = new byte[readEnd - scanStart];
        s.Position = scanStart;
        s.ReadExactly(buf);

        // Positions are relative to buf; "end" is the end of the data the frames must fit in.
        var end = (int)(length - scanStart) <= buf.Length ? (int)(length - scanStart) : buf.Length;
        var atEof = readEnd == length;
        var p = -1;
        for (var i = 0; i < buf.Length - 1; i++)
        {
            if (buf[i] == 0xFF && (buf[i + 1] & 0xE0) == 0xE0 && IsChain(buf, i, end, atEof))
            {
                p = i;
                break;
            }
        }

        if (p < 0)
        {
            // No frame in reach: only possible for junk or a file shorter than one frame. Nothing whole to offer.
            return (scanStart, null);
        }

        var target = (int)(estimate - scanStart);
        var b = p;
        while (b < target && Mp3FrameHeader.TryParse(buf.AsSpan(b, Math.Min(Mp3FrameHeader.HeaderSize, buf.Length - b)), out var h)
               && b + h.FrameLength <= target && b + h.FrameLength <= end)
            b += h.FrameLength;

        long? next = null;
        if (b + Mp3FrameHeader.HeaderSize <= buf.Length && Mp3FrameHeader.TryParse(buf.AsSpan(b), out var nh) && b + nh.FrameLength <= end)
            next = scanStart + b + nh.FrameLength;
        return (scanStart + b, next);
    }

    static bool IsChain(byte[] buf, int start, int end, bool atEof)
    {
        var pos = start;
        for (var k = 0; k < ChainFrames; k++)
        {
            if (k > 0 && pos == end && atEof) return true;
            if (pos + Mp3FrameHeader.HeaderSize > buf.Length) return false;
            if (!Mp3FrameHeader.TryParse(buf.AsSpan(pos), out var h)) return false;
            if (pos + h.FrameLength > end) return false;
            pos += h.FrameLength;
        }

        return true;
    }
}

public sealed class ClipIntegrityException(string message) : Exception(message);

public readonly record struct WalkResult(int Frames, double Seconds, long Bytes);

public static class Mp3Walker
{
    /// <summary>Walks a stream frame by frame from its start; throws unless it is entirely whole, valid frames.</summary>
    public static WalkResult Walk(Stream s)
    {
        s.Position = 0;
        var buf = new byte[1 << 20];
        var count = 0;      // valid bytes in buf
        var at = 0;         // read position in buf
        long pos = 0;       // absolute position of buf[at]
        var frames = 0;
        double seconds = 0;
        var eof = false;
        while (true)
        {
            if (count - at < MaxNeeded && !eof)
            {
                Buffer.BlockCopy(buf, at, buf, 0, count - at);
                count -= at;
                at = 0;
                var n = s.ReadAtLeast(buf.AsSpan(count), buf.Length - count, throwOnEndOfStream: false);
                count += n;
                eof = n < buf.Length - (count - n);
            }

            if (at == count) break;
            if (count - at < Mp3FrameHeader.HeaderSize || !Mp3FrameHeader.TryParse(buf.AsSpan(at, count - at), out var h))
                throw new ClipIntegrityException($"No valid MP3 frame header at byte {pos}");
            if (h.FrameLength > count - at)
                throw new ClipIntegrityException($"The frame at byte {pos} is cut short (needs {h.FrameLength} bytes, {count - at} left)");
            at += h.FrameLength;
            pos += h.FrameLength;
            frames++;
            seconds += h.FrameSeconds;
        }

        return new WalkResult(frames, seconds, pos);
    }

    const int MaxNeeded = FrameSnap.MaxFrameLength + Mp3FrameHeader.HeaderSize;
}
