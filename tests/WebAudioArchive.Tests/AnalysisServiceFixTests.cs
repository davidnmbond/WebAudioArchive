extern alias analysissvc;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

// Fix round 1 (reviewer findings): health under failing passes, low disk lag, show completeness, boundaries, service lock.
public sealed partial class AnalysisServiceTests
{
    string ShowFile => Path.Combine(Paths.ShowDir("burst-20261015T1900Z", Show), AnalysisPaths.ShowAnalysisFile);

    [Fact]
    public async Task A_pass_that_throws_is_recorded_not_counted_as_life_and_turns_health_unhealthy()
    {
        _diskBroken = true;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var first = metrics.Snapshot();
        Assert.Contains("disk gone", first.LastPassError);
        Assert.Null(first.LastPassUtc);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(first.LastActivityUtc, metrics.Snapshot().LastActivityUtc);
        Assert.Equal(200, AnalysisHealth.Build(metrics.Snapshot(), TimeSpan.FromHours(6), _clock.UtcNow).StatusCode);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await runner.PassAsync(CancellationToken.None);
        var health = AnalysisHealth.Build(metrics.Snapshot(), TimeSpan.FromHours(6), _clock.UtcNow);
        Assert.Equal(503, health.StatusCode);
        Assert.Contains("disk gone", JsonSerializer.Serialize(health.Body));
    }

    [Fact]
    public void Failing_passes_are_unhealthy_even_when_jobs_keep_showing_activity()
    {
        var now = Show.AddHours(10);
        var m = new MetricsSnapshot(now.AddHours(-5), now.AddMinutes(-30), 0, 0, null, 0, 0, null, null, 0, null, null, true, 0, 0, 0, 0, 0,
            LastActivityUtc: now.AddMinutes(-1), LastPassError: "IOException: disk gone");

        var h = AnalysisHealth.Build(m, TimeSpan.FromHours(6), now);

        Assert.Equal(503, h.StatusCode);
        Assert.Contains("\"status\":\"failing\"", JsonSerializer.Serialize(h.Body));
        Assert.Equal(200, AnalysisHealth.Build(m with { LastPassError = null }, TimeSpan.FromHours(6), now).StatusCode);
    }

    [Fact]
    public async Task Low_disk_still_measures_the_backlog_so_lag_grows_and_health_goes_503()
    {
        var (runner, metrics) = Runner();
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(0, metrics.Snapshot().Backlog);

        _free = 1;
        var next = Show.AddHours(1);
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", next, Signals.SpeechLike(10, 6));
        _clock.Set(next.AddHours(8));
        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(1, _whisper.Calls);
        Assert.Equal(1, m.Backlog);
        Assert.Equal(next.AddSeconds(10), m.OldestPendingEndUtc);
        Assert.Contains("free disk", m.Paused);
        var health = AnalysisHealth.Build(m, TimeSpan.FromHours(6), _clock.UtcNow);
        Assert.Equal(503, health.StatusCode);
        Assert.Contains("\"status\":\"lagging\"", JsonSerializer.Serialize(health.Body));
    }

    JobState WaitingState(SourceSegment segment, int attempts, DateTimeOffset next)
    {
        var config = AnalysisConfigLoader.Load(ConfigPath);
        return new JobState(JobState.CurrentSchema, segment.JobId, segment.Fingerprint, $"{_whisper.Identity}|{config.Config.Transcription.TranscriptKey()}",
            config.DetectorVersion, JobStatus.Waiting, attempts, next, "retry later", [], 0, _clock.UtcNow, 35, 0, 0);
    }

