extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class SegmentCatalogTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-cat-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string DayDir => Path.Combine(_root, "burst", "master", "2026", "10-15");

    [Fact]
    public void Lists_closed_segments_from_sidecars_and_the_open_part_in_start_order()
    {
        var open = SegmentFiles.Open(_root, "burst", T0.AddHours(1).AddSeconds(5), T0.AddHours(1).AddMinutes(1), firstSeq: 9000);
        SegmentFiles.Closed(_root, "burst", T0.AddMinutes(30), 1800, firstSeq: 0, burstSeconds: 5, endedBy: "hour-boundary");

        var list = SegmentCatalog.Scan(_root, "burst", "a", NullLogger.Instance);

        Assert.Equal(2, list.Count);
        Assert.False(list[0].IsOpen);
        Assert.Equal(T0.AddMinutes(30), list[0].StartUtc);
        Assert.Equal("hour-boundary", list[0].EndedBy);
        Assert.InRange(list[0].BurstSeconds, 4.97, 5.03); // whole frames: within one frame of 5 s

        var part = list[1];
        Assert.True(part.IsOpen);
        Assert.Null(part.EndedBy);
        Assert.Equal(open.AudioPath, part.AudioPath);
        Assert.Equal(T0.AddHours(1).AddSeconds(5), part.StartUtc);
        Assert.Equal(T0.AddHours(1).AddMinutes(1), part.EndUtc);
        Assert.Equal(open.Bytes.Length, part.Bytes);
        Assert.Equal(128, part.BitrateKbps);
        Assert.InRange(part.BurstSeconds, 4.97, 5.03);
        Assert.Equal("a", part.Recorder);
    }

    [Fact]
    public void Malformed_half_written_and_dangling_sidecars_are_skipped()
    {
        SegmentFiles.Closed(_root, "burst", T0, 60, firstSeq: 0);
        File.WriteAllText(Path.Combine(DayDir, "140200.json"), "{ \"schema\": 1, \"startUtc\": \"2026-10-15T14:02");
        File.WriteAllText(Path.Combine(DayDir, "140300.json"), "not json at all");
        File.WriteAllText(Path.Combine(DayDir, "140400.json"), "{}");
        File.WriteAllText(Path.Combine(DayDir, "140500.json.tmp"), "{");
        // A sidecar whose audio file does not exist.
        var orphan = SegmentFiles.Closed(_root, "burst", T0.AddMinutes(6), 60, firstSeq: 0);
        File.Delete(orphan.AudioPath);
        // A sidecar that names a file outside its folder is not followed.
        File.WriteAllText(Path.Combine(DayDir, "140700.json"),
            File.ReadAllText(Path.Combine(DayDir, "140000.json")).Replace("\"140000.mp3\"", "\"../../../../../secret.mp3\""));

        var list = SegmentCatalog.Scan(_root, "burst", "a", NullLogger.Instance);

        var only = Assert.Single(list);
        Assert.Equal(T0, only.StartUtc);
    }

    [Fact]
    public void A_missing_station_folder_is_an_empty_catalog()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(SegmentCatalog.Scan(_root, "burst", "a", NullLogger.Instance));
    }

    [Fact]
    public void A_growing_part_is_read_while_the_writer_holds_it_open()
    {
        var start = T0.AddSeconds(3);
        var dir = DayDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "140003.mp3.part");
        // Same sharing as MasterRecorder: the writer allows readers only.
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 0);
        writer.Write(Mp3TestData.Frames(100));

        var first = Assert.Single(SegmentCatalog.Scan(_root, "burst", "a", NullLogger.Instance));
        Assert.Equal(start, first.StartUtc);
        Assert.Equal(writer.Length, first.Bytes);

        writer.Write(Mp3TestData.Frames(50, 100));
        var second = Assert.Single(SegmentCatalog.Scan(_root, "burst", "a", NullLogger.Instance));
        Assert.Equal(writer.Length, second.Bytes);
        Assert.True(second.Bytes > first.Bytes);
    }

    [Fact]
    public void Results_are_cached_for_a_few_seconds()
    {
        SegmentFiles.Closed(_root, "burst", T0, 60, firstSeq: 0);
        var time = new ManualTimeProvider(T0.AddHours(2));
        var settings = TestSettings.Create(("a", _root));
        var catalog = new SegmentCatalog(settings, time, NullLogger<SegmentCatalog>.Instance);

        Assert.Single(catalog.Segments("a"));
        SegmentFiles.Closed(_root, "burst", T0.AddMinutes(5), 60, firstSeq: 0);
        Assert.Single(catalog.Segments("a"));

        time.Advance(TimeSpan.FromSeconds(SegmentCatalog.CacheSeconds + 1));
        Assert.Equal(2, catalog.Segments("a").Count);
    }

    [Fact]
    public void A_closed_segment_is_read_once_and_then_listed_from_memory()
    {
        // A sidecar is written once, when its segment closes; on a slow share (Docker Desktop on Windows) reading every
        // sidecar again each time took seconds, so only new files are read.
        var first = SegmentFiles.Closed(_root, "burst", T0, 60, firstSeq: 0);
        SegmentFiles.Closed(_root, "burst", T0.AddMinutes(5), 60, firstSeq: 0);
        var time = new ManualTimeProvider(T0.AddHours(2));
        var catalog = new SegmentCatalog(TestSettings.Create(("a", _root)), time, NullLogger<SegmentCatalog>.Instance);

        Assert.Equal(2, catalog.Segments("a").Count);
        Assert.Equal(2, catalog.SidecarReads);

        time.Advance(TimeSpan.FromSeconds(SegmentCatalog.CacheSeconds + 1));
        Assert.Equal(2, catalog.Segments("a").Count);
        Assert.Equal(2, catalog.SidecarReads);

        // A new segment is read; one whose audio has gone is dropped without reading anything.
        SegmentFiles.Closed(_root, "burst", T0.AddMinutes(10), 60, firstSeq: 0);
        File.Delete(first.AudioPath);
        time.Advance(TimeSpan.FromSeconds(SegmentCatalog.CacheSeconds + 1));
        var list = catalog.Segments("a");
        Assert.Equal([T0.AddMinutes(5), T0.AddMinutes(10)], list.Select(s => s.StartUtc));
        Assert.Equal(3, catalog.SidecarReads);

        // A half-written sidecar is not remembered: it is read again until it parses.
        var broken = Path.Combine(DayDir, "141500.json");
        File.WriteAllText(broken, "{ \"schema\": 1");
        time.Advance(TimeSpan.FromSeconds(SegmentCatalog.CacheSeconds + 1));
        Assert.Equal(2, catalog.Segments("a").Count);
        time.Advance(TimeSpan.FromSeconds(SegmentCatalog.CacheSeconds + 1));
        Assert.Equal(2, catalog.Segments("a").Count);
        Assert.Equal(5, catalog.SidecarReads);
    }
}
