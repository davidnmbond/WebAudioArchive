using System.Security.Cryptography;
using System.Text.Json;
using WebAudioArchive.Core.Consolidation;
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

    /// <summary>A and B record 09:00 to 12:00; the three hours are merged. Returns the merged files and A's 10:00 segment.</summary>
    (IReadOnlyList<string> Merged, SourceSegment A10) MergeThreeHours()
    {
        foreach (var id in new[] { "a", "b" })
            for (var h = -1; h < 2; h++)
                SegmentFiles.Live(Root(id), "burst", Epoch, T0.AddHours(h), T0.AddHours(h + 1));

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
        Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, " ", Now.AddDays(8), Log));
        Assert.True(File.Exists(heldMp3));

        // Ready, but the held file changed since the recheck: refused.
        var original = File.ReadAllBytes(heldMp3);
        File.AppendAllBytes(heldMp3, [1]);
        var changed = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now.AddDays(8), Log));
        Assert.Contains("checksum", changed.Message);
        Assert.True(File.Exists(heldMp3));
        Assert.False(File.Exists(Log));
        File.WriteAllBytes(heldMp3, original);

        var when = Now.AddDays(8);
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

        var ex = Assert.Throws<HoldingException>(() => Holding.Delete(heldJson, "manager", Now.AddDays(8), Log));

        Assert.Contains("100000.mp3", ex.Message);
        Assert.True(File.Exists(Path.Combine(HeldDay, "100000.mp3")));
        Assert.False(File.Exists(Log));
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