    [Fact]
    public async Task A_job_that_fails_for_good_completes_its_show_without_a_restart()
    {
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var segment = new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan().Single();
        JobStore.Write(dir, WaitingState(segment, JobRules.MaxAttempts - 1, _clock.UtcNow + TimeSpan.FromMinutes(10)));
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        Assert.False(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);

        _clock.Advance(TimeSpan.FromMinutes(11));
        _whisper.Respond = (_, _) => throw new InvalidOperationException("boom");
        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Failed, JobStore.Read(dir)!.Status);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);
    }

    [Fact]
    public async Task Starting_with_whisper_missing_does_not_write_incomplete_shows_and_they_complete_when_it_returns()
    {
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);

        // A restart with the model missing: every finished segment looks new to the planner (the engine identity changed; a model switch alone would not do it).
        _whisper.Identity = "fake-next-major";
        _whisper.Ready = false;
        var (second, metrics) = Runner();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);
        Assert.Equal(1, metrics.Snapshot().Backlog);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);

        _whisper.Identity = "fake";
        _whisper.Ready = true;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);

        Assert.Equal(0, metrics.Snapshot().Backlog);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);
        Assert.Equal(1, _whisper.Calls);
    }

    [Fact]
    public async Task A_show_left_incomplete_is_rechecked_every_pass_and_completes_when_nothing_is_pending()
    {
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var segment = new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan().Single();
        JobStore.Write(dir, WaitingState(segment, 1, _clock.UtcNow + TimeSpan.FromMinutes(10)));
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        Assert.False(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);

        // The state file is removed behind the runner's back and the segment is analysed elsewhere: nothing is pending, nothing touched.
        _clock.Advance(TimeSpan.FromMinutes(11));
        JobStore.Write(dir, WaitingState(segment, 1, _clock.UtcNow) with { Status = JobStatus.Done, Attempts = 0, DetectorVersion = AnalysisConfigLoader.Load(ConfigPath).DetectorVersion });
        await runner.PassAsync(CancellationToken.None);

        Assert.True(AnalysisJson.Read<ShowAnalysis>(ShowFile)!.Complete);
    }

    [Fact]
    public async Task A_bad_sidecar_is_reported_once_per_class_not_on_every_pass()
    {
        var odd = Path.Combine(A, "burst", "master", "2026", "10-15", "19 00 00.mp3");
        File.Copy(Path.Combine(A, "burst", "master", "2026", "10-15", "190000.mp3"), odd);
        File.Copy(Path.Combine(A, "burst", "master", "2026", "10-15", "190000.json"), Path.ChangeExtension(odd, ".json"));
        SegmentMetaStore.Write(Path.ChangeExtension(odd, ".json"), SegmentMetaStore.Read(Path.ChangeExtension(odd, ".json"))! with { File = "19 00 00.mp3" });
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);
        await runner.PassAsync(CancellationToken.None);
        await runner.PassAsync(CancellationToken.None);

        var status = metrics.Snapshot().Classes!["a"];
        Assert.Equal(1, status.Failed);
        Assert.Contains("19 00 00", status.LastErrorJob);
        Assert.Contains("cannot be analysed", status.LastError);
    }

    public static TheoryData<int, int, int, int> BoundaryCases => new()
    {
        // lag seconds over the 6 h alarm, seconds since the last pass over 15 min, seconds a job has run over 3 h, expected code
        { 0, 0, 0, 200 },
        { 1, 0, 0, 503 },
        { 0, 1, 0, 503 },
        { 0, 0, 1, 503 },
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Health_boundaries_are_exact(int lagOver, int passOver, int jobOver, int code)
    {
        var now = Show.AddHours(30);
        var alarm = TimeSpan.FromHours(6);
        MetricsSnapshot Idle(int over) => new(now.AddDays(-1), now - AnalysisHealth.StallAfter - TimeSpan.FromSeconds(over), 1, 0,
            now - alarm - TimeSpan.FromSeconds(lagOver), 0, 0, null, null, 0, null, null, true, 0, 0, 0, 0, 0);
        MetricsSnapshot Running(int over) => new(now.AddDays(-1), now.AddMinutes(-1), 1, 0, now - alarm - TimeSpan.FromSeconds(lagOver), 0, 0, null, null, 0, "a/2026/10-15/190000", null,
            true, 0, 0, 0, 0, 0, LastActivityUtc: now - AnalysisHealth.JobStallAfter - TimeSpan.FromSeconds(over));

        Assert.Equal(passOver > 0 || lagOver > 0 ? 503 : 200, AnalysisHealth.Build(Idle(passOver), alarm, now).StatusCode);
        Assert.Equal(jobOver > 0 || lagOver > 0 ? 503 : 200, AnalysisHealth.Build(Running(jobOver), alarm, now).StatusCode);
        Assert.Equal(code, new[] { lagOver > 0, passOver > 0, jobOver > 0 }.Any(b => b) ? 503 : 200);
    }

    [Fact]
    public void The_job_stall_limit_is_at_least_the_ollama_timeout()
    {
        Assert.Equal(AnalysisHealth.JobStallAfter, AnalysisSettings.Load(Env()).JobStallAfter);
        Assert.Equal(TimeSpan.FromMinutes(240) + TimeSpan.FromMinutes(5), AnalysisSettings.Load(Env(("WAA_OLLAMA_TIMEOUT_MINUTES", "240"))).JobStallAfter);

        var now = Show.AddHours(30);
        var m = new MetricsSnapshot(now.AddDays(-1), now.AddMinutes(-1), 0, 0, null, 0, 0, null, null, 0, "enrich:x", null, true, 0, 0, 0, 0, 0,
            LastActivityUtc: now.AddHours(-4));
        Assert.Equal(503, AnalysisHealth.Build(m, TimeSpan.FromHours(6), now).StatusCode);
        Assert.Equal(200, AnalysisHealth.Build(m, TimeSpan.FromHours(6), now, TimeSpan.FromHours(5)).StatusCode);
    }

    [Fact]
    public void A_scratch_folder_named_like_a_parent_step_is_still_inside_the_protected_root()
    {
        var protectedRoot = Directory.CreateDirectory(Path.Combine(_root, "prot")).FullName;
        Assert.Throws<ArgumentException>(() => new ScratchSpace(Path.Combine(protectedRoot, "..x"), 1 << 20, [protectedRoot]));
        Assert.Throws<ArgumentException>(() => new ScratchSpace(Path.Combine(protectedRoot, "scratch"), 1 << 20, [protectedRoot]));
        Assert.Throws<ArgumentException>(() => new ScratchSpace(_root, 1 << 20, [protectedRoot]));
        var ok = new ScratchSpace(Path.Combine(_root, "prot2"), 1 << 20, [protectedRoot]);
        Assert.Equal(Path.Combine(_root, "prot2"), ok.Root);
    }

    [Fact]
    public void The_service_lock_blocks_a_second_holder_and_the_maintenance_commands_until_released()
    {
        var dir = Path.Combine(_root, "lockdir");
        var now = DateTimeOffset.UtcNow;
        var first = new ServiceLock(dir);
        Assert.True(first.TryAcquire(now, out _));
        // This process cannot tell itself from a second holder (its own pid is "ours"), so the lock is made to look like another instance's.
        var lockFile = Path.Combine(dir, ServiceLock.FileName);
        File.WriteAllText(lockFile, File.ReadAllText(lockFile).Replace(Environment.MachineName, "another-container"));

        Assert.Contains("Stop the service first", ServiceLock.RunningMessage(dir, now));
        Assert.DoesNotContain("docker compose", ServiceLock.RunningMessage(dir, now));  // environment neutral: also read in Kubernetes
        var second = new ServiceLock(dir);
        Assert.False(second.TryAcquire(now, out var why));
        Assert.Contains("already running", why);

        first.Release();
        Assert.Null(ServiceLock.RunningMessage(dir, now));
        Assert.True(second.TryAcquire(now, out _));
        second.Release();
        Assert.False(File.Exists(Path.Combine(dir, ServiceLock.FileName)));
    }

    [Fact]
    public void A_stale_service_lock_is_ignored_and_taken_over()
    {
        var dir = Path.Combine(_root, "lockdir2");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, ServiceLock.FileName);
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(file, $$"""{ "pid": {{Environment.ProcessId}}, "startedUtc": "2026-01-01T00:00:00Z" }""");
        File.SetLastWriteTimeUtc(file, now.UtcDateTime - ServiceLock.StaleAfter - TimeSpan.FromMinutes(1));
        Assert.Null(ServiceLock.RunningMessage(dir, now));

        File.WriteAllText(file, """{ "pid": 2147480000, "startedUtc": "2026-01-01T00:00:00Z" }""");
        Assert.Null(ServiceLock.RunningMessage(dir, now));

        File.WriteAllText(file, "not json");
        Assert.Null(ServiceLock.RunningMessage(dir, now));

        File.WriteAllText(file, $$"""{ "pid": {{Environment.ProcessId}}, "host": "another-container", "startedUtc": "2026-01-01T00:00:00Z" }""");
        Assert.NotNull(ServiceLock.RunningMessage(dir, now));
        Assert.True(new ServiceLock(dir).TryAcquire(now.AddMinutes(30), out _));
    }

    [Fact]
    public async Task The_host_holds_the_lock_while_it_runs_and_refuses_to_start_beside_a_live_one()
    {
        await using (var factory = Host())
        {
            factory.CreateClient();
            Assert.True(File.Exists(Path.Combine(_root, "host-out", ServiceLock.FileName)));
        }

        Assert.False(File.Exists(Path.Combine(_root, "host-out", ServiceLock.FileName)));

        Directory.CreateDirectory(Path.Combine(_root, "host-out"));
        File.WriteAllText(Path.Combine(_root, "host-out", ServiceLock.FileName), $$"""{ "pid": {{Environment.ProcessId}}, "host": "another-container", "startedUtc": "2026-01-01T00:00:00Z" }""");
        using var second = Host();
        Assert.ThrowsAny<Exception>(() => second.CreateClient());
    }
}
