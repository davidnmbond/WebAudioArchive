using PanoramicData.Blazor.Extensions;
using WebAudioArchive.Web;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Components;
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

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SegmentCatalog>();
builder.Services.AddSingleton<ClipService>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<RecordingsQuery>();
builder.Services.AddHostedService<CacheHousekeeper>();
builder.Services.AddHttpClient(StatusService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddPanoramicDataBlazor();

var app = builder.Build();

app.Logger.LogInformation(
    "Serving station {Station} from recorders {Recorders}; clip cache {Cache}. No sign-in: publish this port on loopback only.",
    settings.Station, string.Join(", ", settings.Recorders.Select(r => $"{r.Id}={r.Root}")), settings.CacheDir);

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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
return 0;
