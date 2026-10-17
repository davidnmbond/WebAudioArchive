using System.Globalization;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests.Support;

public static class AnalysisFixtures
{
    /// <summary>
    /// A real, decodable segment in the recorder's layout: the mono signal encoded as a stereo 128 kbps MP3 at
    /// &lt;root&gt;/&lt;station&gt;/master/yyyy/MM-dd/HHmmss.mp3 with a sidecar beside it. Written to a temp folder, never committed.
    /// The sidecar's frame count is counted from the file by the frame splitter (not worked out from a formula). With
    /// <paramref name="burstSeconds"/> the audio is longer than the wall span by that much (the Revma start burst), and
    /// the wall span includes the gaps.
    /// </summary>
    public static async Task<string> WriteDecodableSegmentAsync(string recorderRoot, string station, DateTimeOffset start, float[] mono, IReadOnlyList<SegmentGap>? gaps = null, double burstSeconds = 0)
    {
        var utc = start.UtcDateTime;
        var dir = Path.Combine(recorderRoot, station, "master", utc.ToString("yyyy", CultureInfo.InvariantCulture), utc.ToString("MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var name = utc.ToString("HHmmss", CultureInfo.InvariantCulture) + ".mp3";
        var mp3 = Path.Combine(dir, name);
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(mono), 2);
        var seconds = mono.Length / (double)Signals.Rate;
        var gapSeconds = (gaps ?? []).Sum(g => (g.EndUtc - g.StartUtc).TotalSeconds);
        var end = start.AddSeconds(seconds - burstSeconds + gapSeconds);
        SegmentMetaStore.Write(Path.ChangeExtension(mp3, ".json"), new SegmentMeta(
            1, "home-a", station, name, start, end, new FileInfo(mp3).Length, CountFrames(mp3), seconds,
            new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], gaps ?? [], "hour-boundary"));
        return mp3;
    }

    /// <summary>The number of MPEG frames in a file, counted by the recorder's own frame splitter (independent of the decoder under test).</summary>
    public static int CountFrames(string mp3Path)
    {
        var splitter = new Mp3FrameSplitter();
        var n = 0;
        splitter.Push(File.ReadAllBytes(mp3Path), (ReadOnlySpan<byte> _, in Mp3FrameHeader _) => n++);
        splitter.Complete((ReadOnlySpan<byte> _, in Mp3FrameHeader _) => n++);
        return n;
    }

    /// <summary>What a transcriber would return for <paramref name="text"/> said from <paramref name="start"/> seconds: one word every 0.3 s.</summary>
    public static RawSegment Said(double start, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => new TranscriptWord(w, start + i * 0.3, start + i * 0.3 + 0.25, 0.9))
            .ToList();
        return new RawSegment(start, start + words.Count * 0.3, text, 0.01, -0.2, words);
    }
}
