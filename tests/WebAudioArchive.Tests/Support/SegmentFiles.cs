using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests.Support;

/// <summary>A synthetic segment written to disk: its audio bytes and where they are.</summary>
public sealed record WrittenSegment(string AudioPath, byte[] Bytes, int FirstSeq, int Frames, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double GapSeconds = 0)
{
    /// <summary>The start offset the web app derives from this segment: audio seconds minus (span minus gaps).</summary>
    public double BurstSeconds => Math.Max(0, Frames * SegmentFiles.FrameSeconds - ((EndUtc - StartUtc).TotalSeconds - GapSeconds));

    /// <summary>The estimated byte offset of a wall time, before frame snapping (no gaps).</summary>
    public long Estimate(DateTimeOffset t) => (long)Math.Round(((t - StartUtc).TotalSeconds + BurstSeconds) * 16000);
}

/// <summary>
/// Writes synthetic master segments in the recorder's layout: &lt;root&gt;/&lt;station&gt;/master/yyyy/MM-dd/HHmmss.mp3 plus a
/// .json sidecar, or an open HHmmss.mp3.part with no sidecar. Frames come from <see cref="Mp3TestData"/> (128 kbps, so
/// 16000 bytes per second on average) and carry consecutive sequence numbers so tests can check exactly which frames a
/// clip holds.
/// </summary>
public static class SegmentFiles
{
    public const double FrameSeconds = 1152.0 / 44100;

    public static int FramesFor(double seconds) => (int)Math.Round(seconds / FrameSeconds);

    /// <summary>
    /// A closed segment whose first frame arrived at <paramref name="start"/>, holding <paramref name="burstSeconds"/> of
    /// audio from before that (the Revma start offset) plus <paramref name="wallSeconds"/> of live audio, minus gaps.
    /// </summary>
    public static WrittenSegment Closed(
        string root, string station, DateTimeOffset start, double wallSeconds, int firstSeq,
        double burstSeconds = 0, string endedBy = "hour-boundary", IReadOnlyList<SegmentGap>? gaps = null, string? name = null)
    {
        gaps ??= [];
        var gapSeconds = gaps.Sum(g => (g.EndUtc - g.StartUtc).TotalSeconds);
        var frames = FramesFor(wallSeconds - gapSeconds + burstSeconds);
        var bytes = Mp3TestData.CbrFrames(frames, firstSeq);
        var path = AudioPath(root, station, start, name);
        File.WriteAllBytes(path, bytes);
        var end = start.AddSeconds(wallSeconds);
        SegmentMetaStore.Write(Path.ChangeExtension(path, ".json"), new SegmentMeta(
            1, "test", station, Path.GetFileName(path), start, end, bytes.Length, frames, frames * FrameSeconds,
            new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], gaps, endedBy));
        return new WrittenSegment(path, bytes, firstSeq, frames, start, end, gapSeconds);
    }

    /// <summary>An open segment (.mp3.part, no sidecar) whose last write time is <paramref name="lastWrite"/>.</summary>
    public static WrittenSegment Open(
        string root, string station, DateTimeOffset start, DateTimeOffset lastWrite, int firstSeq, double burstSeconds = 5, string? name = null)
    {
        var frames = FramesFor((lastWrite - start).TotalSeconds + burstSeconds);
        var bytes = Mp3TestData.CbrFrames(frames, firstSeq);
        var path = AudioPath(root, station, start, name) + ".part";
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return new WrittenSegment(path, bytes, firstSeq, frames, start, lastWrite);
    }

    static string AudioPath(string root, string station, DateTimeOffset start, string? name)
    {
        var dir = Path.Combine(root, station, "master", start.ToString("yyyy"), start.ToString("MM-dd"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, (name ?? start.ToString("HHmmss")) + ".mp3");
    }

    /// <summary>Sequence numbers of the frames in a byte buffer, walking from byte 0; throws if it is not whole frames.</summary>
    public static List<int> Seqs(byte[] data)
    {
        var seqs = new List<int>();
        var pos = 0;
        while (pos < data.Length)
        {
            if (!WebAudioArchive.Core.Mp3.Mp3FrameHeader.TryParse(data.AsSpan(pos), out var h) || pos + h.FrameLength > data.Length)
                throw new InvalidOperationException($"Not a whole frame at byte {pos}");
            seqs.Add(Mp3TestData.SeqOf(data.AsSpan(pos)));
            pos += h.FrameLength;
        }

        return seqs;
    }

    /// <summary>Byte offsets of every frame boundary in a buffer of whole frames, including the end.</summary>
    public static List<long> Boundaries(byte[] data)
    {
        var list = new List<long> { 0 };
        var pos = 0;
        while (pos < data.Length)
        {
            WebAudioArchive.Core.Mp3.Mp3FrameHeader.TryParse(data.AsSpan(pos), out var h);
            pos += h.FrameLength;
            list.Add(pos);
        }

        return list;
    }
}
