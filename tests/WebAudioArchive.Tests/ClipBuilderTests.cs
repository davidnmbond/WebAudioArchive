extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class ClipBuilderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-build-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    string Data => Path.Combine(_root, "data");
    string Cache => Path.Combine(_root, "cache");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    ClipPlan PlanFor(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) => ClipPlanner.Plan(
        new ClipRequest(from, to, TimeSpan.Zero, TimeSpan.Zero, "auto"),
        [new RecorderSegments("a", SegmentCatalog.Scan(Data, "burst", "a", NullLogger.Instance))],
        now, ClipPlanner.DefaultMaxClip);

    ClipBuilder Builder() => new(Cache, NullLogger.Instance);

    [Fact]
    public void Builds_the_concatenated_pieces_into_the_cache_with_a_stable_name()
    {
        var s1 = SegmentFiles.Closed(Data, "burst", T0, 60, firstSeq: 0);
        var s2 = SegmentFiles.Closed(Data, "burst", T0.AddMinutes(1), 60, firstSeq: s1.Frames);
        var plan = PlanFor(T0.AddSeconds(15), T0.AddSeconds(75), T0.AddHours(1));

        var clip = Builder().Build("burst", plan);

        Assert.Matches("^[0-9a-f]{32}$", clip.Id);
        Assert.Equal(Path.Combine(Cache, clip.Id + ".mp3"), clip.FilePath);
        var bytes = File.ReadAllBytes(clip.FilePath);
        Assert.Equal(plan.Pieces.SelectMany(p => File.ReadAllBytes(p.Segment.AudioPath)[(int)p.StartByte..(int)p.EndByte]), bytes);
        Assert.Equal(bytes.Length, clip.Bytes);
        Assert.Equal(SegmentFiles.Seqs(bytes).Count * SegmentFiles.FrameSeconds, clip.Seconds, 6);
        Assert.Equal("burst_20261015T150015Z_20261015T150115Z_A.mp3", clip.DownloadName);
        Assert.False(clip.Reused);
        Assert.Empty(Directory.EnumerateFiles(Cache, "*.tmp"));
        Assert.Equal(clip.DownloadName, ClipBuilder.ReadInfo(Cache, clip.Id)!.DownloadName);
    }

    [Fact]
    public void The_same_plan_reuses_the_cached_clip_and_a_grown_part_makes_a_new_one()
    {
        var open = SegmentFiles.Open(Data, "burst", T0, T0.AddSeconds(60), firstSeq: 0, burstSeconds: 0);
        var first = Builder().Build("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(61)));
        var again = Builder().Build("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(61)));

        Assert.Equal(first.Id, again.Id);
        Assert.True(again.Reused);
        Assert.Equal(first.Seconds, again.Seconds, 6);

        // The recorder appends more frames: same window, but the .part's size and time changed.
        File.WriteAllBytes(open.AudioPath, open.Bytes.Concat(Mp3TestData.Frames(40, open.Frames)).ToArray());
        File.SetLastWriteTimeUtc(open.AudioPath, T0.AddSeconds(61).UtcDateTime);
        var grown = Builder().Build("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(62)));

        Assert.NotEqual(first.Id, grown.Id);
        Assert.False(grown.Reused);
    }

    [Fact]
    public void A_clip_that_is_not_whole_frames_fails_loudly_and_leaves_nothing_in_the_cache()
    {
        SegmentFiles.Closed(Data, "burst", T0, 60, firstSeq: 0);
        var plan = PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddHours(1));
        var torn = plan with { Pieces = [plan.Pieces[0] with { EndByte = plan.Pieces[0].EndByte - 5 }] };

        Assert.Throws<ClipIntegrityException>(() => Builder().Build("burst", torn));
        Assert.Empty(Directory.EnumerateFiles(Cache));
    }

    [Fact]
    public void Housekeeping_removes_old_clips_and_caps_the_total_size_oldest_first()
    {
        Directory.CreateDirectory(Cache);
        var now = T0.AddDays(2);
        void Clip(string id, int bytes, TimeSpan age)
        {
            var mp3 = Path.Combine(Cache, id + ".mp3");
            File.WriteAllBytes(mp3, new byte[bytes]);
            File.WriteAllText(Path.Combine(Cache, id + ".json"), "{}");
            File.SetLastWriteTimeUtc(mp3, (now - age).UtcDateTime);
        }

        Clip(new string('1', 32), 100, TimeSpan.FromHours(25)); // too old
        Clip(new string('2', 32), 400, TimeSpan.FromHours(5));  // oldest of the rest, dropped by the cap
        Clip(new string('3', 32), 400, TimeSpan.FromHours(3));
        Clip(new string('4', 32), 400, TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(Cache, new string('5', 32) + ".json"), "{}"); // orphan info file
        var staleTmp = Path.Combine(Cache, "x.tmp");
        File.WriteAllBytes(staleTmp, new byte[10]);
        File.SetLastWriteTimeUtc(staleTmp, (now - TimeSpan.FromHours(2)).UtcDateTime);

        var removed = ClipCache.Housekeep(Cache, now, TimeSpan.FromHours(24), maxBytes: 1000, NullLogger.Instance);

        Assert.Equal(
            [new string('3', 32) + ".json", new string('3', 32) + ".mp3", new string('4', 32) + ".json", new string('4', 32) + ".mp3"],
            Directory.EnumerateFiles(Cache).Select(f => Path.GetFileName(f)).Order().ToArray());
        Assert.Equal(2, removed);
    }
}
