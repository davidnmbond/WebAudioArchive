using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

// Final fix wave (whole-branch review): service lock after a hard exit, events re-placed from the current sidecar.
public sealed partial class AnalysisServiceTests
{
    static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    [Fact]
    public void A_lock_left_by_our_own_process_id_on_this_host_is_ours_and_is_taken_over_at_once()
    {
        // A restarted container is PID 1 again with the same hostname; a hard exit left a fresh lock naming that pid.
        var dir = Path.Combine(_root, "lock-own");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(dir, ServiceLock.FileName), $$"""{ "pid": {{Environment.ProcessId}}, "host": "{{Environment.MachineName}}", "startedUtc": "2026-01-01T00:00:00Z" }""");

        Assert.Null(ServiceLock.RunningMessage(dir, now));
        Assert.True(new ServiceLock(dir).TryAcquire(now, out var problem), problem);
    }

    [Fact]
    public void A_fresh_lock_from_another_instance_is_still_refused_even_when_it_names_our_pid()
    {
        // Another container (the maintenance run, or a second service) shares the folder; pid 1 means nothing across containers.
        var dir = Path.Combine(_root, "lock-other");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(dir, ServiceLock.FileName), $$"""{ "pid": {{Environment.ProcessId}}, "host": "some-other-container", "startedUtc": "2026-01-01T00:00:00Z" }""");

        Assert.Contains("already running", ServiceLock.RunningMessage(dir, now));
        Assert.False(new ServiceLock(dir).TryAcquire(now, out _));
        File.WriteAllText(Path.Combine(dir, ServiceLock.FileName), """{ "pid": 1, "host": "some-other-container", "startedUtc": "2026-01-01T00:00:00Z" }""");
        Assert.NotNull(ServiceLock.RunningMessage(dir, now));
    }

    [Fact]
    public void On_the_same_host_a_live_pid_with_a_different_process_start_time_is_a_reused_pid_and_stale()
    {
        var dir = Path.Combine(_root, "lock-reuse");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 30 127.0.0.1") : new ProcessStartInfo("sleep", "30");
        psi.RedirectStandardOutput = true;
        psi.CreateNoWindow = true;
        using var other = Process.Start(psi)!;
        try
        {
            var started = new DateTimeOffset(other.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var file = Path.Combine(dir, ServiceLock.FileName);

            File.WriteAllText(file, $$"""{ "pid": {{other.Id}}, "host": "{{Environment.MachineName}}", "processStartUtc": "{{Stamp(started)}}", "startedUtc": "2026-01-01T00:00:00Z" }""");
            Assert.NotNull(ServiceLock.RunningMessage(dir, now));

            File.WriteAllText(file, $$"""{ "pid": {{other.Id}}, "host": "{{Environment.MachineName}}", "processStartUtc": "{{Stamp(started.AddHours(-1))}}", "startedUtc": "2026-01-01T00:00:00Z" }""");
            Assert.Null(ServiceLock.RunningMessage(dir, now));
        }
        finally
        {
            other.Kill();
        }
    }

    [Fact]
    public void A_lock_records_the_host_and_the_process_start_time()
    {
        var dir = Path.Combine(_root, "lock-fields");
        var held = new ServiceLock(dir);
        Assert.True(held.TryAcquire(DateTimeOffset.UtcNow, out _));

        var text = File.ReadAllText(Path.Combine(dir, ServiceLock.FileName));
        Assert.Contains($"\"host\":\"{Environment.MachineName}\"", text.Replace(" ", ""));
        Assert.Contains("processStartUtc", text);
        held.Release();
    }

    [Fact]
    public async Task Rebuilding_events_takes_the_timeline_from_the_current_sidecar_so_a_position_fix_moves_them()
    {
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var eventsFile = Path.Combine(dir, AnalysisPaths.EventsFile);
        var before = AnalysisJson.Read<EventsDocument>(eventsFile)!.Events.Single(e => e.Type == EventTypes.Filler).StartUtc;

        // The sidecar now says the audio is 10 s longer than the wall span: a 10 s lead before the first connection.
        var sidecar = Path.Combine(A, "burst", "master", "2026", "10-15", "190000.json");
        var meta = SegmentMetaStore.Read(sidecar)!;
        SegmentMetaStore.Write(sidecar, meta with { AudioSeconds = meta.AudioSeconds + 10 });
        Assert.Equal(1, runner.RebuildAll());

        var after = AnalysisJson.Read<EventsDocument>(eventsFile)!.Events.Single(e => e.Type == EventTypes.Filler).StartUtc;
        Assert.Equal(before.AddSeconds(-10), after);
        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(dir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal(Show.AddSeconds(-10), transcript.Timeline[0].Utc);
        Assert.Equal(meta.AudioSeconds + 10, transcript.AudioSeconds, 3);
    }

    [Fact]
    public async Task Writing_a_json_file_survives_a_reader_that_briefly_holds_it_open()
    {
        var path = Path.Combine(_root, "locked.json");
        AnalysisJson.Write(path, new { a = 1 });

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = Task.Run(() => AnalysisJson.Write(path, new { a = 2 }));
        await Task.Delay(120);
        await reader.DisposeAsync();
        await write;

        Assert.Contains("2", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "locked.json.*.tmp"));
    }
}
