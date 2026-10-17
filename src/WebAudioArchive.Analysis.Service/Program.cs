using System.Globalization;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Service;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Validation;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("WAA_PORT") ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
string? Setting(string name) => builder.Configuration[name] ?? Environment.GetEnvironmentVariable(name);

AnalysisSettings settings;
LoadedConfig config;
try
{
    settings = AnalysisSettings.Load(Setting);
    config = AnalysisConfigLoader.Load(settings.ConfigPath);
}
catch (AnalysisSettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}
catch (AnalysisConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

var validate = Array.IndexOf(args, "--validate");
if (validate >= 0)
{
    // Scores owner-supplied labels against the analysed events under the output folder. Reads only; no model, scratch or recorder needed.
    if (validate + 3 >= args.Length)
    {
        Console.Error.WriteLine("usage: --validate <labels.csv> <fromUtc> <toUtc>");
        return 2;
    }

    try
    {
        var from = DateTimeOffset.Parse(args[validate + 2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTimeOffset.Parse(args[validate + 3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var labels = DetectorScore.ReadCsv(args[validate + 1]);
        var detected = AnalysisStore.DayEvents(new AnalysisPaths(settings.OutputDir, settings.Station), from, to);
        Console.Write(DetectorScore.Report(labels, detected, from, to));
        return 0;
    }
    catch (Exception ex) when (ex is FormatException or IOException)
    {
        Console.Error.WriteLine("validate error: " + ex.Message);
        return 2;
    }
}

builder.Logging.AddSimpleConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z' ";
});
using var loggers = LoggerFactory.Create(l => l.AddSimpleConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z' ";
}));

AnalysisRuntime runtime;
try
{
    runtime = AnalysisComposition.Build(settings, config, loggers, new SystemClock());
}
catch (AnalysisSettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}
catch (AnalysisConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

var benchmark = Array.IndexOf(args, "--benchmark");
if (benchmark >= 0)
{
    if (benchmark + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --benchmark <file.mp3> [seconds]");
        return 2;
    }

    var seconds = benchmark + 2 < args.Length && double.TryParse(args[benchmark + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 600;
    return await Benchmark.RunAsync(args[benchmark + 1], seconds, runtime.Decoder, runtime.Scratch, runtime.Transcriber, config.Config.Transcription, Console.Out, CancellationToken.None);
}

if (args.Contains("--rebuild"))
{
    Console.WriteLine($"rebuilt events for {runtime.Runner.RebuildAll()} segments");
    return 0;
}

if (args.Contains("--unpark"))
{
    Console.WriteLine($"un-parked {runtime.Runner.UnparkAll()} segments; the running service picks them up on its next pass");
    return 0;
}

if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://+:{settings.Port}");

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = AnalysisWorker.ShutdownTimeout);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(runtime);
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();
app.Logger.LogInformation(
    "Analysing {Station} from recorders {Recorders} (preference {Preference}) into {Out}; whisper {Model} on {Device} (DTW {Dtw}); enrichment {Enrichment}",
    settings.Station, string.Join(", ", settings.Recorders.Select(r => $"{r.Id}={r.Root}")), string.Join(",", settings.Preference), settings.OutputDir,
    settings.WhisperModelPath, settings.WhisperDevice, settings.WhisperDtw ? "on" : "off", settings.EnrichmentEnabled ? $"{settings.OllamaModel} at {settings.OllamaUrl}" : "off");

app.MapGet("/healthz", (AnalysisRuntime rt, AnalysisSettings s) =>
{
    var h = AnalysisHealth.Build(rt.Metrics.Snapshot(), s.LagAlarm, DateTimeOffset.UtcNow);
    return Results.Json(h.Body, statusCode: h.StatusCode);
});

app.Run();
return 0;
