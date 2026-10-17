using System.Globalization;
using System.Text.Json;
using WebAudioArchive.Core.Config;

namespace WebAudioArchive.Web.Schedule;

/// <summary>
/// Where the station's schedule comes from: its page URL and the zone its local times are in, both read from the
/// stations config (WAA_STATIONS_CONFIG), or the reason there is no schedule.
/// </summary>
public sealed record ScheduleSource(string Station, Uri? Url, TimeZoneInfo? Zone, string? Problem)
{
    public bool IsConfigured => Url is not null && Zone is not null;

    /// <summary>Reads the station's scheduleUrl and timezone. Never throws: a problem is kept and shown on the Shows page.</summary>
    public static ScheduleSource Load(string station, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return new(station, null, null, "No stations config is set (WAA_STATIONS_CONFIG), so there is no schedule URL.");
        try
        {
            var config = StationConfigLoader.Load(configPath.Trim()).FirstOrDefault(s => s.Id == station);
            if (config is null) return new(station, null, null, $"Station '{station}' is not in {configPath}.");
            if (string.IsNullOrWhiteSpace(config.ScheduleUrl)) return new(station, null, null, $"Station '{station}' has no scheduleUrl in {configPath}.");
            return new(station, new Uri(config.ScheduleUrl), TimeZoneInfo.FindSystemTimeZoneById(config.Timezone), null);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new(station, null, null, $"The stations config could not be read: {ex.Message}");
        }
    }
}

/// <summary>What the Shows page knows about the schedule right now. All times UTC.</summary>
public sealed record ScheduleSnapshot(
    WeeklySchedule? Schedule,
    DateTimeOffset? FetchedUtc,
    bool FromSavedCopy,
    DateTimeOffset? LastAttemptUtc,
    string? LastError)
{
    public static readonly ScheduleSnapshot Empty = new(null, null, false, null, null);
}

/// <summary>
/// Fetches the station's schedule page, parses it, and keeps the last good schedule, in memory and in the cache volume
/// (cache/schedule/&lt;station&gt;.json) so it survives a restart. A failed fetch or a page that no longer parses never
/// replaces a good schedule: the last good one is kept and the failure is reported. Nothing here throws to a caller.
/// </summary>
public sealed class ScheduleService(ScheduleSource source, WebSettings settings, IHttpClientFactory http, TimeProvider time, ILogger<ScheduleService> log)
{
    public const string HttpClientName = "schedule";
    public const string UserAgent = "WebAudioArchive/1.0 (schedule reader; +https://github.com/davidnmbond/WebAudioArchive)";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile ScheduleSnapshot _current = ScheduleSnapshot.Empty;
    bool _savedLoaded;

    public ScheduleSource Source => source;

    public ScheduleSnapshot Current
    {
        get
        {
            EnsureSavedLoaded();
            return _current;
        }
    }

    public string SavedPath => Path.Combine(settings.CacheDir, "schedule", source.Station + ".json");

    /// <summary>Waits (up to <paramref name="limit"/>) for the first fetch when there is no schedule yet; returns at once otherwise.</summary>
    public async Task<ScheduleSnapshot> WaitForScheduleAsync(TimeSpan limit)
    {
        if (Current.Schedule is null && source.IsConfigured)
        {
            try
            {
                await _firstAttempt.Task.WaitAsync(limit);
            }
            catch (TimeoutException)
            {
            }
        }

        return Current;
    }

    /// <summary>Fetches and parses the page once. A success replaces the schedule and saves it; a failure keeps the last good one.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSavedLoaded();
        if (!source.IsConfigured)
        {
            _current = _current with { LastAttemptUtc = time.GetUtcNow(), LastError = source.Problem };
            _firstAttempt.TrySetResult();
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var (schedule, error) = await FetchAsync(ct);
            if (schedule is not null)
            {
                _current = new ScheduleSnapshot(schedule, now, false, now, null);
                Save(schedule, now);
                log.LogInformation("Schedule for {Station} fetched from {Url}: {Slots} weekly slots", source.Station, source.Url, schedule.Slots.Count);
            }
            else
            {
                _current = _current with { LastAttemptUtc = now, LastError = error };
                log.LogWarning("Schedule for {Station} not updated: {Error}. {Kept}", source.Station, error,
                    _current.Schedule is null ? "There is no last good schedule." : "Keeping the last good schedule.");
            }
        }
        finally
        {
            _gate.Release();
            _firstAttempt.TrySetResult();
        }
    }

    async Task<(WeeklySchedule? Schedule, string? Error)> FetchAsync(CancellationToken ct)
    {
        string html;
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return (null, $"{source.Url} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            html = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"{source.Url} did not answer within {FetchTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"{source.Url} could not be fetched: {ex.Message}");
        }

        var parsed = BurstScheduleParser.Parse(html);
        return parsed.Schedule is not null ? (parsed.Schedule, null) : (null, "The schedule page was not understood: " + parsed.Error);
    }

    /// <summary>Every scheduled show overlapping [fromUtc, toUtc), or none when there is no schedule.</summary>
    public IReadOnlyList<ShowOccurrence> Occurrences(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var s = Current.Schedule;
        return s is null || source.Zone is null ? [] : ScheduleExpander.Expand(s, source.Zone, source.Station, fromUtc, toUtc);
    }

    /// <summary>The occurrence with this id in the current schedule, worked out again from the id (not held in memory).</summary>
    public ShowOccurrence? Find(string? id)
    {
        if (!ShowOccurrence.TryParseId(id, out var station, out var start) || station != source.Station) return null;
        return Occurrences(start.AddMinutes(-1), start.AddMinutes(1)).FirstOrDefault(o => o.StartUtc == start);
    }

    void EnsureSavedLoaded()
    {
        if (_savedLoaded) return;
        _savedLoaded = true;
        try
        {
            if (!File.Exists(SavedPath)) return;
            var saved = JsonSerializer.Deserialize<SavedScheduleFile>(File.ReadAllText(SavedPath), Json);
            var schedule = saved?.ToSchedule();
            if (saved is null || schedule is null || saved.Station != source.Station)
            {
                log.LogWarning("Ignoring the saved schedule {Path}: it is not a valid schedule for {Station}", SavedPath, source.Station);
                return;
            }

            if (_current.Schedule is null) _current = new ScheduleSnapshot(schedule, saved.FetchedUtc, true, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log.LogWarning("Could not read the saved schedule {Path}: {Error}", SavedPath, ex.Message);
        }
    }

    void Save(WeeklySchedule schedule, DateTimeOffset fetched)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
            var tmp = SavedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(SavedScheduleFile.From(source.Station, source.Url?.ToString(), source.Zone?.Id, schedule, fetched), Json));
            File.Move(tmp, SavedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not save the schedule to {Path}: {Error}", SavedPath, ex.Message);
        }
    }
}

/// <summary>Fetches the schedule at start-up and then every 15 minutes.</summary>
public sealed class ScheduleRefresher(ScheduleService schedule, TimeProvider time, ILogger<ScheduleRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScheduleService.RefreshInterval, time);
        do
        {
            try
            {
                await schedule.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RefreshAsync reports its own failures; this only keeps an unexpected one from stopping the loop.
                log.LogError(ex, "Schedule refresh failed unexpectedly");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
