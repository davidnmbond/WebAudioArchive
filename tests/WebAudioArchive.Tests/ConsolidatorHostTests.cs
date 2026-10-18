using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using WebAudioArchive.Worker.Consolidation;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #104: the recorder image started with --consolidate merges eligible hours, holds originals in hold mode,
/// rechecks them, and deletes only what a manager confirms, reporting all of it on /healthz.
/// </summary>
public sealed class ConsolidatorHostTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-consolidator-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(1);
    static readonly DateTimeOffset[] Starts = [T0 - SegmentLength, T0, T0.AddHours(1)];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    string MergedRoot => Path.Combine(_root, "merged");

    string MergedMp3(DateTimeOffset hour) => Path.Combine(MergedRoot, "burst", "master", "2026", "10-15", hour.ToString("HHmmss") + ".mp3");

    string MasterMp3(string id, DateTimeOffset start) => Path.Combine(Root(id), "burst", "master", "2026", "10-15", start.ToString("HHmmss") + ".mp3");

    string HeldMp3(string id, DateTimeOffset start) => Path.Combine(Root(id), "burst", "held", "2026", "10-15", start.ToString("HHmmss") + ".mp3");

    /// <summary>A and B each record a minute at 09:59, 10:00 and 11:00 on 2026-10-15 (short, so the tests run quickly).</summary>
    void Record()
    {
        Directory.CreateDirectory(MergedRoot);
        foreach (var id in new[] { "a", "b" })
            foreach (var start in Starts)
                SegmentFiles.Live(Root(id), "burst", Epoch, start, start + SegmentLength);
    }

    /// <summary>
    /// The consolidator host as --consolidate builds it, on a test server. Built through <see cref="ConsolidatorHost.Build"/>
    /// directly, because production starts it only for the command-line argument, never for a setting.
    /// </summary>
    sealed class TestConsolidator(Dictionary<string, string?> settings, IClock clock) : IAsyncDisposable
    {
        WebApplication? _app;

        public async Task<(HttpClient Client, ConsolidatorService Service)> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(clock);
            var (app, exitCode) = ConsolidatorHost.Build(builder);
            Assert.True(app is not null, $"the consolidator did not build (exit code {exitCode})");
            _app = app;
            await app.StartAsync();
            var service = app.Services.GetRequiredService<ConsolidatorService>();
            await service.FirstRun.WaitAsync(TimeSpan.FromSeconds(60));
            return (app.GetTestClient(), service);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }
    }

    TestConsolidator Host(string mode, ManualClock clock, string analysis = "") => new(new()
    {
        ["WAA_STATION"] = "burst",
        ["WAA_SOURCES"] = $"a={Root("a")},b={Root("b")}",
        ["WAA_MERGED"] = MergedRoot,
        ["WAA_ANALYSIS"] = analysis,
        ["WAA_CONSOLIDATE_MODE"] = mode,
    }, clock);

    /// <summary>Starts the host and waits for the run it does at start.</summary>
    static Task<(HttpClient Client, ConsolidatorService Service)> StartAsync(TestConsolidator host) => host.StartAsync();

    static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    static async Task<JsonElement> Healthz(HttpClient client)
    {
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    static DateTimeOffset Utc(JsonElement e) => DateTimeOffset.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task In_merge_mode_an_eligible_hour_is_merged_and_nothing_moves()
    {
        Record();
        var analysis = Path.Combine(_root, "analysis");
        var paths = new AnalysisPaths(analysis, "burst");
        foreach (var id in new[] { "a", "b" })
            foreach (var start in Starts.Take(2))
            {
                var dir = paths.SegmentDir($"{id}/2026/10-15/{start:HHmmss}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, AnalysisPaths.StateFile), $$"""{"status":"{{JobStatus.Done}}"}""");
            }

        var before = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(f => f.StartsWith(Root("a")) || f.StartsWith(Root("b")))
            .ToDictionary(f => f, File.ReadAllBytes);

        await using var factory = Host("merge", new ManualClock(Now), analysis);
        var (client, _) = await StartAsync(factory);

        Assert.True(File.Exists(MergedMp3(T0.AddHours(-1))));
        Assert.True(File.Exists(Path.ChangeExtension(MergedMp3(T0), ".json")));
        Assert.False(File.Exists(MergedMp3(T0.AddHours(1))), "11:00 is waiting for analysis");
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(Root("a"), "burst", "held")));
        Assert.False(Directory.Exists(Path.Combine(Root("b"), "burst", "held")));

        var health = await Healthz(client);
        Assert.Equal("burst", health.GetProperty("station").GetString());
        Assert.Equal("merge", health.GetProperty("mode").GetString());
        Assert.Equal(T0, Utc(health.GetProperty("lastMergedHourUtc")));
        Assert.Equal(0, health.GetProperty("backlogHours").GetInt32());
        Assert.Equal(1, health.GetProperty("waitingForAnalysis").GetInt32());
        Assert.Equal(0, health.GetProperty("failures").GetArrayLength());
        Assert.Equal(0, health.GetProperty("held").GetInt32());
        Assert.Equal(0, health.GetProperty("ready").GetInt32());
        Assert.Equal(0, health.GetProperty("deletedBytes").GetInt64());
    }

    [Fact]
    public async Task In_hold_mode_merged_sources_move_to_held_and_healthz_counts_them()
    {
        Record();
        await using var factory = Host("hold", new ManualClock(Now));
        var (client, _) = await StartAsync(factory);

        foreach (var hour in Starts.Select(s => new DateTimeOffset(s.Year, s.Month, s.Day, s.Hour, 0, 0, TimeSpan.Zero)))
            Assert.True(File.Exists(MergedMp3(hour)), hour.ToString("O"));

        // Every hour each segment overlaps is merged, and the hours beside them are merged or hold no recording at all (08:00, 12:00).
        foreach (var id in new[] { "a", "b" })
        {
            Assert.False(File.Exists(MasterMp3(id, T0)));
            Assert.True(File.Exists(HeldMp3(id, T0)));
            Assert.True(File.Exists(Holding.RecordPath(HeldMp3(id, T0))));
            Assert.True(File.Exists(HeldMp3(id, Starts[0])));
            Assert.True(File.Exists(HeldMp3(id, Starts[2])));
        }

        var health = await Healthz(client);
        Assert.Equal("hold", health.GetProperty("mode").GetString());
        Assert.Equal(T0.AddHours(1), Utc(health.GetProperty("lastMergedHourUtc")));
        Assert.Equal(6, health.GetProperty("held").GetInt32());
        Assert.Equal(0, health.GetProperty("ready").GetInt32());
        Assert.Equal(0, health.GetProperty("backlogHours").GetInt32());
    }

    [Fact]
    public async Task A_verification_failure_stops_the_run_and_is_reported_on_healthz()
    {
        Record();
        // Bytes the sidecar does not account for: the writer refuses this source.
        File.AppendAllBytes(MasterMp3("a", T0), [0, 0, 0, 0]);

        await using var factory = Host("hold", new ManualClock(Now));
        var (client, _) = await StartAsync(factory);

        Assert.True(File.Exists(MergedMp3(T0.AddHours(-1))));
        Assert.False(File.Exists(MergedMp3(T0)));
        Assert.False(File.Exists(MergedMp3(T0.AddHours(1))), "the run stops at the failure");
        Assert.False(Directory.Exists(Path.Combine(Root("a"), "burst", "held")));
        Assert.False(Directory.Exists(Path.Combine(Root("b"), "burst", "held")));

        var health = await Healthz(client);
        var failure = Assert.Single(health.GetProperty("failures").EnumerateArray());
        Assert.Equal(T0, Utc(failure.GetProperty("hourUtc")));
        Assert.Contains("100000.mp3", failure.GetProperty("reason").GetString());
        Assert.Equal(T0.AddHours(-1), Utc(health.GetProperty("lastMergedHourUtc")));

        var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(MergedRoot, "burst", "failures.json"))).RootElement;
        var entry = Assert.Single(stored.EnumerateArray());
        Assert.Equal(T0, Utc(entry.GetProperty("hourUtc")));
        Assert.Equal(Now, Utc(entry.GetProperty("utc")));
        var sources = entry.GetProperty("sources").EnumerateArray().ToList();
        Assert.Contains(sources, s => s.GetProperty("recorder").GetString() == "a" && s.GetProperty("file").GetString() == "2026/10-15/100000.mp3");
        Assert.Contains(sources, s => s.GetProperty("recorder").GetString() == "b" && s.GetProperty("file").GetString() == "2026/10-15/100000.mp3");
        Assert.All(sources, s => Assert.Equal(64, s.GetProperty("sha256").GetString()!.Length));
    }

    [Fact]
    public async Task Deletion_needs_a_ready_record_and_reports_each_item()
    {
        Record();
        var clock = new ManualClock(Now);
        await using var factory = Host("hold", clock);
        var (client, service) = await StartAsync(factory);
        var aBytes = new FileInfo(HeldMp3("a", T0)).Length;
        var bBytes = new FileInfo(HeldMp3("b", T0)).Length;

        object Body(params (string Recorder, string File)[] items) =>
            new { confirmedBy = "manager", items = items.Select(i => new { recorder = i.Recorder, file = i.File }).ToArray() };

        var early = await Json(await client.PostAsJsonAsync("/deletions", Body(("a", "2026/10-15/100000.mp3"))));
        var refused = Assert.Single(early.EnumerateArray());
        Assert.False(refused.GetProperty("deleted").GetBoolean());
        Assert.Contains("not ready", refused.GetProperty("reason").GetString());
        Assert.True(File.Exists(HeldMp3("a", T0)));
        Assert.Equal(0, (await Json(await client.GetAsync("/held"))).GetArrayLength());

        clock.Advance(Holding.HoldPeriod);
        await service.RunOnceAsync(CancellationToken.None);
        var ready = await Json(await client.GetAsync("/held"));
        Assert.Equal(6, ready.GetArrayLength());
        Assert.All(ready.EnumerateArray(), r => Assert.Equal("ready", r.GetProperty("status").GetString()));
        Assert.Equal(6, (await Healthz(client)).GetProperty("ready").GetInt32());

        var results = (await Json(await client.PostAsJsonAsync("/deletions", Body(
            ("a", "2026/10-15/100000.mp3"), ("a", "../../escape.mp3"), ("c", "2026/10-15/100000.mp3"))))).EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.True(results[0].GetProperty("deleted").GetBoolean());
        Assert.Equal("a", results[0].GetProperty("recorder").GetString());
        Assert.Equal("2026/10-15/100000.mp3", results[0].GetProperty("file").GetString());
        Assert.False(results[1].GetProperty("deleted").GetBoolean());
        Assert.False(string.IsNullOrEmpty(results[1].GetProperty("reason").GetString()));
        Assert.False(results[2].GetProperty("deleted").GetBoolean());
        Assert.Contains("c", results[2].GetProperty("reason").GetString());
        Assert.False(File.Exists(HeldMp3("a", T0)));
        Assert.True(File.Exists(HeldMp3("b", T0)));
        Assert.Contains("\"manager\"", File.ReadAllText(Path.Combine(MergedRoot, "burst", "deletions.jsonl")));

        var health = await Healthz(client);
        Assert.Equal(5, health.GetProperty("ready").GetInt32());
        Assert.Equal(aBytes, health.GetProperty("deletedBytes").GetInt64());

        // A check older than a day must be renewed first; the hourly run renews it.
        clock.Advance(TimeSpan.FromHours(25));
        var stale = Assert.Single((await Json(await client.PostAsJsonAsync("/deletions", Body(("b", "2026/10-15/100000.mp3"))))).EnumerateArray());
        Assert.False(stale.GetProperty("deleted").GetBoolean());
        Assert.Contains("recheck due", stale.GetProperty("reason").GetString());

        await service.RunOnceAsync(CancellationToken.None);
        var fresh = Assert.Single((await Json(await client.PostAsJsonAsync("/deletions", Body(("b", "2026/10-15/100000.mp3"))))).EnumerateArray());
        Assert.True(fresh.GetProperty("deleted").GetBoolean(), fresh.GetProperty("reason").ToString());
        Assert.False(File.Exists(HeldMp3("b", T0)));
        health = await Healthz(client);
        Assert.Equal(4, health.GetProperty("ready").GetInt32());
        Assert.Equal(aBytes + bBytes, health.GetProperty("deletedBytes").GetInt64());
    }

    [Fact]
    public async Task A_failed_hour_is_not_retried_until_a_source_changes_or_retry_is_posted()
    {
        Record();
        // A minute at 10:59 too, so 10:00 runs up to 11:00 and 11:00 can join it by its tail.
        foreach (var id in new[] { "a", "b" }) SegmentFiles.Live(Root(id), "burst", Epoch, T0.AddMinutes(59), T0.AddHours(1));
        var corrupt = MasterMp3("a", T0);
        var original = File.ReadAllBytes(corrupt);
        File.AppendAllBytes(corrupt, [0, 0, 0, 0]);
        var clock = new ManualClock(Now);
        var failures = Path.Combine(MergedRoot, "burst", "failures.json");
        DateTimeOffset FailedUtc() => Utc(Assert.Single(JsonDocument.Parse(File.ReadAllText(failures)).RootElement.EnumerateArray()).GetProperty("utc"));

        await using var factory = Host("merge", clock);
        var (client, service) = await StartAsync(factory);
        Assert.Equal(Now, FailedUtc());

        // Same inputs: not tried again.
        clock.Advance(TimeSpan.FromHours(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(Now, FailedUtc());
        Assert.False(File.Exists(MergedMp3(T0)));
        // Merging stops at the unresolved failure: 11:00 waits, so it can later join 10:00 by its tail.
        Assert.False(File.Exists(MergedMp3(T0.AddHours(1))));
        Assert.Equal(2, (await Healthz(client)).GetProperty("backlogHours").GetInt32());

        // A manager asks: the entry goes and the next run tries again (and fails again, so a new entry).
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/retry", new { hourUtc = "2026-10-15T08:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/retry", new { hourUtc = "2026-10-15T10:00:00Z" })).StatusCode);
        Assert.Equal(0, (await Healthz(client)).GetProperty("failures").GetArrayLength());
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(Now.AddHours(1), FailedUtc());
        Assert.False(File.Exists(MergedMp3(T0)));
        Assert.False(File.Exists(MergedMp3(T0.AddHours(1))));

        // The source changes: the hour is tried again, merges, and the failure is cleared; 11:00 follows on 10:00's tail.
        File.WriteAllBytes(corrupt, original);
        clock.Advance(TimeSpan.FromHours(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(MergedMp3(T0)));
        var eleven = SegmentMetaStore.Read(Path.ChangeExtension(MergedMp3(T0.AddHours(1)), ".json"));
        Assert.NotNull(eleven?.Merge);
        Assert.False(eleven.Merge.JoinedByTime);
        Assert.Equal(0, (await Healthz(client)).GetProperty("backlogHours").GetInt32());
        Assert.Equal(0, (await Healthz(client)).GetProperty("failures").GetArrayLength());
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(failures)).RootElement.EnumerateArray());
    }

    /// <summary>A and B each record a minute at 10:00 and 11:00 only: nothing at all in 09:00.</summary>
    void RecordFromTen()
    {
        Directory.CreateDirectory(MergedRoot);
        foreach (var id in new[] { "a", "b" })
            foreach (var start in Starts.Skip(1))
                SegmentFiles.Live(Root(id), "burst", Epoch, start, start + SegmentLength);
    }

    [Fact]
    public async Task A_segment_beside_an_hour_with_no_recording_is_held()
    {
        RecordFromTen();
        await using var factory = Host("hold", new ManualClock(Now));
        var (client, _) = await StartAsync(factory);

        Assert.False(File.Exists(MergedMp3(T0.AddHours(-1))));
        foreach (var id in new[] { "a", "b" })
        {
            Assert.True(File.Exists(HeldMp3(id, T0)), id);
            Assert.False(File.Exists(MasterMp3(id, T0)), id);
            Assert.True(File.Exists(HeldMp3(id, T0.AddHours(1))), "12:00 holds no recording either");
        }

        var health = await Healthz(client);
        Assert.Equal(4, health.GetProperty("held").GetInt32());
        Assert.Equal(0, health.GetProperty("backlogHours").GetInt32());
    }

    [Fact]
    public async Task An_hour_whose_segments_hold_no_frame_is_resolved_empty_and_its_neighbours_are_held()
    {
        RecordFromTen();
        // A segment that closed with no audio at all: the 09:00 hour has a source but nothing to merge.
        var empty = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
        SegmentFiles.Live(Root("a"), "burst", Epoch, empty, empty);

        await using var factory = Host("hold", new ManualClock(Now));
        var (client, service) = await StartAsync(factory);

        Assert.False(File.Exists(MergedMp3(T0.AddHours(-1))));
        var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(MergedRoot, "burst", "empty.json"))).RootElement;
        Assert.Equal(T0.AddHours(-1), Utc(Assert.Single(stored.EnumerateArray()).GetProperty("hourUtc")));
        Assert.True(File.Exists(HeldMp3("a", T0)));
        Assert.True(File.Exists(HeldMp3("b", T0)));
        Assert.True(File.Exists(MasterMp3("a", empty)), "the empty segment's own hour is not merged, so it stays");
        Assert.Equal(0, (await Healthz(client)).GetProperty("backlogHours").GetInt32());

        // Not planned again on the next pass.
        var written = File.GetLastWriteTimeUtc(Path.Combine(MergedRoot, "burst", "empty.json"));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(MergedRoot, "burst", "empty.json")));
        Assert.Equal(0, (await Healthz(client)).GetProperty("backlogHours").GetInt32());
    }

    [Fact]
    public async Task A_merged_hour_whose_sidecar_was_never_written_is_merged_again()
    {
        Record();
        await using var factory = Host("merge", new ManualClock(Now));
        var (client, service) = await StartAsync(factory);
        var mp3 = MergedMp3(T0);
        var json = Path.ChangeExtension(mp3, ".json");
        var good = File.ReadAllBytes(mp3);

        // Killed after the audio was published but before its sidecar: the hour is not merged yet.
        File.Delete(json);
        File.WriteAllBytes(mp3, good[..(good.Length / 2)]);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(json));
        Assert.Equal(good, File.ReadAllBytes(mp3));
        Assert.Equal(T0.AddHours(1), Utc((await Healthz(client)).GetProperty("lastMergedHourUtc")));
    }

    [Fact]
    public async Task A_consolidate_setting_or_environment_variable_does_not_start_the_consolidator()
    {
        Assert.True(ConsolidatorHost.Requested(["--consolidate"]));
        Assert.False(ConsolidatorHost.Requested([]));
        Assert.False(ConsolidatorHost.Requested(["--consolidate=true"]));

        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "stations.json");
        File.WriteAllText(config, """{ "stations": [ { "id": "off", "name": "Off FM", "listenUrl": "http://127.0.0.1:9/", "enabled": false } ] }""");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);

        var before = Environment.GetEnvironmentVariable("consolidate");
        Environment.SetEnvironmentVariable("consolidate", "true");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("consolidate", "true");
                b.UseSetting("WAA_CONFIG", config);
                b.UseSetting("WAA_DATA", data);
                b.UseSetting("WAA_INSTANCE_ID", "still-a-recorder");
            });
            var client = factory.CreateClient();

            Assert.Null(factory.Services.GetService<ConsolidatorService>());
            var body = await Json(await client.GetAsync("/healthz"));
            Assert.Equal("still-a-recorder", body.GetProperty("instance").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("consolidate", before);
        }
    }
}
