extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class ClipStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-store-" + Guid.NewGuid().ToString("N"));
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

    ClipStore Store() => new(Cache, [Path.Combine(Data, "burst", "master")], NullLogger.Instance);

    static byte[] ReadAll(Stream s)
    {
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void A_clip_is_a_small_manifest_and_streams_the_pieces_straight_from_the_segments()
    {
        var s1 = SegmentFiles.Closed(Data, "burst", T0, 60, firstSeq: 0);
        SegmentFiles.Closed(Data, "burst", T0.AddMinutes(1), 60, firstSeq: s1.Frames);
        var plan = PlanFor(T0.AddSeconds(15), T0.AddSeconds(75), T0.AddHours(1));

        var clip = Store().Register("burst", plan);

        Assert.Matches("^[0-9a-f]{32}$", clip.Id);
        var expected = plan.Pieces.SelectMany(p => File.ReadAllBytes(p.Segment.AudioPath)[(int)p.StartByte..(int)p.EndByte]).ToArray();
        Assert.Equal(expected.Length, clip.Bytes);
        // Constant bitrate: the length in seconds is the bytes over the byte rate, within a frame of counting frames.
        Assert.Equal(SegmentFiles.Seqs(expected).Count * SegmentFiles.FrameSeconds, clip.Seconds, 1);
        Assert.Equal(expected.Length / 16000.0, clip.Seconds, 6);
        Assert.Equal("burst_20261015T150015Z_20261015T150115Z_A.mp3", clip.DownloadName);

        // Nothing is copied: the cache holds only the manifest, and the stream reads the segments.
        Assert.Equal([clip.Id + ".json"], Directory.EnumerateFiles(Cache).Select(f => Path.GetFileName(f)!));
        var opened = Store().Open(clip.Id);
        Assert.NotNull(opened);
        Assert.Equal(clip.DownloadName, opened.Value.Manifest.DownloadName);
        using var stream = opened.Value.Stream;
        Assert.True(stream.CanSeek);
        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(expected, ReadAll(stream));

        // Any range, including one across the join between the two segments.
        var join = plan.Pieces[0].EndByte - plan.Pieces[0].StartByte;
        stream.Position = join - 100;
        var across = new byte[300];
        stream.ReadExactly(across);
        Assert.Equal(expected[(int)(join - 100)..(int)(join + 200)], across);
    }

    [Fact]
    public void The_same_plan_gives_the_same_id_and_a_grown_part_a_new_one()
    {
        var open = SegmentFiles.Open(Data, "burst", T0, T0.AddSeconds(60), firstSeq: 0, burstSeconds: 0);
        var first = Store().Register("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(61)));
        var again = Store().Register("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(61)));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Seconds, again.Seconds, 6);

        File.WriteAllBytes(open.AudioPath, open.Bytes.Concat(Mp3TestData.Frames(40, open.Frames)).ToArray());
        File.SetLastWriteTimeUtc(open.AudioPath, T0.AddSeconds(61).UtcDateTime);
        var grown = Store().Register("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(62)));

        Assert.NotEqual(first.Id, grown.Id);
    }

    [Fact]
    public void A_clip_of_the_open_segment_still_plays_after_the_recorder_closes_it()
    {
        var open = SegmentFiles.Open(Data, "burst", T0, T0.AddSeconds(60), firstSeq: 0, burstSeconds: 0);
        var plan = PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(61));
        var clip = Store().Register("burst", plan);
        var expected = open.Bytes[(int)plan.Pieces[0].StartByte..(int)plan.Pieces[0].EndByte];

        // At the hour the recorder renames 150000.mp3.part to 150000.mp3; the bytes are the same.
        File.Move(open.AudioPath, open.AudioPath[..^".part".Length]);

        using var stream = Store().Open(clip.Id)!.Value.Stream;
        Assert.Equal(expected, ReadAll(stream));
    }

    [Fact]
    public void A_join_that_is_not_on_a_frame_boundary_fails_loudly_and_leaves_nothing_in_the_cache()
    {
        SegmentFiles.Closed(Data, "burst", T0, 60, firstSeq: 0);
        var plan = PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddHours(1));
        var torn = plan with { Pieces = [plan.Pieces[0] with { EndByte = plan.Pieces[0].EndByte - 5 }] };
        var offset = plan with { Pieces = [plan.Pieces[0] with { StartByte = plan.Pieces[0].StartByte + 1 }] };

        Assert.Throws<ClipIntegrityException>(() => Store().Register("burst", torn));
        Assert.Throws<ClipIntegrityException>(() => Store().Register("burst", offset));
        Assert.False(Directory.Exists(Cache) && Directory.EnumerateFiles(Cache).Any());
    }

    [Fact]
    public void Only_ids_and_manifests_naming_files_in_the_recorders_folders_are_opened()
    {
        SegmentFiles.Closed(Data, "burst", T0, 60, firstSeq: 0);
        var clip = Store().Register("burst", PlanFor(T0.AddSeconds(10), T0.AddSeconds(20), T0.AddHours(1)));
        var manifest = Path.Combine(Cache, clip.Id + ".json");

        Assert.Null(Store().Open("../../etc/passwd"));
        Assert.Null(Store().Open(new string('0', 32)));

        // A manifest edited to point elsewhere is refused rather than followed.
        var secret = Path.Combine(_root, "secret.mp3");
        File.WriteAllBytes(secret, Mp3TestData.Frames(10));
        var json = File.ReadAllText(manifest);
        var audio = System.Text.Json.JsonSerializer.Serialize(Directory.EnumerateFiles(Data, "*.mp3", SearchOption.AllDirectories).Single()).Trim('"');
        File.WriteAllText(manifest, json.Replace(audio, System.Text.Json.JsonSerializer.Serialize(secret).Trim('"'), StringComparison.Ordinal));
        Assert.Null(Store().Open(clip.Id));
    }

    [Fact]
    public void Housekeeping_removes_old_manifests_built_clips_from_before_streaming_and_stale_temp_files()
    {
        Directory.CreateDirectory(Cache);
        var now = T0.AddDays(2);
        void Write(string name, TimeSpan age)
        {
            var path = Path.Combine(Cache, name);
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, (now - age).UtcDateTime);
        }

        Write(new string('1', 32) + ".json", TimeSpan.FromHours(25)); // too old
        Write(new string('2', 32) + ".json", TimeSpan.FromHours(1));
        Write(new string('3', 32) + ".mp3", TimeSpan.FromHours(1));   // a built clip from before streaming
        Write("x.tmp", TimeSpan.FromHours(2));
        Write("y.tmp", TimeSpan.FromMinutes(1));                       // may still be being written
        Directory.CreateDirectory(Path.Combine(Cache, "keys"));

        var removed = ClipCache.Housekeep(Cache, now, TimeSpan.FromHours(24), NullLogger.Instance);

        Assert.Equal([new string('2', 32) + ".json", "y.tmp"], Directory.EnumerateFiles(Cache).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.True(Directory.Exists(Path.Combine(Cache, "keys")));
        Assert.Equal(2, removed);
    }
}
