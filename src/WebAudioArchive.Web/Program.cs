using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using PanoramicData.Blazor.Extensions;
using WebAudioArchive.Web;
using WebAudioArchive.Web.Analysis;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Components;
using WebAudioArchive.Web.Schedule;
using WebAudioArchive.Web.Status;

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

WebSettings settings;
try
{
    settings = WebSettings.Load(Setting);
}
catch (SettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}

if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://+:{Setting("WAA_PORT") ?? "8080"}");

// Keys for the circuit and antiforgery cookies live in the cache volume, so an open page survives a web restart.
// Housekeeping only touches clip files at the top of the cache, never this folder.
builder.Services.AddDataProtection()
    .SetApplicationName("WebAudioArchive.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.CacheDir, "keys")));
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SegmentCatalog>();
builder.Services.AddSingleton<ClipService>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<RecordingsQuery>();
builder.Services.AddSingleton<AnalysisReader>();
builder.Services.AddHostedService<CacheHousekeeper>();
// The schedule: page URL and zone from the stations config; fetched at start and every 15 minutes, last good kept.
var scheduleSource = ScheduleSource.Load(settings.Station, Setting("WAA_STATIONS_CONFIG"));
builder.Services.AddSingleton(scheduleSource);
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddHostedService<ScheduleRefresher>();
builder.Services.AddHttpClient(ScheduleService.HttpClientName, c => c.Timeout = ScheduleService.FetchTimeout + TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient(StatusService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddPanoramicDataBlazor();

var app = builder.Build();

app.Logger.LogInformation(
    "Serving station {Station} from recorders {Recorders}; clip cache {Cache}. No sign-in: publish this port on loopback only.",
    settings.Station, string.Join(", ", settings.Recorders.Select(r => $"{r.Id}={r.Root}")), settings.CacheDir);
if (scheduleSource.IsConfigured)
    app.Logger.LogInformation("Schedule for {Station}: {Url}, local times in {Zone}, refreshed every {Minutes} minutes", scheduleSource.Station, scheduleSource.Url, scheduleSource.Zone!.Id, ScheduleService.RefreshInterval.TotalMinutes);
else
    app.Logger.LogWarning("No schedule: {Problem}", scheduleSource.Problem);

app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/healthz", (WebSettings s) =>
{
    var recorders = s.Recorders.Select(r => new { id = r.Id, root = r.Root, rootExists = Directory.Exists(r.Root) }).ToList();
    var ok = recorders.All(r => r.rootExists);
    return Results.Json(new { status = ok ? "ok" : "data root missing", station = s.Station, recorders },
        statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/clip", async (HttpRequest req, ClipService clips) =>
{
    var q = req.Query;
    try
    {
        var request = ClipService.ParseQuery(q["from"], q["to"], q["marginBefore"], q["marginAfter"], q["recorder"]);
        var clip = await Task.Run(() => clips.Prepare(q["station"].FirstOrDefault(), request));
        return Results.Json(ClipJson.From(clip));
    }
    catch (ClipException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
    catch (ClipIntegrityException ex)
    {
        return Results.Json(new { error = "The clip could not be built as whole MP3 frames: " + ex.Message }, statusCode: 500);
    }
});

app.MapGet("/media/clip/{id}.mp3", (string id, HttpRequest req, WebSettings s) =>
{
    // Only a 32 character lower-case hex id reaches the file system, and only inside the cache directory.
    if (!ClipBuilder.IsValidId(id)) return Results.NotFound();
    var path = ClipBuilder.PathFor(s.CacheDir, id);
    if (!File.Exists(path)) return Results.NotFound();
    var download = req.Query["download"] == "1";
    var name = download ? ClipBuilder.ReadInfo(s.CacheDir, id)?.DownloadName ?? $"{s.Station}_{id}.mp3" : null;
    return Results.File(path, "audio/mpeg", name, enableRangeProcessing: true);
});

// Analysis results (plan 3), read-only, all times UTC. The player pages use them in plan 4.
// NO SIGN-IN yet: loopback only until plan 4a adds it (see AnalysisReader). Review-only swear hits are left out unless includeReview=true.
if (settings.AnalysisDir is not null)
    app.Logger.LogWarning("Analysis API enabled from {Dir} (read-only). It has no sign-in: publish this port on loopback only until plan 4a.", settings.AnalysisDir);
app.MapGet("/api/analysis/events", (HttpRequest req, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (from, to) = AnalysisReader.Range(req.Query["from"], req.Query["to"], AnalysisReader.MaxEventsRange);
    var includeReview = string.Equals(req.Query["includeReview"], "true", StringComparison.OrdinalIgnoreCase);
    var events = a.Events(from, to, req.Query["types"], includeReview);
    return new { from, to, includeReview, count = events.Count, events = events.Select(AnalysisReader.EventView).ToList() };
}));
app.MapGet("/api/analysis/transcript", (HttpRequest req, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (from, to) = AnalysisReader.Range(req.Query["from"], req.Query["to"], AnalysisReader.MaxTranscriptRange);
    var lines = a.Transcript(from, to);
    return new { from, to, count = lines.Count, lines };
}));
app.MapGet("/api/analysis/shows/{id}", (string id, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (analysis, enrichment) = a.Show(id);
    return new { analysis, enrichment };
}));

// A UTC time (an analysis event, a recording) opens the show on air then, or that off-air stretch, at that moment.
app.MapGet("/at", async (HttpRequest req, ScheduleService schedule) =>
{
    var utc = (string?)req.Query["utc"];
    if (utc is null || !utc.EndsWith('Z')
        || !DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
        || at.Year is < AnalysisReader.MinYear or > AnalysisReader.MaxYear)
        return Results.BadRequest(new { error = "utc must be a UTC time ending in Z, such as 2026-10-15T19:00:00Z." });
    await schedule.WaitForScheduleAsync(TimeSpan.FromSeconds(10));
    if (schedule.At(at) is not { } show)
        return Results.Problem("There is no schedule yet, so the show at that time cannot be found. See the Shows page.", statusCode: 503);
    var recorder = (string?)req.Query["recorder"];
    var query = "?at=" + PlayLink.Iso(at) + (string.IsNullOrWhiteSpace(recorder) ? "" : "&recorder=" + Uri.EscapeDataString(recorder.Trim().ToLowerInvariant()));
    return Results.Redirect("/shows/" + show.Id + query);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
return 0;
