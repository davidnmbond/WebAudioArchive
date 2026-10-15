using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Worker;

static string Setting(IConfiguration c, string name, string fallback) => c[name] ?? Environment.GetEnvironmentVariable(name) ?? fallback;

if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("WAA_PORT") ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var configPath = Setting(builder.Configuration, "WAA_CONFIG", "/config/stations.json");
var dataRoot = Setting(builder.Configuration, "WAA_DATA", "/data");
var instanceId = Setting(builder.Configuration, "WAA_INSTANCE_ID", Environment.MachineName);

IReadOnlyList<StationConfig> stations;
try
{
    stations = StationConfigLoader.Load(configPath);
}
catch (ConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://+:{Setting(builder.Configuration, "WAA_PORT", "8080")}");

var clock = new SystemClock();
var runtimes = stations.Select(s => new StationRuntime(
    s,
    new StationHealth(),
    new MasterRecorder(dataRoot, s.Id, instanceId, clock, LoggerFactory.Create(l => l.AddConsole()).CreateLogger<MasterRecorder>()))).ToList();

builder.Services.AddSingleton<IClock>(clock);
builder.Services.AddSingleton(new StationRegistry(instanceId, runtimes));
builder.Services.AddHostedService<StationRecorderService>();

var app = builder.Build();

app.MapGet("/healthz", (StationRegistry registry) =>
{
    var now = DateTimeOffset.UtcNow;
    var uptime = now - registry.StartedUtc;
    var unhealthy = false;
    var stationReports = registry.Stations.Select(s =>
    {
        var last = s.Health.LastByteUtc;
        var since = last is null ? (double?)null : Math.Round((now - last.Value).TotalSeconds, 1);
        var silent = s.Config.Enabled && uptime > TimeSpan.FromSeconds(180) && (since is null || since > 120);
        if (silent) unhealthy = true;
        return new
        {
            id = s.Config.Id,
            enabled = s.Config.Enabled,
            connected = s.Health.Connected,
            lastByteUtc = last,
            secondsSinceLastByte = since,
            reconnects = s.Health.Reconnects,
            currentSegmentStartUtc = s.Recorder.CurrentSegmentStartUtc,
            coverage24h = CoverageReader.Read(s.Recorder.MasterDir, now, TimeSpan.FromHours(24)).Fraction,
            coverage30d = CoverageReader.Read(s.Recorder.MasterDir, now, TimeSpan.FromDays(30)).Fraction,
        };
    }).ToList();

    var body = new { instance = registry.InstanceId, uptimeSeconds = Math.Round(uptime.TotalSeconds), stations = stationReports };
    return Results.Json(body, statusCode: unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
});

app.Run();
return 0;

public partial class Program;
