using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    WebApplicationFactory<Program> Host(string mode, ManualClock clock, string analysis = "") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("consolidate", "true");
            b.UseSetting("WAA_STATION", "burst");
            b.UseSetting("WAA_SOURCES", $"a={Root("a")},b={Root("b")}");
            b.UseSetting("WAA_MERGED", MergedRoot);
            b.UseSetting("WAA_ANALYSIS", analysis);
            b.UseSetting("WAA_CONSOLIDATE_MODE", mode);
            b.ConfigureTestServices(s => s.AddSingleton<IClock>(clock));
        });

    /// <summary>Starts the host and waits for the run it does at start.</summary>
    static async Task<(HttpClient Client, ConsolidatorService Service)> StartAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<ConsolidatorService>();
        await service.FirstRun.WaitAsync(TimeSpan.FromSeconds(60));
        return (client, service);
    }

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

        // Only the 10:00 segments have merged hours on both sides (09:00 to 11:00); 09:59 needs 08:00 and 11:00 needs 12:00.
        foreach (var id in new[] { "a", "b" })
        {
            Assert.False(File.Exists(MasterMp3(id, T0)));
            Assert.True(File.Exists(HeldMp3(id, T0)));
            Assert.True(File.Exists(Holding.RecordPath(HeldMp3(id, T0))));
            Assert.True(File.Exists(MasterMp3(id, Starts[0])));
            Assert.True(File.Exists(MasterMp3(id, Starts[2])));
        }

        var health = await Healthz(client);
        Assert.Equal("hold", health.GetProperty("mode").GetString());
        Assert.Equal(T0.AddHours(1), Utc(health.GetProperty("lastMergedHourUtc")));
        Assert.Equal(2, health.GetProperty("held").GetInt32());
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
        Assert.Equal(2, ready.GetArrayLength());
        Assert.All(ready.EnumerateArray(), r => Assert.Equal("ready", r.GetProperty("status").GetString()));
        Assert.Equal(2, (await Healthz(client)).GetProperty("ready").GetInt32());

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
        Assert.Equal(1, health.GetProperty("ready").GetInt32());
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
        Assert.Equal(0, health.GetProperty("ready").GetInt32());
        Assert.Equal(aBytes + bBytes, health.GetProperty("deletedBytes").GetInt64());
    }

    [Fact]
    public async Task A_failed_hour_is_not_retried_until_a_source_changes_or_retry_is_posted()
    {
        Record();
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

        // A manager asks: the entry goes and the next run tries again (and fails again, so a new entry).
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/retry", new { hourUtc = "2026-10-15T08:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/retry", new { hourUtc = "2026-10-15T10:00:00Z" })).StatusCode);
        Assert.Equal(0, (await Healthz(client)).GetProperty("failures").GetArrayLength());
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(Now.AddHours(1), FailedUtc());
        Assert.False(File.Exists(MergedMp3(T0)));

        // The source changes: the hour is tried again, merges, and the failure is cleared.
        File.WriteAllBytes(corrupt, original);
        clock.Advance(TimeSpan.FromHours(1));
        await service.RunOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(MergedMp3(T0)));
        Assert.Equal(0, (await Healthz(client)).GetProperty("failures").GetArrayLength());
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(failures)).RootElement.EnumerateArray());
    }
}
