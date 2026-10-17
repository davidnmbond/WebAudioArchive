using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WebAudioArchive.Core.Mp3;

/// <summary>One whole MP3 frame in a file: where it is and a hash of its bytes, which identifies it across recorders.</summary>
public readonly record struct FrameRef(long Offset, int Length, ulong Hash);

/// <summary>Lists the whole frames in a byte range of an MP3 file. Read-only; bytes that are not a frame are skipped.</summary>
public static class FrameIndex
{
    /// <summary>
    /// Read in large chunks: segments are read over Docker's file sharing on Windows, where every read call is slow, so
    /// reading frame by frame made a four hour clip take minutes.
    /// </summary>
    const int ChunkBytes = 1 << 20;

    public static List<FrameRef> Read(Stream s, long from, long to)
    {
        var frames = new List<FrameRef>();
        var buffer = new byte[ChunkBytes];
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        var bufferStart = from; // file offset of buffer[0]
        var filled = 0;
        var at = 0; // parse position in the buffer
        s.Position = from;
        while (true)
        {
            // Keep the unparsed tail and top the buffer up whenever a whole frame might not fit in what is left.
            if (filled - at < 2048 && bufferStart + filled < to)
            {
                Array.Copy(buffer, at, buffer, 0, filled - at);
                bufferStart += at;
                filled -= at;
                at = 0;
                var want = (int)Math.Min(buffer.Length - filled, to - (bufferStart + filled));
                var got = s.ReadAtLeast(buffer.AsSpan(filled, want), want, throwOnEndOfStream: false);
                filled += got;
            }

            if (filled - at < Mp3FrameHeader.HeaderSize) break;
            if (!Mp3FrameHeader.TryParse(buffer.AsSpan(at), out var h) || h.FrameLength < Mp3FrameHeader.HeaderSize)
            {
                at++;
                continue;
            }

            if (at + h.FrameLength > filled) break;
            SHA1.HashData(buffer.AsSpan(at, h.FrameLength), hash);
            frames.Add(new FrameRef(bufferStart + at, h.FrameLength, BinaryPrimitives.ReadUInt64LittleEndian(hash)));
            at += h.FrameLength;
        }

        return frames;
    }
}
