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

    /// <summary>A recorder sidecar exactly as plan 1 writes it: no merge key at all.</summary>
    const string Plan1Sidecar = """
        {
          "schema": 1,
          "instance": "recorder-a",
          "station": "burst",
          "file": "140000.mp3",
          "startUtc": "2026-10-16T14:00:00+00:00",
          "endUtc": "2026-10-16T15:00:00+00:00",
          "bytes": 57600000,
          "frames": 137812,
          "audioSeconds": 3600.026,
          "format": {
            "codec": "MPEG-1 Layer III",
            "bitrateKbps": 128,
            "sampleRateHz": 44100,
            "channels": 2
          },
          "stream": {
            "url": "https://stream.example.org/live",
            "howFound": "listen page",
            "icyName": "Example",
            "icyGenre": null,
            "icyMetaInt": 16000
          },
          "titles": [
            {
              "title": "Artist - Song",
              "atFileByte": 1000,
              "utc": "2026-10-16T14:00:01+00:00"
            }
          ],
          "gaps": [],
          "endedBy": "hour-boundary"
        }
        """;

    [Fact]
    public void A_plan_1_sidecar_with_no_merge_key_reads_and_is_written_back_byte_identical()
    {
        var path = Path.Combine(_root, "140000.json");
        File.WriteAllText(path, Plan1Sidecar);

        var read = SegmentMetaStore.Read(path)!;
        Assert.Null(read.Merge);
        Assert.Equal("recorder-a", read.Instance);
        Assert.Equal(137812, read.Frames);
        Assert.Equal("Artist - Song", Assert.Single(read.Titles).Title);

        var copy = Path.Combine(_root, "copy.json");
        SegmentMetaStore.Write(copy, read);
        var written = File.ReadAllText(copy);

        Assert.DoesNotContain("merge", written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Plan1Sidecar.ReplaceLineEndings(), written.ReplaceLineEndings());
    }
}
