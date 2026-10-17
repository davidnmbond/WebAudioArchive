using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WorkPlanningTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-work-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string A => Path.Combine(_root, "a");
    string B => Path.Combine(_root, "b");

    SegmentDiscovery Discovery() => new([new RecorderRoot("a", A), new RecorderRoot("b", B)], "burst", NullLogger.Instance);

    [Fact]
    public void Discovery_reads_closed_segments_and_builds_job_ids_newest_first()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 2, 0, endedBy: "shutdown");

        var found = Discovery().Scan();

        Assert.Equal(new[] { "a/2026/10-16/090000", "a/2026/10-16/080000" }, found.Select(s => s.JobId));
        Assert.Equal(44100, found[1].SampleRate);
        Assert.Equal(2, found[1].Channels);
        Assert.EndsWith("080000.mp3", found[1].AudioPath);
    }

    [Fact]
    public void Discovery_skips_parts_tmp_and_mismatched_sizes()
    {
        var good = SegmentFiles.Closed(A, "burst", T, 600, 0);
        SegmentFiles.Open(A, "burst", T.AddHours(1), T.AddHours(1).AddMinutes(5), 0);
        File.WriteAllText(Path.ChangeExtension(good.AudioPath, ".json") + ".tmp", "{ half");
        var grown = SegmentFiles.Closed(A, "burst", T.AddHours(2), 600, 0);
        File.AppendAllText(grown.AudioPath, "extra");
        var missing = SegmentFiles.Closed(A, "burst", T.AddHours(3), 600, 0);
        File.Delete(missing.AudioPath);
        var problems = new List<string>();

        var found = Discovery().Scan(problems);

        Assert.Equal("a/2026/10-16/080000", Assert.Single(found).JobId);
        Assert.Contains(problems, p => p.Contains("sidecar says"));
        Assert.Contains(problems, p => p.Contains("is missing"));
    }

    [Fact]
    public void Changed_sidecar_changes_the_fingerprint_and_reprocesses()
    {
        var seg = SegmentFiles.Closed(A, "burst", T, 600, 0);
        var discovery = Discovery();
        var first = Assert.Single(discovery.Scan());
        var done = new JobState(1, first.JobId, first.Fingerprint, "tv", "dv", JobStatus.Done, 0, null, null, [0], 1, T, 600, 30, 40);
        Assert.Equal(JobAction.Skip, JobRules.Decide(done, first.Fingerprint, "tv", "dv", T).Action);

        var json = Path.ChangeExtension(seg.AudioPath, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { EndedBy = "crash" });
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddMinutes(1));
        var second = Assert.Single(discovery.Scan());

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(JobAction.Run, JobRules.Decide(done, second.Fingerprint, "tv", "dv", T).Action);
    }

    [Fact]
    public void B_is_planned_only_where_a_leaves_a_hole()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(B, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 3600, 0, gaps: [new SegmentGap(T.AddHours(1).AddMinutes(20), T.AddHours(1).AddMinutes(30), 600, "stream ended by server")]);
        SegmentFiles.Closed(B, "burst", T.AddHours(1), 3600, 0);
        SegmentFiles.Closed(B, "burst", T.AddHours(2), 3600, 0);

        var jobs = WorkPlanner.Plan(Discovery().Scan(), ["a", "b"], since: null);

        Assert.Equal(new[] { "b/2026/10-16/100000", "a/2026/10-16/090000", "b/2026/10-16/090000", "a/2026/10-16/080000" }, jobs.Select(j => j.Source.JobId));
        Assert.True(jobs[0].FillIn);
        Assert.False(jobs[1].FillIn);
    }

    [Fact]
    public void Covered_fraction_counts_spans_minus_gaps_once()
    {
        SegmentFiles.Closed(A, "burst", T, 1800, 0, gaps: [new SegmentGap(T.AddMinutes(20), T.AddMinutes(30), 600, "x")]);
        SegmentFiles.Closed(A, "burst", T.AddMinutes(30), 1800, 0, name: "083000");
        SegmentFiles.Closed(B, "burst", T, 3600, 0);
        var all = Discovery().Scan();
        var b = all.Single(s => s.Recorder == "b");

        Assert.Equal(50.0 / 60, WorkPlanner.CoveredFraction(b, all.Where(s => s.Recorder == "a")), 6);
    }

    [Fact]
    public void Since_leaves_out_older_segments()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 3600, 0);
        var jobs = WorkPlanner.Plan(Discovery().Scan(), ["a", "b"], since: T.AddHours(1).AddMinutes(1));
        Assert.Equal("a/2026/10-16/090000", Assert.Single(jobs).Source.JobId);
    }

    public static TheoryData<string?, string, JobAction> Decisions => new()
    {
        { null, "", JobAction.Run },
        { JobStatus.Done, "", JobAction.Skip },
        { JobStatus.Done, "detector", JobAction.Redetect },
        { JobStatus.Done, "transcript", JobAction.Run },
        { JobStatus.Running, "", JobAction.Run },
        { JobStatus.Waiting, "future", JobAction.Wait },
        { JobStatus.Waiting, "past", JobAction.Run },
        { JobStatus.Failed, "future", JobAction.Wait },
        { JobStatus.Failed, "exhausted", JobAction.Skip },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Job_rules(string? status, string change, JobAction expected)
    {
        var state = status is null ? null : new JobState(1, "a/x/y/z", "fp", "tv", "dv", status,
            change == "exhausted" ? JobRules.MaxAttempts : 1,
            change switch { "future" => T.AddMinutes(5), "past" => T.AddMinutes(-5), "exhausted" => T.AddMinutes(-5), _ => null },
            null, [], 0, T, 0, 0, 0);
        var tv = change == "transcript" ? "tv2" : "tv";
        var dv = change == "detector" ? "dv2" : "dv";

        Assert.Equal(expected, JobRules.Decide(state, "fp", tv, dv, T).Action);
    }

    [Fact]
    public void Retry_backoff_doubles_from_five_minutes_and_stops_at_six_hours()
    {
        Assert.Equal(T.AddMinutes(5), JobRules.NextAttempt(1, T));
        Assert.Equal(T.AddMinutes(10), JobRules.NextAttempt(2, T));
        Assert.Equal(T.AddMinutes(20), JobRules.NextAttempt(3, T));
        Assert.Equal(T.AddHours(6), JobRules.NextAttempt(20, T));
    }

    [Fact]
    public void Job_state_round_trips_and_a_damaged_file_reads_as_none()
    {
        var dir = Path.Combine(_root, "state");
        var state = new JobState(1, "a/2026/10-16/080000", "fp", "tv", "dv", JobStatus.Running, 2, T, "x", [0, 1], 3, T, 3600, 120, 150);
        JobStore.Write(dir, state);
        var back = JobStore.Read(dir)!;
        Assert.Equal(state with { ChunksDone = back.ChunksDone }, back);
        Assert.Equal(new[] { 0, 1 }, back.ChunksDone);

        File.WriteAllText(Path.Combine(dir, "state.json"), "{");
        Assert.Null(JobStore.Read(dir));
    }

    [Fact]
    public void Disk_guard_reports_low_space()
    {
        var free = 30L << 30;
        var guard = new DiskGuard(_root, 20L << 30, _ => free);
        Assert.False(guard.IsLow());
        free = 19L << 30;
        Assert.True(guard.IsLow());
        Assert.True(new DiskGuard(_root, 0).FreeBytes() > 0);
    }
}
