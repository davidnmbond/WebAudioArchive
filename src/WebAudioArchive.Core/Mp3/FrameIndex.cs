using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WebAudioArchive.Core.Mp3;

/// <summary>One whole MP3 frame in a file: where it is and a hash of its bytes, which identifies it across recorders.</summary>
public readonly record struct FrameRef(long Offset, int Length, ulong Hash);

/// <summary>Lists the whole frames in a byte range of an MP3 file. Read-only; bytes that are not a frame are skipped.</summary>
public static class FrameIndex
{
    public static List<FrameRef> Read(Stream s, long from, long to)
    {
        var frames = new List<FrameRef>();
        var buffer = new byte[2048];
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        var pos = from;
        while (pos + Mp3FrameHeader.HeaderSize <= to)
        {
            s.Position = pos;
            if (s.ReadAtLeast(buffer.AsSpan(0, Mp3FrameHeader.HeaderSize), Mp3FrameHeader.HeaderSize, throwOnEndOfStream: false) < Mp3FrameHeader.HeaderSize)
                break;
            if (!Mp3FrameHeader.TryParse(buffer, out var h) || h.FrameLength > buffer.Length || h.FrameLength < Mp3FrameHeader.HeaderSize)
            {
                pos++;
                continue;
            }

            if (pos + h.FrameLength > to) break;
            var rest = h.FrameLength - Mp3FrameHeader.HeaderSize;
            if (s.ReadAtLeast(buffer.AsSpan(Mp3FrameHeader.HeaderSize, rest), rest, throwOnEndOfStream: false) < rest) break;

            SHA1.HashData(buffer.AsSpan(0, h.FrameLength), hash);
            frames.Add(new FrameRef(pos, h.FrameLength, BinaryPrimitives.ReadUInt64LittleEndian(hash)));
            pos += h.FrameLength;
        }

        return frames;
    }
}
