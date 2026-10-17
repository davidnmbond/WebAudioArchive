using System.Security.Cryptography;
using System.Text.Json;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #104: an original moves to held/ only once every frame is in the merged copy, is rechecked after 7 days, and is
/// deleted only on a manager's confirmation, with the deletion logged.
/// </summary>
public sealed class HoldingTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-holding-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    string Merged => Path.Combine(_root, "merged", "burst", "master");

    string Log => Path.Combine(_root, "merged", "burst", "deletions.jsonl");

    string HeldDay => Path.Combine(Root("a"), "burst", "held", "2026", "10-15");

    string MasterDay => Path.Combine(Root("a"), "burst", "master", "2026", "10-15");

    IReadOnlyList<SourceSegment> Scan(string id) => SourceSegment.Scan(Root(id), "burst", id);

    static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(1);

    static int SeqAt(double secondAfterT0) => SegmentFiles.FramesFor((T0.AddSeconds(secondAfterT0) - Epoch).TotalSeconds);

    /// <summary>
    /// A and B each record a minute at 09:59, 10:00 and 11:00 (short, so the tests run quickly); the three hours 09:00,
    /// 10:00 and 11:00 are merged. Returns the merged files and A's 10:00 segment. <paramref name="aReplaySeconds"/> makes
    /// A's 10:00 segment begin with a reconnect replay of audio A already holds at the end of 09:59.
    /// </summary>
    (IReadOnlyList<string> Merged, SourceSegment A10) MergeThreeHours((int From, int To)? silence = null, double aReplaySeconds = 0)
    {
        foreach (var id in new[] { "a", "b" })
            foreach (var start in new[] { T0 - SegmentLength, T0, T0.AddHours(1) })
                SegmentFiles.Live(
                    Root(id), "burst", Epoch, start, start + SegmentLength, silence: silence,
                    replaySeconds: id == "a" && start == T0 ? aReplaySeconds : 0);

        var files = new List<string>();
        IReadOnlyList<ulong> tail = [];
        for (var h = -1; h < 2; h++)
        {
            var hour = T0.AddHours(h);
            var plan = HourMerger.Plan(hour, Scan("a"), Scan("b"), tail)!;
            MergedHourWriter.Write(plan, Merged, "burst");
            var mp3 = Path.Combine(Merged, "2026", "10-15", hour.ToString("HHmmss") + ".mp3");
            files.Add(mp3);
            tail = MergedHourWriter.Tail(mp3);
        }

        return (files, Scan("a").Single(s => s.Meta.StartUtc == T0));
    }

    [Fact]
    public void Held_dir_is_under_the_recorder_root_by_utc_day()
    {
        Assert.Equal(
            Path.Combine("data", "a", "burst", "held", "2026", "10-15"),
            Holding.HeldDir(Path.Combine("data", "a"), "burst", new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(
            Path.Combine("data", "a", "burst", "held", "2026", "10-15"),
            Holding.HeldDir(Path.Combine("data", "a"), "burst", new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void A_segment_whose_frames_are_all_merged_moves_to_held_with_a_record()
    {
        var (merged, a10) = MergeThreeHours();
        var sha = Sha(a10.AudioPath);
        var bytes = new FileInfo(a10.AudioPath).Length;

        Assert.True(Holding.AllFramesMerged(a10, merged, out var why), why);
        var record = Holding.Hold(a10, merged, Now);

        Assert.False(File.Exists(Path.Combine(MasterDay, "100000.mp3")));
        Assert.False(File.Exists(Path.Combine(MasterDay, "100000.json")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.json")));
        Assert.Equal(sha, Sha(Path.Combine(HeldDay, "100000.mp3")));
        Assert.Equal("a", record.Recorder);
        Assert.Equal("burst", record.Station);
        Assert.Equal("2026/10-15/100000.mp3", record.File);
        Assert.Equal(sha, record.Sha256);
        Assert.Equal(bytes, record.Bytes);
        Assert.Equal(Now, record.HeldUtc);
        Assert.Equal(merged, record.MergedFiles);
        Assert.Equal("held", record.Status);
        Assert.Null(record.CheckedUtc);
        Assert.Null(record.Reason);
        var stored = Holding.ReadRecord(Path.Combine(HeldDay, "100000.held.json"))!;
        Assert.Equal(record.Sha256, stored.Sha256);
        Assert.Equal(record.MergedFiles, stored.MergedFiles);
        Assert.Equal("held", stored.Status);
        Assert.DoesNotContain(Scan("a"), s => s.Meta.StartUtc == T0);
    }

    [Fact]
    public void A_segment_one_frame_short_does_not_move()
    {
        var (merged, a10) = MergeThreeHours();
        // One frame the merged copy does not have, added to A's segment and its sidecar.
        File.AppendAllBytes(a10.AudioPath, Mp3TestData.CbrFrame(77_777_777));
        var json = Path.ChangeExtension(a10.AudioPath, ".json");
        var meta = SegmentMetaStore.Read(json)!;
        SegmentMetaStore.Write(json, meta with { Bytes = new FileInfo(a10.AudioPath).Length, Frames = meta.Frames + 1 });
        var grown = Scan("a").Single(s => s.Meta.StartUtc == T0);

        Assert.False(Holding.AllFramesMerged(grown, merged, out var reason));
        Assert.Contains("1 ", reason);
        var ex = Assert.Throws<HoldingException>(() => Holding.Hold(grown, merged, Now));

        Assert.Contains(reason, ex.Message);
        Assert.True(File.Exists(Path.Combine(MasterDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(MasterDay, "100000.json")));
        Assert.False(Directory.Exists(Path.Combine(Root("a"), "burst", "held")));
    }

    [Fact]
    public void A_segment_is_not_held_when_a_merged_file_is_missing_or_does_not_match_its_sidecar()
    {
        var (merged, a10) = MergeThreeHours();

        Assert.False(Holding.AllFramesMerged(a10, [], out var none));
        Assert.Contains("no merged", none);
        File.AppendAllBytes(merged[2], [0, 0, 0, 0]);
        Assert.False(Holding.AllFramesMerged(a10, merged, out var changed));
        Assert.Contains("110000.mp3", changed);
        File.Delete(merged[2]);
        Assert.False(Holding.AllFramesMerged(a10, merged, out var missing));
        Assert.Contains("110000.mp3", missing);
        Assert.Throws<HoldingException>(() => Holding.Hold(a10, merged, Now));
        Assert.True(File.Exists(a10.AudioPath));
    }

    [Fact]
    public void After_seven_days_a_held_segment_is_ready_only_if_it_and_its_merged_copy_are_unchanged()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        var heldJson = Path.Combine(HeldDay, "100000.held.json");

        var early = Holding.Recheck(heldJson, Now + TimeSpan.FromDays(6) + TimeSpan.FromHours(23));
        Assert.Equal("held", early.Status);
        Assert.Null(early.CheckedUtc);
        Assert.Equal("held", Holding.ReadRecord(heldJson)!.Status);

        var due = Now + Holding.HoldPeriod;
        var ready = Holding.Recheck(heldJson, due);
        Assert.Equal("ready", ready.Status);
        Assert.Equal(due, ready.CheckedUtc);
        Assert.Null(ready.Reason);
        Assert.Equal("ready", Holding.ReadRecord(heldJson)!.Status);

        var bytes = File.ReadAllBytes(merged[1]);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(merged[1], bytes);
        var failed = Holding.Recheck(heldJson, due.AddHours(1));
        Assert.Equal("failed", failed.Status);
        Assert.Equal(due.AddHours(1), failed.CheckedUtc);
        Assert.Contains("100000.mp3", failed.Reason);
        Assert.Equal("failed", Holding.ReadRecord(heldJson)!.Status);
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
    }

    [Fact]
    public void A_held_segment_that_changed_is_not_ready()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        File.AppendAllBytes(Path.Combine(HeldDay, "100000.mp3"), [1]);

        var record = Holding.Recheck(Path.Combine(HeldDay, "100000.held.json"), Now + Holding.HoldPeriod);

        Assert.Equal("failed", record.Status);
        Assert.Contains("checksum", record.Reason);
    }

    [Fact]
    public void Only_a_ready_segment_is_deleted_and_the_deletion_is_logged_with_who_and_the_checksum()
    {
        var (merged, a10) = MergeThreeHours();
        var held = Holding.Hold(a10, merged, Now);
        var heldJson = Path.Combine(HeldDay, "100000.held.json");
        var heldMp3 = Path.Combine(HeldDay, "100000.mp3");

        // Still held: refused, nothing deleted, nothing logged.
        var notReady = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now.AddDays(1), Log));
        Assert.Contains("held", notReady.Message);
        Assert.True(File.Exists(heldMp3));
        Assert.False(File.Exists(Log));

        Assert.Equal("ready", Holding.Recheck(heldJson, Now + Holding.HoldPeriod).Status);

        // No one named: refused.
        Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, " ", Now + Holding.HoldPeriod + TimeSpan.FromHours(2), Log));
        Assert.True(File.Exists(heldMp3));

        // Ready, but the held file changed since the recheck: refused.
        var original = File.ReadAllBytes(heldMp3);
        File.AppendAllBytes(heldMp3, [1]);
        var changed = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now + Holding.HoldPeriod + TimeSpan.FromHours(2), Log));
        Assert.Contains("checksum", changed.Message);
        Assert.True(File.Exists(heldMp3));
        Assert.False(File.Exists(Log));
        File.WriteAllBytes(heldMp3, original);

        var when = Now + Holding.HoldPeriod + TimeSpan.FromHours(2);
        Holding.Delete(heldJson, "station.manager@example.org", when, Log);

        Assert.False(File.Exists(heldMp3));
        Assert.False(File.Exists(Path.Combine(HeldDay, "100000.json")));
        Assert.False(File.Exists(heldJson));
        Assert.All(merged, m => Assert.True(File.Exists(m)));
        var lines = File.ReadAllLines(Log);
        var entry = Assert.Single(lines);
        using var doc = JsonDocument.Parse(entry);
        var e = doc.RootElement;
        Assert.Equal(when, e.GetProperty("utc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, e.GetProperty("utc").GetDateTimeOffset().Offset);
        Assert.Equal("station.manager@example.org", e.GetProperty("confirmedBy").GetString());
        Assert.Equal("a", e.GetProperty("recorder").GetString());
        Assert.Equal("2026/10-15/100000.mp3", e.GetProperty("file").GetString());
        Assert.Equal(held.Sha256, e.GetProperty("sha256").GetString());
        Assert.Equal(held.Bytes, e.GetProperty("bytes").GetInt64());

        // A second deletion appends a line; a torn last line is not joined to it.
        var b10 = Scan("b").Single(s => s.Meta.StartUtc == T0);
        Holding.Hold(b10, merged, Now);
        var bJson = Path.Combine(Root("b"), "burst", "held", "2026", "10-15", "100000.held.json");
        Holding.Recheck(bJson, Now + Holding.HoldPeriod);
        File.AppendAllText(Log, "{\"utc\":\"torn");
        Holding.Delete(bJson, "manager", when, Log);
        var after = File.ReadAllLines(Log);
        Assert.Equal(3, after.Length);
        Assert.Equal(entry, after[0]);
        using var second = JsonDocument.Parse(after[2]);
        Assert.Equal("b", second.RootElement.GetProperty("recorder").GetString());
    }

    [Fact]
    public void A_ready_segment_whose_merged_copy_is_gone_is_not_deleted()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        var heldJson = Path.Combine(HeldDay, "100000.held.json");
        Holding.Recheck(heldJson, Now + Holding.HoldPeriod);
        File.Delete(merged[1]);

        var ex = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now + Holding.HoldPeriod + TimeSpan.FromHours(2), Log));

        Assert.Contains("100000.mp3", ex.Message);
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.False(File.Exists(Log));
    }

    [Fact]
    public void A_merged_copy_missing_part_of_a_silence_run_does_not_account_for_the_segment()
    {
        var (merged, a10) = MergeThreeHours(silence: (SeqAt(10), SeqAt(40)));
        Assert.True(Holding.AllFramesMerged(a10, merged, out var before), before);

        // Drop 20 frames from the middle of the silent run in the merged 10:00 hour, and reseal its sidecar so only the
        // frame count tells. Every distinct frame of the segment is still in the merged copy.
        var mp3 = merged[1];
        var data = File.ReadAllBytes(mp3);
        List<FrameRef> frames;
        using (var fs = new MemoryStream(data)) frames = FrameIndex.Read(fs, 0, data.Length);
        var silent = frames.GroupBy(f => f.Hash).OrderByDescending(g => g.Count()).First().ToList();
        Assert.True(silent.Count > 100);
        var dropped = silent.Skip(silent.Count / 2).Take(20).Select(f => f.Offset).ToHashSet();
        var kept = frames.Where(f => !dropped.Contains(f.Offset)).SelectMany(f => data.AsSpan((int)f.Offset, f.Length).ToArray()).ToArray();
        File.WriteAllBytes(mp3, kept);
        var json = Path.ChangeExtension(mp3, ".json");
        var meta = SegmentMetaStore.Read(json)!;
        SegmentMetaStore.Write(json, meta with { Bytes = kept.Length, Frames = meta.Frames - 20, Merge = meta.Merge! with { Sha256 = Sha(mp3) } });
        var mergedHashes = merged.SelectMany(m =>
        {
            using var fs = File.OpenRead(m);
            return FrameIndex.Read(fs, 0, fs.Length).Select(f => f.Hash).ToList();
        }).ToHashSet();
        using (var fs = File.OpenRead(a10.AudioPath)) Assert.All(FrameIndex.Read(fs, 0, fs.Length), f => Assert.Contains(f.Hash, mergedHashes));

        Assert.False(Holding.AllFramesMerged(a10, merged, out var reason));
        Assert.Contains("20 frame(s)", reason);
        Assert.Throws<HoldingException>(() => Holding.Hold(a10, merged, Now));
        Assert.True(File.Exists(a10.AudioPath));
    }

    [Fact]
    public void A_segment_that_begins_with_a_reconnect_replay_is_accounted_for()
    {
        var (merged, a10) = MergeThreeHours(aReplaySeconds: 5);
        var a0959 = Scan("a").Single(s => s.Meta.StartUtc == T0 - SegmentLength);
        using (var fs = File.OpenRead(a10.AudioPath))
        using (var prev = File.OpenRead(a0959.AudioPath))
        {
            var head = FrameIndex.Read(fs, 0, fs.Length).Take(SegmentFiles.FramesFor(5)).Select(f => f.Hash);
            var tail = FrameIndex.Read(prev, 0, prev.Length).Select(f => f.Hash).ToHashSet();
            Assert.All(head, h => Assert.Contains(h, tail)); // the replay repeats audio A already holds
        }

        Assert.True(Holding.AllFramesMerged(a10, merged, out var why), why);
        Assert.Equal("held", Holding.Hold(a10, merged, Now).Status);
    }

    [Fact]
    public void A_ready_record_not_rechecked_within_a_day_is_not_deleted()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        var heldJson = Path.Combine(HeldDay, "100000.held.json");
        var checkedAt = Now + Holding.HoldPeriod;
        Holding.Recheck(heldJson, checkedAt);

        var ex = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", checkedAt + Holding.ReadyValidFor + TimeSpan.FromSeconds(1), Log));

        Assert.Equal(TimeSpan.FromHours(24), Holding.ReadyValidFor);
        Assert.Contains("recheck due", ex.Message);
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.True(File.Exists(heldJson));
        Assert.False(File.Exists(Log));

        // Rechecked again, it may be deleted.
        var again = checkedAt.AddDays(2);
        Assert.Equal("ready", Holding.Recheck(heldJson, again).Status);
        Holding.Delete(heldJson, "manager", again + Holding.ReadyValidFor, Log);
        Assert.False(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
    }

    [Fact]
    public void A_deletion_whose_log_is_held_open_by_another_writer_deletes_nothing()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        var heldJson = Path.Combine(HeldDay, "100000.held.json");
        Holding.Recheck(heldJson, Now + Holding.HoldPeriod);
        Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
        File.WriteAllText(Log, "{\"earlier\":1}\n");

        using (new FileStream(Log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now + Holding.HoldPeriod + TimeSpan.FromHours(1), Log));
            Assert.Contains("deletions log", ex.Message);
        }

        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.json")));
        Assert.True(File.Exists(heldJson));
        Assert.Equal("{\"earlier\":1}\n", File.ReadAllText(Log));
    }

    [Fact]
    public void A_half_finished_move_is_completed_on_the_next_run()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);

        // Stopped after the audio moved but before its sidecar did.
        File.Move(Path.Combine(HeldDay, "100000.json"), Path.Combine(MasterDay, "100000.json"));
        Holding.Repair(Root("a"), "burst");
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.json")));
        Assert.False(File.Exists(Path.Combine(MasterDay, "100000.json")));

        // The reverse: the sidecar is held but the audio is still in master.
        File.Move(Path.Combine(HeldDay, "100000.mp3"), Path.Combine(MasterDay, "100000.mp3"));
        Holding.Repair(Root("a"), "burst");
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.False(File.Exists(Path.Combine(MasterDay, "100000.mp3")));

        // Stopped after the record was written but before anything moved.
        var a11 = Scan("a").Single(s => s.Meta.StartUtc == T0.AddHours(1));
        Holding.Hold(a11, merged, Now);
        File.Move(Path.Combine(HeldDay, "110000.mp3"), Path.Combine(MasterDay, "110000.mp3"));
        File.Move(Path.Combine(HeldDay, "110000.json"), Path.Combine(MasterDay, "110000.json"));
        Holding.Repair(Root("a"), "burst");
        Assert.True(File.Exists(Path.Combine(HeldDay, "110000.mp3")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "110000.json")));
        Assert.False(File.Exists(Path.Combine(MasterDay, "110000.mp3")));

        Assert.Equal("ready", Holding.Recheck(Path.Combine(HeldDay, "100000.held.json"), Now + Holding.HoldPeriod).Status);
        Assert.Equal("ready", Holding.Recheck(Path.Combine(HeldDay, "110000.held.json"), Now + Holding.HoldPeriod).Status);
    }

    [Fact]
    public void A_half_move_with_no_record_is_undone_back_to_master()
    {
        var (_, a10) = MergeThreeHours();
        Directory.CreateDirectory(HeldDay);
        File.Move(a10.AudioPath, Path.Combine(HeldDay, "100000.mp3"));

        Holding.Repair(Root("a"), "burst");

        Assert.True(File.Exists(Path.Combine(MasterDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(MasterDay, "100000.json")));
        Assert.False(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.Contains(Scan("a"), s => s.Meta.StartUtc == T0);
    }

    [Fact]
    public void Repair_never_overwrites_a_file_that_is_in_both_places()
    {
        var (merged, a10) = MergeThreeHours();
        Holding.Hold(a10, merged, Now);
        File.Copy(Path.Combine(HeldDay, "100000.json"), Path.Combine(MasterDay, "100000.json"));
        File.WriteAllBytes(Path.Combine(MasterDay, "100000.mp3"), [1, 2, 3]);
        var heldSha = Sha(Path.Combine(HeldDay, "100000.mp3"));

        Holding.Repair(Root("a"), "burst");

        Assert.Equal(heldSha, Sha(Path.Combine(HeldDay, "100000.mp3")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(MasterDay, "100000.mp3")));
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.json")));
    }
}
