using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Worker.Consolidation;

/// <summary>
/// The recorder image started with --consolidate (issue #104): no recorder and no stream connection, only the hourly
/// consolidation and its endpoints, /healthz, /held, POST /deletions and POST /retry.
/// </summary>
public static class ConsolidatorHost
{
    /// <summary>
    /// True only for the --consolidate command-line argument. A setting or environment variable never turns a recorder
    /// into the consolidator, because that would silently stop it recording.
    /// </summary>
    public static bool Requested(string[] args) => args.Contains("--consolidate", StringComparer.Ordinal);

    public static async Task<int> RunAsync(WebApplicationBuilder builder)
    {
        var (app, exitCode) = Build(builder);
        if (app is null) return exitCode;
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// The consolidator app, or null with the exit code when its settings or folders are wrong (the reasons are written
    /// to standard error). An <see cref="IClock"/> already registered is kept.
    /// </summary>
    public static (WebApplication? App, int ExitCode) Build(WebApplicationBuilder builder)
    {
        string? Get(string name) => builder.Configuration[name] ?? Environment.GetEnvironmentVariable(name);

        var (settings, errors) = ConsolidatorSettings.Read(Get);
        if (settings is null)
        {
            foreach (var e in errors) Console.Error.WriteLine("config error: " + e);
            return (null, 2);
        }

        var missing = settings.Sources.Select(s => s.Root).Append(settings.MergedRoot).Where(d => !Directory.Exists(d)).ToList();
        if (missing.Count > 0)
        {
            foreach (var d in missing) Console.Error.WriteLine($"data error: {d} does not exist");
            return (null, 3);
        }

        if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
            builder.WebHost.UseUrls($"http://+:{settings.Port}");

        builder.Services.AddSingleton(settings);
        builder.Services.TryAddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ConsolidatorService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsolidatorService>());

        var app = builder.Build();

        app.MapGet("/healthz", (ConsolidatorService service) => Results.Json(service.Health()));

        app.MapGet("/held", (ConsolidatorService service) =>
            Results.Json(service.HeldRecords().Where(r => r.Status == Holding.Ready).OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Recorder, StringComparer.Ordinal).ToList()));

        app.MapPost("/deletions", async (DeletionRequest request, ConsolidatorService service, CancellationToken ct) =>
            Results.Json(await service.DeleteAsync(request, ct)));

        app.MapPost("/retry", (RetryRequest request, ConsolidatorService service) =>
            request.HourUtc is not { } hour ? Results.BadRequest(new { error = "hourUtc is required" })
            : service.Retry(hour) ? Results.Ok(new { hourUtc = hour.ToUniversalTime(), retry = true })
            : Results.NotFound(new { hourUtc = hour.ToUniversalTime(), error = "no failure is recorded for that hour" }));

        return (app, 0);
    }
}
