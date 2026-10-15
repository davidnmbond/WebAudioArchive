using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web;

/// <summary>A public type in this assembly, for WebApplicationFactory in the tests.</summary>
public sealed class WebEntryPoint;

/// <summary>Cache housekeeping at start-up and then once a day.</summary>
public sealed class CacheHousekeeper(WebSettings settings, TimeProvider time, ILogger<CacheHousekeeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                ClipCache.Housekeep(settings.CacheDir, time.GetUtcNow(), settings.CacheMaxAge, settings.CacheMaxBytes, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError(ex, "Cache housekeeping failed in {Cache}", settings.CacheDir);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>The JSON returned by GET /api/clip. All times are UTC.</summary>
public static class ClipJson
{
    public static object From(PreparedClip c) => new
    {
        id = c.File.Id,
        url = c.Url,
        downloadUrl = c.DownloadUrl,
        downloadName = c.File.DownloadName,
        station = c.Station,
        recorder = c.Plan.Recorder,
        requestedFrom = c.Plan.RequestedFrom.UtcDateTime,
        requestedTo = c.Plan.RequestedTo.UtcDateTime,
        windowFrom = c.Plan.WindowFrom.UtcDateTime,
        windowTo = c.Plan.WindowTo.UtcDateTime,
        from = c.Plan.StartUtc.UtcDateTime,
        to = c.Plan.EndUtc.UtcDateTime,
        seconds = Math.Round(c.File.Seconds, 3),
        bytes = c.File.Bytes,
        reused = c.File.Reused,
        warnings = c.Plan.Warnings,
        notes = c.Plan.Notes,
        coverageSeconds = c.Plan.CoverageSeconds.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
        pieces = c.Plan.Pieces.Select(p => new
        {
            segmentStartUtc = p.Segment.StartUtc.UtcDateTime,
            open = p.Segment.IsOpen,
            startByte = p.StartByte,
            endByte = p.EndByte,
            fromUtc = p.StartUtc.UtcDateTime,
            toUtc = p.EndUtc.UtcDateTime,
        }),
    };
}
