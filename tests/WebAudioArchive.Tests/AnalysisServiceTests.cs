extern alias analysissvc;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using analysissvc::WebAudioArchive.Analysis.Service;

namespace WebAudioArchive.Tests;

public sealed partial class AnalysisServiceTests : IAsyncLifetime
{
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json");
    readonly string _root = Directory.CreateTempSubdirectory("waa-svc-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));
    readonly FakeTranscriber _whisper = new();
    readonly FakeLlm _llm = new();
    long _free = long.MaxValue;
    bool _diskBroken;

    string A => Path.Combine(_root, "a");
    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    public async Task InitializeAsync()
    {
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", Show, Signals.Concat(Signals.SpeechLike(30, 4), Signals.Sine(5)));
        File.WriteAllText(Path.Combine(_root, "schedule.json"), """
            { "station": "burst", "url": null, "timezone": "Europe/London", "fetchedUtc": "2026-10-15T12:00:00+00:00",
              "slots": [ { "day": "Thursday", "localStart": "20:00", "minutes": 60, "title": "Backseat Aux" } ] }
            """);
        _whisper.Respond = (_, _) => [AnalysisFixtures.Said(1, "Um, hello, I'm Amy on Burst.")];
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    (AnalysisRunner Runner, AnalysisMetrics Metrics) Runner(ITranscriber? transcriber = null)
    {
        var whisper = transcriber ?? _whisper;
        var config = AnalysisConfigLoader.Load(ConfigPath);
        var scratch = new ScratchSpace(Path.Combine(_root, "scratch"), 512L * 1024 * 1024);
        var disk = new DiskGuard(Paths.Root, 1L << 30, _ => _diskBroken ? throw new IOException("disk gone") : _free);
        var schedule = new ScheduleProvider(Path.Combine(_root, "schedule.json"), null, "burst", NullLogger.Instance);
        var pipeline = new SegmentPipeline(Paths, config, scratch, new SegmentDecoder(), whisper, disk,
            Watershed.From(config.Config.Swearing, schedule.Zone), schedule.TitleAt, _clock, NullLogger.Instance);
        var shows = new ShowProcessor(Paths, schedule, ["a"], AnalysisComposition.ShowMargin, _clock, NullLogger.Instance);
        var enricher = new Enricher(Paths, _llm, EnrichmentPrompt.Load(config.ConfigDir, config.Config.Enrichment), config.Config.Enrichment,
            "qwen3.6:latest", ["a"], _clock, NullLogger.Instance);
        var metrics = new AnalysisMetrics(_clock.UtcNow);
        var runner = new AnalysisRunner(new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance), pipeline, shows, enricher,
            whisper, disk, scratch, metrics, ["a"], null, _clock, NullLogger.Instance);
        return (runner, metrics);
    }

    [Fact]
    public async Task One_pass_analyses_the_segment_maps_the_show_and_enriches_it()
    {
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(1, m.JobsDone);
        Assert.Equal(0, m.Backlog);
        Assert.Null(m.OldestPendingEndUtc);
        Assert.NotNull(m.LastRealtimeFactor);
        Assert.Null(m.Paused);
        Assert.True(m.WhisperReady);
        Assert.Equal(1, m.EnrichmentDone);
        Assert.NotNull(m.LastSuccessUtc);
        Assert.Equal(1, m.Classes!["a"].Done);
        Assert.Null(m.Classes["a"].LastError);
        Assert.Equal(512L * 1024 * 1024, m.ScratchMaxBytes);
        var showDir = Paths.ShowDir("burst-20261015T1900Z", Show);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(Path.Combine(showDir, AnalysisPaths.ShowAnalysisFile))!.Complete);
        Assert.Equal(EnrichmentStatus.Done, AnalysisJson.Read<ShowEnrichment>(Path.Combine(showDir, AnalysisPaths.EnrichmentFile))!.Status);
        Assert.Single(_llm.Requests);

        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(1, _whisper.Calls);
        Assert.Single(_llm.Requests);
    }

