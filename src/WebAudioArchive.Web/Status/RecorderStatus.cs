using System.Text.Json;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Status;

public enum HealthLevel
{
    Green,
    Amber,
    Red,
}

/// <summary>What a recorder's /healthz said about the configured station, or why it could not be read.</summary>
public sealed record RecorderHealth(
    bool Reachable,
    string? Error,
    string? Instance,
    double? UptimeSeconds,
    bool Connected,
    double? SecondsSinceLastFrame,
    string? LastError,
    DateTimeOffset? LastErrorUtc,
    DateTimeOffset? CurrentSegmentStartUtc,
    int Reconnects)
{
    public static RecorderHealth Unreachable(string error) => new(false, error, null, null, false, null, null, null, null, 0);
}

public sealed record RecorderStatus(
    RecorderSettings Recorder,
    RecorderHealth? Health,
    HealthLevel Level,
    string Summary,
    double Coverage24hSeconds,
    int Segments24h,
    CatalogSegment? Latest)
{
    public double Coverage24hFraction => Math.Min(1, Coverage24hSeconds / TimeSpan.FromHours(24).TotalSeconds);
}

public static class StatusRules
{
    /// <summary>Frames written within this many seconds is green.</summary>
    public const double GreenSeconds = 10;

    /// <summary>The recorder's own unhealthy threshold (HealthRules.MaxSilence); beyond it is red.</summary>
    public const double RedSeconds = 120;

    public static (HealthLevel Level, string Summary) Classify(RecorderHealth? h)
    {
        if (h is null) return (HealthLevel.Amber, "No health URL is configured for this recorder (WAA_RECORDER_HEALTH).");
        if (!h.Reachable) return (HealthLevel.Red, $"Health check unreachable: {h.Error}");
        if (h.SecondsSinceLastFrame is null) return (HealthLevel.Red, "No frame has been written yet.");

        var s = h.SecondsSinceLastFrame.Value;
        if (s > RedSeconds) return (HealthLevel.Red, $"No frame written for {s:0} s.");
        if (h.LastError is not null) return (HealthLevel.Amber, $"Recording, but the last write failed: {h.LastError}");
        if (!h.Connected) return (HealthLevel.Amber, $"Reconnecting; last frame written {s:0.0} s ago.");
        if (s > GreenSeconds) return (HealthLevel.Amber, $"Connected, but the last frame was written {s:0.0} s ago.");
        return (HealthLevel.Green, $"Recording; last frame written {s:0.0} s ago.");
    }

    /// <summary>Reads the station's entry from a recorder /healthz body (200 or 503 both carry it).</summary>
    public static RecorderHealth Parse(string json, string station)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var instance = root.TryGetProperty("instance", out var i) ? i.GetString() : null;
            double? uptime = root.TryGetProperty("uptimeSeconds", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
            if (!root.TryGetProperty("stations", out var stations) || stations.ValueKind != JsonValueKind.Array)
                return RecorderHealth.Unreachable("the response has no stations list");

            foreach (var s in stations.EnumerateArray())
            {
                if (s.TryGetProperty("id", out var id) && id.GetString() == station)
                {
                    return new RecorderHealth(
                        true, null, instance, uptime,
                        s.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                        Number(s, "secondsSinceLastFrameWritten"),
                        s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                        Time(s, "lastErrorUtc"),
                        Time(s, "currentSegmentStartUtc"),
                        s.TryGetProperty("reconnects", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0);
                }
            }

            return RecorderHealth.Unreachable($"the recorder does not report station '{station}'");
        }
        catch (JsonException ex)
        {
            return RecorderHealth.Unreachable("the response is not JSON: " + ex.Message);
        }
    }

    static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // The recorder may write times with a local offset; everything shown here is converted to UTC.
    static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t) ? t.ToUniversalTime() : null;
}

/// <summary>Builds the Status page: each recorder's health (fetched server side) and its last 24 hours of coverage.</summary>
public sealed class StatusService(WebSettings settings, SegmentCatalog catalog, IHttpClientFactory http, TimeProvider time)
{
    public const string HttpClientName = "recorder-health";

    public async Task<IReadOnlyList<RecorderStatus>> GetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tasks = settings.Recorders.Select(async r =>
        {
            var health = r.HealthUrl is null ? null : await FetchAsync(r.HealthUrl, ct);
            var (level, summary) = StatusRules.Classify(health);
            var segments = catalog.Segments(r.Id);
            var from = now.AddHours(-24);
            var recent = segments.Where(s => s.EndUtc > from).ToList();
            return new RecorderStatus(r, health, level, summary, WindowCoverage.Seconds(recent, from, now), recent.Count, segments.LastOrDefault());
        });
        return await Task.WhenAll(tasks);
    }

    async Task<RecorderHealth> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return StatusRules.Parse(body, settings.Station);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return RecorderHealth.Unreachable($"{url} did not answer ({ex.Message})");
        }
    }
}
