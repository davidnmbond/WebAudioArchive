using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Schedule;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// The station's shows in UTC, from the schedule the web app saved (read-only), reloaded when the file changes. The
/// zone comes from the stations config (Europe/London when it is not given). No file means no shows: segments are still
/// analysed, they just map to unassigned time.
/// </summary>
public sealed class ScheduleProvider
{
    readonly string? _file;
    readonly string _station;
    readonly ILogger _log;
    readonly object _gate = new();
    DateTime _loadedWrite;
    WeeklySchedule? _schedule;

    public ScheduleProvider(string? scheduleFile, string? stationsConfig, string station, ILogger log)
    {
        _file = scheduleFile;
        _station = station;
        _log = log;
        Zone = LoadZone(stationsConfig, station, log);
    }

    public TimeZoneInfo Zone { get; }

    public DateTimeOffset? FetchedUtc { get; private set; }

    public IReadOnlyList<ShowOccurrence> Occurrences(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        lock (_gate)
        {
            Reload();
            return _schedule is null ? [] : ScheduleExpander.Expand(_schedule, Zone, _station, fromUtc, toUtc);
        }
    }

    public string? TitleAt(DateTimeOffset utc) =>
        Occurrences(utc.AddDays(-1), utc.AddSeconds(1)).LastOrDefault(o => o.StartUtc <= utc && utc < o.EndUtc)?.Title;

    void Reload()
    {
        if (_file is null) return;
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.LastWriteTimeUtc == _loadedWrite) return;
            var saved = SavedScheduleFile.Read(_file);
            // Remembered only after the file was read, so a transient IO error is retried on the next call.
            _loadedWrite = info.LastWriteTimeUtc;
            var schedule = saved?.ToSchedule();
            if (saved is null || schedule is null || saved.Station != _station)
            {
                _log.LogWarning("Ignoring the saved schedule {Path}: it is not a valid schedule for {Station}", _file, _station);
                return;
            }

            _schedule = schedule;
            FetchedUtc = saved.FetchedUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A file that parses badly will parse badly again: wait for it to change. An IO error is transient and is retried.
            if (ex is JsonException or NotSupportedException && _file is not null && File.Exists(_file)) _loadedWrite = new FileInfo(_file).LastWriteTimeUtc;
            _log.LogWarning("Could not read the saved schedule {Path}: {Error}", _file, ex.Message);
        }
    }

    static TimeZoneInfo LoadZone(string? stationsConfig, string station, ILogger log)
    {
        var id = "Europe/London";
        if (stationsConfig is not null)
        {
            try
            {
                id = StationConfigLoader.Load(stationsConfig).FirstOrDefault(s => s.Id == station)?.Timezone ?? id;
            }
            catch (ConfigException ex)
            {
                log.LogWarning("Stations config {Path} not usable ({Error}); using {Zone} for the watershed and schedule", stationsConfig, ex.Message, id);
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