    [Fact]
    public async Task Low_disk_stops_analysis_before_any_work()
    {
        _free = 1;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(0, _whisper.Calls);
        Assert.Contains("free disk", metrics.Snapshot().Paused);
        Assert.NotNull(metrics.Snapshot().LastPassUtc);
    }

    [Fact]
    public async Task Whisper_not_ready_leaves_a_visible_backlog_and_lag()
    {
        _whisper.Ready = false;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(0, _whisper.Calls);
        Assert.Equal(1, m.Backlog);
        Assert.Contains("whisper", m.Paused);
        Assert.Equal(Show.AddSeconds(35), m.OldestPendingEndUtc);
        var health = AnalysisHealth.Build(m, TimeSpan.FromHours(6), _clock.UtcNow);
        Assert.Equal(200, health.StatusCode);
        Assert.Equal(503, AnalysisHealth.Build(m, TimeSpan.FromHours(1), _clock.UtcNow).StatusCode);
        Assert.Empty(_llm.Requests);
    }

    [Fact]
    public async Task Whisper_readiness_is_checked_before_anything_is_planned()
    {
        var order = new OrderTranscriber();
        var (runner, _) = Runner(order);

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal("ready", order.Log[0]);
        Assert.Contains("transcribe", order.Log);
    }

    [Fact]
    public async Task One_job_that_throws_does_not_stop_the_others_and_is_reported_per_class()
    {
        // The newest segment cannot write its state (a folder is in the way): ProcessAsync itself throws. The older one must still be analysed.
        var older = Show.AddHours(-1);
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", older, Signals.SpeechLike(10, 5));
        Directory.CreateDirectory(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.StateFile));
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(1, m.JobsDone);
        Assert.Equal(1, m.JobsFailed);
        Assert.NotNull(m.LastPassUtc);
        var status = m.Classes!["a"];
        Assert.Equal(1, status.Done);
        Assert.Equal(1, status.Failed);
        Assert.Equal("a/2026/10-15/190000", status.LastErrorJob);
        Assert.False(string.IsNullOrEmpty(status.LastError));
        Assert.NotNull(status.LastSuccessUtc);
        Assert.True(File.Exists(Path.Combine(Paths.SegmentDir("a/2026/10-15/180000"), AnalysisPaths.EventsFile)));
        var json = JsonSerializer.Serialize(AnalysisHealth.Build(m, TimeSpan.FromHours(6), _clock.UtcNow).Body);
        Assert.Contains("\"lastErrorBySegmentClass\"", json);
        Assert.Contains("a/2026/10-15/190000", json);
    }

    [Fact]
    public async Task A_graceful_stop_is_not_a_failure_and_does_not_burn_an_attempt()
    {
        using var stop = new CancellationTokenSource();
        _whisper.Respond = (_, _) =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        };
        var (runner, metrics) = Runner();

        await runner.PassAsync(stop.Token);

        var m = metrics.Snapshot();
        Assert.Equal(0, m.JobsFailed);
        Assert.Equal(0, m.JobsDone);
        Assert.Null(m.CurrentJob);
        var state = JobStore.Read(Paths.SegmentDir("a/2026/10-15/190000"))!;
        Assert.Equal(JobStatus.Waiting, state.Status);
        Assert.Equal(0, state.Attempts);

        _whisper.Respond = (_, _) => [AnalysisFixtures.Said(1, "Hello again.")];
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(1, metrics.Snapshot().JobsDone);
    }

    [Fact]
    public async Task A_job_killed_again_and_again_is_parked_by_the_runner_and_stops_blocking()
    {
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var segment = new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan().Single();
        var (runner, metrics) = Runner();
        var version = $"{_whisper.Identity}|{AnalysisConfigLoader.Load(ConfigPath).Config.Transcription.TranscriptKey()}";
        JobStore.Write(dir, new JobState(JobState.CurrentSchema, segment.JobId, segment.Fingerprint, version, AnalysisConfigLoader.Load(ConfigPath).DetectorVersion,
            JobStatus.Running, JobRules.MaxAttempts, null, null, [], 0, _clock.UtcNow, 35, 0, 0));

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Failed, JobStore.Read(dir)!.Status);
        Assert.Equal(0, _whisper.Calls);
        var m = metrics.Snapshot();
        Assert.Equal(1, m.FailedSegments);
        Assert.Equal(0, m.Backlog);
        Assert.Null(m.OldestPendingEndUtc);
    }

    [Fact]
    public async Task Unpark_puts_a_parked_segment_back_and_it_is_then_analysed()
    {
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var segment = new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan().Single();
        var (runner, metrics) = Runner();
        var config = AnalysisConfigLoader.Load(ConfigPath);
        JobStore.Write(dir, new JobState(JobState.CurrentSchema, segment.JobId, segment.Fingerprint, $"{_whisper.Identity}|{config.Config.Transcription.TranscriptKey()}",
            config.DetectorVersion, JobStatus.Running, JobRules.MaxAttempts, null, null, [], 0, _clock.UtcNow, 35, 0, 0));
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Failed, JobStore.Read(dir)!.Status);
        Assert.Equal(0, _whisper.Calls);

        Assert.Equal(1, runner.UnparkAll());

        var state = JobStore.Read(dir)!;
        Assert.Equal(0, state.Attempts);
        Assert.Contains("un-parked", state.LastError);
        Assert.Equal(0, runner.UnparkAll());
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Done, JobStore.Read(dir)!.Status);
        Assert.Equal(1, metrics.Snapshot().JobsDone);
    }

    [Fact]
    public async Task A_sidecar_that_cannot_be_planned_is_reported_and_does_not_break_the_pass()
    {
        // A file name that is not a plain job id part: the plan must skip it, not throw for every pass.
        var odd = Path.Combine(A, "burst", "master", "2026", "10-15", "19 00 00.mp3");
        File.Copy(Path.Combine(A, "burst", "master", "2026", "10-15", "190000.mp3"), odd);
        File.Copy(Path.Combine(A, "burst", "master", "2026", "10-15", "190000.json"), Path.ChangeExtension(odd, ".json"));
        var meta = SegmentMetaStore.Read(Path.ChangeExtension(odd, ".json"))!;
        SegmentMetaStore.Write(Path.ChangeExtension(odd, ".json"), meta with { File = "19 00 00.mp3" });
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(1, m.JobsDone);
        Assert.Equal(1, m.FailedSegments);
    }

    [Fact]
    public async Task Rebuild_redoes_events_without_whisper()
    {
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        File.Delete(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.EventsFile));

        Assert.Equal(1, runner.RebuildAll());

        Assert.True(File.Exists(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.EventsFile)));
        Assert.Equal(1, _whisper.Calls);
    }

    public static TheoryData<string, int, int, int, string?> HealthCases => new()
    {
        // name, minutes since last pass, hours of lag, expected code, paused reason
        { "ok", 1, 0, 200, null },
        { "paused", 1, 0, 200, "free disk below 20 GB" },
        { "lagging", 1, 7, 503, null },
        { "stalled", 20, 0, 503, null },
    };

    [Theory]
    [MemberData(nameof(HealthCases))]
    public void Health_reports_lag_stalls_and_pauses(string expected, int minutesSincePass, int lagHours, int code, string? paused)
    {
        var now = Show.AddHours(10);
        var m = new MetricsSnapshot(now.AddHours(-2), now.AddMinutes(-minutesSincePass), lagHours > 0 ? 3 : 0, 0, lagHours > 0 ? now.AddHours(-lagHours) : null,
            10, 0, 0.05, 0.06, 23.5, null, paused, true, 0, 4, 0, 50L << 30, 0);

        var h = AnalysisHealth.Build(m, TimeSpan.FromHours(6), now);
        var json = JsonSerializer.Serialize(h.Body);

        Assert.Equal(code, h.StatusCode);
        Assert.Contains($"\"status\":\"{expected}\"", json);
        Assert.Contains("\"lagSeconds\":" + (lagHours * 3600), json);
        Assert.Contains("\"audioHoursAnalysedLast24h\":23.5", json);
    }

    [Fact]
    public void Health_reports_queue_device_scratch_and_last_success()
    {
        var now = Show.AddHours(10);
        var classes = new Dictionary<string, SegmentClassStatus>
        {
            ["a"] = new(5, 1, now.AddMinutes(-3), now.AddHours(-1), "decode failed", "a/2026/10-15/190000"),
        };
        var m = new MetricsSnapshot(now.AddHours(-2), now.AddMinutes(-1), 3, 1, now.AddHours(-1), 10, 1, 0.05, 0.06, 2, null, null, true, 0, 4, 0, 50L << 30, 1234,
            Device: "cuda", Classes: classes, LastSuccessUtc: now.AddMinutes(-3), ScratchMaxBytes: 2L << 30, MinFreeBytes: 20L << 30);

        var json = JsonSerializer.Serialize(AnalysisHealth.Build(m, TimeSpan.FromHours(6), now).Body);

        Assert.Contains("\"queueSegments\":3", json);
        Assert.Contains("\"device\":\"cuda\"", json);
        Assert.Contains("\"scratchBytes\":1234", json);
        Assert.Contains("\"scratchMaxBytes\":" + (2L << 30), json);
        Assert.Contains("\"freeDiskBytes\":" + (50L << 30), json);
        Assert.Contains("\"minFreeDiskBytes\":" + (20L << 30), json);
        Assert.Contains("\"lastSuccessUtc\":", json);
        Assert.Contains("\"decode failed\"", json);
    }

    [Fact]
    public void A_long_running_job_is_not_a_stall_until_it_has_run_for_hours()
    {
        var now = Show.AddHours(10);
        MetricsSnapshot Running(TimeSpan runningFor) => new(now.AddHours(-5), now.AddMinutes(-40), 1, 0, now.AddHours(-1), 0, 0, null, null, 0, "a/2026/10-15/190000", null, true, 0, 0, 0, 50L << 30, 0,
            LastActivityUtc: now - runningFor);

        Assert.Equal(200, AnalysisHealth.Build(Running(TimeSpan.FromMinutes(40)), TimeSpan.FromHours(6), now).StatusCode);
        Assert.Equal(503, AnalysisHealth.Build(Running(AnalysisHealth.JobStallAfter + TimeSpan.FromMinutes(1)), TimeSpan.FromHours(6), now).StatusCode);
    }

    [Fact]
    public async Task The_benchmark_prints_the_realtime_factor()
    {
        var mp3 = Path.Combine(_root, "bench.mp3");
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(Signals.SpeechLike(40, 9)), 2);
        var output = new StringWriter();

        var code = await Benchmark.RunAsync(mp3, 35, new SegmentDecoder(), new ScratchSpace(Path.Combine(_root, "bscratch"), 512L << 20),
            _whisper, new TranscriptionOptions(), output, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("35.0 s of audio", output.ToString());
        Assert.Contains("realtime factor", output.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "bscratch")));
    }

    [Fact]
    public async Task The_benchmark_refuses_open_recordings_and_a_transcriber_that_is_not_ready()
    {
        var output = new StringWriter();
        var scratch = new ScratchSpace(Path.Combine(_root, "bscratch"), 512L << 20);
        var part = Path.Combine(_root, "open.mp3.part");
        File.WriteAllBytes(part, [1, 2, 3]);

        Assert.Equal(2, await Benchmark.RunAsync(part, 35, new SegmentDecoder(), scratch, _whisper, new TranscriptionOptions(), output, CancellationToken.None));
        Assert.Contains(".part", output.ToString());

        var mp3 = Path.Combine(_root, "bench.mp3");
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(Signals.SpeechLike(5, 9)), 2);
        _whisper.Ready = false;
        Assert.Equal(1, await Benchmark.RunAsync(mp3, 5, new SegmentDecoder(), scratch, _whisper, new TranscriptionOptions(), output, CancellationToken.None));
        Assert.Contains("not ready", output.ToString());
        Assert.Equal(0, _whisper.Calls);
    }

    // ---- composition and the Ollama client's transport ----

    Func<string, string?> Env(params (string Name, string Value)[] extra)
    {
        var d = new Dictionary<string, string>
        {
            ["WAA_RECORDERS"] = $"a={A}",
            ["WAA_ANALYSIS_OUT"] = Path.Combine(_root, "comp-out"),
            ["WAA_SCRATCH"] = Path.Combine(_root, "comp-scratch"),
            ["WAA_ANALYSIS_CONFIG"] = ConfigPath,
        };
        foreach (var (n, v) in extra) d[n] = v;
        return n => d.GetValueOrDefault(n);
    }

    [Fact]
    public void Composition_refuses_a_scratch_root_that_holds_recordings_or_output()
    {
        foreach (var scratch in new[] { A, _root, Path.Combine(_root, "comp-out"), Path.Combine(A, "scratch"), Path.Combine(_root, "comp-out", "scratch") })
        {
            var settings = AnalysisSettings.Load(Env(("WAA_SCRATCH", scratch)));
            var ex = Assert.Throws<AnalysisSettingsException>(() =>
                AnalysisComposition.Build(settings, AnalysisConfigLoader.Load(ConfigPath), NullLoggerFactory.Instance, _clock));
            Assert.Contains("WAA_SCRATCH", ex.Message);
        }

        Assert.True(Directory.Exists(A));
    }

    [Fact]
    public void Composition_refuses_an_output_folder_inside_a_recorders_data()
    {
        var settings = AnalysisSettings.Load(Env(("WAA_ANALYSIS_OUT", Path.Combine(A, "analysis"))));
        var ex = Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisComposition.Build(settings, AnalysisConfigLoader.Load(ConfigPath), NullLoggerFactory.Instance, _clock));
        Assert.Contains("WAA_ANALYSIS_OUT", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(A, "analysis")));
    }

    [Fact]
    public void The_ollama_timeout_defaults_to_twenty_minutes_and_is_at_least_ten()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), AnalysisSettings.Load(Env()).OllamaTimeout);
        Assert.Equal(TimeSpan.FromMinutes(45), AnalysisSettings.Load(Env(("WAA_OLLAMA_TIMEOUT_MINUTES", "45"))).OllamaTimeout);
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_OLLAMA_TIMEOUT_MINUTES", "2"))));
        Assert.Contains("WAA_OLLAMA_TIMEOUT_MINUTES", ex.Message);
        Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_OLLAMA_TIMEOUT_MINUTES", "soon"))));
    }

    [Fact]
    public void The_ollama_http_client_has_the_configured_timeout_not_the_default_100_seconds()
    {
        using var http = OllamaClient.CreateHttpClient(TimeSpan.FromMinutes(33));
        Assert.Equal(TimeSpan.FromMinutes(33), http.Timeout);
        Assert.True(http.Timeout >= OllamaClient.MinTimeout);

        Assert.Throws<ArgumentOutOfRangeException>(() => OllamaClient.CreateHttpClient(TimeSpan.FromSeconds(100)));

        var settings = AnalysisSettings.Load(Env(("WAA_ENRICHMENT", "on"), ("WAA_OLLAMA_TIMEOUT_MINUTES", "30")));
        Assert.Equal(TimeSpan.FromMinutes(30), settings.OllamaTimeout);
    }

    [Fact]
    public async Task The_ollama_client_never_follows_a_redirect_so_the_transcript_stays_on_the_configured_host()
    {
        var elsewhere = 0;
        await using var target = await StartAsync(app => app.MapPost("/api/chat", () =>
        {
            Interlocked.Increment(ref elsewhere);
            return Results.Json(new { message = new { content = "{}" } });
        }));
        foreach (var status in new[] { 307, 308 })
        {
            await using var origin = await StartAsync(app => app.MapPost("/api/chat", (HttpContext c) =>
            {
                c.Response.StatusCode = status;
                c.Response.Headers.Location = target.Url + "api/chat";
                return Task.CompletedTask;
            }));
            using var http = OllamaClient.CreateHttpClient(TimeSpan.FromMinutes(10));
            var client = new OllamaClient(http, new Uri(origin.Url));

            await Assert.ThrowsAsync<EnrichmentInvalidException>(() => client.ChatAsync(
                new LlmRequest("m", "system", "the whole transcript", JsonDocument.Parse("{}").RootElement, 0.1, 1, 1024, false, 100), CancellationToken.None));
        }

        Assert.Equal(0, elsewhere);
    }

    sealed record Started(WebApplication App, string Url) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    static async Task<Started> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Started(app, url.TrimEnd('/') + "/");
    }

    sealed class OrderTranscriber : ITranscriber
    {
        public List<string> Log { get; } = [];

        public string Engine => "order";

        public string Identity
        {
            get
            {
                Log.Add("identity");
                return "order|m";
            }
        }

        public string Model => "m";

        public Task<bool> IsReadyAsync(CancellationToken ct)
        {
            Log.Add("ready");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
        {
            Log.Add("transcribe");
            return Task.FromResult<IReadOnlyList<RawSegment>>([AnalysisFixtures.Said(1, "Hello there.")]);
        }
    }

    // ---- the host ----

    WebApplicationFactory<AnalysisEntryPoint> Host(params (string Name, string Value)[] extra) => new WebApplicationFactory<AnalysisEntryPoint>().WithWebHostBuilder(b =>
    {
        b.UseSetting("WAA_RECORDERS", $"a={A}");
        b.UseSetting("WAA_ANALYSIS_OUT", Path.Combine(_root, "host-out"));
        b.UseSetting("WAA_SCRATCH", Path.Combine(_root, "host-scratch"));
        b.UseSetting("WAA_ANALYSIS_CONFIG", ConfigPath);
        b.UseSetting("WAA_WHISPER_MODEL_PATH", Path.Combine(_root, "no-model.bin"));
        b.UseSetting("WAA_ENRICHMENT", "off");
        b.UseSetting("WAA_MIN_FREE_GB", "0");
        // Nothing is planned (the test segment is older), so the lag, and with it the status code, does not depend on today's date.
        b.UseSetting("WAA_ANALYSIS_SINCE", "2100-01-01T00:00:00Z");
        foreach (var (name, value) in extra) b.UseSetting(name, value);
    });

    [Fact]
    public async Task The_host_serves_healthz()
    {
        await using var factory = Host();
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\"", body);
        Assert.Contains("\"lagSeconds\"", body);
        Assert.Contains("\"queueSegments\"", body);
        Assert.Contains("\"device\"", body);
        Assert.Contains("\"scratchBytes\"", body);
    }

    [Fact]
    public void The_host_refuses_to_start_with_bad_settings()
    {
        using var factory = Host(("WAA_OLLAMA_MODEL", "kimi-k2.6:cloud"), ("WAA_ENRICHMENT", "on"));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_refuses_to_start_when_scratch_would_swallow_the_recordings()
    {
        using var factory = Host(("WAA_SCRATCH", A));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.True(File.Exists(Path.Combine(A, "burst", "master", "2026", "10-15", "190000.mp3")));
    }
}
