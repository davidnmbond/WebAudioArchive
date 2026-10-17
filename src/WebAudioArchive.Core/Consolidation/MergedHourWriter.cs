using System.Security.Cryptography;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>A merged hour failed one of its checks (named in the message); nothing was published.</summary>
public sealed class MergeVerificationException(string message) : Exception(message);

/// <summary>
/// Writes a planned merged hour (issue #104) as &lt;yyyy&gt;/&lt;MM-dd&gt;/&lt;HHmmss&gt;.mp3 under a merged station master folder.
/// The audio is built as a .part, checked against the plan and its sources, and only then given its sidecar and its
/// final name, so a reader never sees a merged hour that was not verified.
/// </summary>
public static class MergedHourWriter
{
    const int CopyBufferBytes = 1 << 20;

    public static SegmentMeta Write(MergedHourPlan plan, string mergedStationMasterDir, string station)
    {
        var hour = plan.Hour.ToUniversalTime();
        var dir = Path.Combine(mergedStationMasterDir, hour.ToString("yyyy"), hour.ToString("MM-dd"));
        var mp3 = Path.Combine(dir, hour.ToString("HHmmss") + ".mp3");
        var part = mp3 + ".part";
        Directory.CreateDirectory(dir);
        if (File.Exists(part)) File.Delete(part); // left by an interrupted run

        try
        {
            var sources = plan.Pieces.Select(p => p.Source).Distinct().ToList();
            var before = sources.Select(CheckSource).ToList();

            using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var piece in plan.Pieces) Copy(piece, output);
            }

            var bytes = VerifyAudio(plan, part);
            foreach (var (source, input) in sources.Zip(before))
                if (CheckSource(source) != input) throw new MergeVerificationException($"source {source.Recorder} {source.RelativeFile} changed while the hour was written");

            var meta = BuildMeta(plan, station, Path.GetFileName(mp3), bytes, before, Sha256(part));
            SegmentMetaStore.Write(Path.ChangeExtension(mp3, ".json"), meta);
            File.Move(part, mp3, overwrite: true);
            return meta;
        }
        catch
        {
            if (File.Exists(part)) File.Delete(part);
            throw;
        }
    }

    /// <summary>
    /// The frame hashes the next hour is joined on: the file's trailing run of identical frames (silence), however long,
    /// and the <paramref name="frames"/> frames before that run. A file that ends on a frame unlike the one before it
    /// gives just its last <paramref name="frames"/> hashes. A fixed few hashes inside silence match at many places.
    /// </summary>
    public static IReadOnlyList<ulong> Tail(string mergedMp3, int frames = 8)
    {
        using var fs = new FileStream(mergedMp3, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hashes = FrameIndex.Read(fs, 0, fs.Length).Select(f => f.Hash).ToList();
        if (hashes.Count == 0) return hashes;

        var run = 1;
        while (run < hashes.Count && hashes[^(run + 1)] == hashes[^1]) run++;
        var take = run == 1 ? frames : run + frames;
        return [.. hashes.TakeLast(take)];
    }

    static SegmentMeta BuildMeta(
        MergedHourPlan plan, string station, string file, long bytes, List<MergeInput> inputs, string sha256)
    {
        var first = plan.Pieces[0].Source.Meta;
        var frames = plan.FrameHashes.Count;
        var samplesPerFrame = first.Format is { SampleRateHz: < 32000 } ? 576 : 1152;
        var audioSeconds = first.Format is { SampleRateHz: > 0 } f ? frames * samplesPerFrame / (double)f.SampleRateHz : 0;
        var merge = new MergeInfo(
            [.. plan.Pieces.Select(p => new MergeSource(p.Recorder, p.Source.RelativeFile, p.FirstFrame, p.Frames, p.FromUtc, p.ToUtc))],
            inputs,
            sha256,
            plan.Pieces.Where(p => p.Recorder == "a").Sum(p => p.Frames),
            plan.Pieces.Where(p => p.Recorder != "a").Sum(p => p.Frames));
        return new SegmentMeta(
            1, "merged", station, file, plan.Pieces[0].FromUtc, plan.Pieces[^1].ToUtc, bytes, frames, audioSeconds,
            first.Format, null, [], plan.Missing, "hour-boundary", merge);
    }

    /// <summary>The source as it is now. It must be exactly the closed segment its sidecar describes.</summary>
    static MergeInput CheckSource(SourceSegment source)
    {
        var length = new FileInfo(source.AudioPath).Length;
        if (length != source.Meta.Bytes)
            throw new MergeVerificationException($"source {source.Recorder} {source.RelativeFile} is {length} bytes but its sidecar says {source.Meta.Bytes}");
        return new MergeInput(source.Recorder, source.RelativeFile, Sha256(source.AudioPath), length);
    }

    static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    static void Copy(MergedPiece piece, Stream output)
    {
        using var input = new FileStream(piece.Source.AudioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        input.Position = piece.StartByte;
        var buffer = new byte[CopyBufferBytes];
        var left = piece.EndByte - piece.StartByte;
        while (left > 0)
        {
            var got = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (got == 0) throw new MergeVerificationException($"source {piece.Source.Recorder} {piece.Source.RelativeFile} ended before byte {piece.EndByte}");
            output.Write(buffer, 0, got);
            left -= got;
        }
    }

    /// <summary>The written audio must be whole frames from its first byte to its last, and exactly the frames planned. Returns its length.</summary>
    static long VerifyAudio(MergedHourPlan plan, string part)
    {
        using var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frames = FrameIndex.Read(fs, 0, fs.Length);
        long at = 0;
        foreach (var frame in frames)
        {
            if (frame.Offset != at) throw new MergeVerificationException($"frames: the merged audio has bytes that are not a frame at byte {at}");
            at += frame.Length;
        }

        if (at != fs.Length) throw new MergeVerificationException($"frames: the merged audio ends with bytes that are not a whole frame at byte {at}");
        if (!frames.Select(f => f.Hash).SequenceEqual(plan.FrameHashes)) throw new MergeVerificationException("frames: the merged audio does not hold the planned frames");
        return fs.Length;
    }
}
