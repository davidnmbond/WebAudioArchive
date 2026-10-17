using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class MergedSidecarTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-merged-" + Guid.NewGuid().ToString("N"));

    public MergedSidecarTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_merged_sidecar_round_trips_its_sources_inputs_and_checksum()
    {
        var start = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero);
        var mid = start.AddMinutes(30);
        var end = start.AddHours(1);
        var gap = new SegmentGap(mid, mid.AddSeconds(2), 2, "not recorded by either recorder");
        var merge = new MergeInfo(
            [
                new MergeSource("a", "2026/10-16/140000.mp3", 0, 1000, start, mid),
                new MergeSource("b", "2026/10-16/140000.mp3", 1010, 2000, mid.AddSeconds(2), end),
            ],
            [
                new MergeInput("a", "2026/10-16/140000.mp3", new string('a', 64), 16000),
                new MergeInput("b", "2026/10-16/140000.mp3", new string('b', 64), 32000),
            ],
            new string('c', 64), 1000, 2000);
        var meta = new SegmentMeta(
            1, "merged", "test", "140000.mp3", start, end, 48000, 3000, 78.4,
            new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], [gap], "hour-boundary", merge);
        var path = Path.Combine(_root, "140000.json");

        SegmentMetaStore.Write(path, meta);
        var read = SegmentMetaStore.Read(path)!;

        Assert.Contains("\"merge\":", File.ReadAllText(path));
        Assert.Equal("merged", read.Instance);
        Assert.NotNull(read.Merge);
        Assert.Equal(merge.Sources, read.Merge.Sources);
        Assert.Equal(merge.Inputs, read.Merge.Inputs);
        Assert.Equal(merge.Sha256, read.Merge.Sha256);
        Assert.Equal(1000, read.Merge.FramesFromA);
        Assert.Equal(2000, read.Merge.FramesFromB);
    }

    [Fact]
    public void An_existing_sidecar_without_a_merge_block_still_reads()
    {
        var start = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero);
        var seg = SegmentFiles.Closed(_root, "test", start, 60, 0);

        var json = File.ReadAllText(Path.ChangeExtension(seg.AudioPath, ".json"));
        var read = SegmentMetaStore.Read(Path.ChangeExtension(seg.AudioPath, ".json"))!;

        Assert.DoesNotContain("\"merge\":", json.Replace("\"merge\": null", ""));
        Assert.Null(read.Merge);
        Assert.Equal("test", read.Instance);
        Assert.Equal("test", read.Station);
        Assert.Equal(seg.Frames, read.Frames);
        Assert.Equal(seg.Bytes.Length, read.Bytes);
        Assert.Equal(start, read.StartUtc);
        Assert.Equal("hour-boundary", read.EndedBy);
        Assert.Empty(read.Gaps);
    }
}
