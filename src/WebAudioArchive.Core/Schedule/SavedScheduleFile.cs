using System.Globalization;
using System.Text.Json;

namespace WebAudioArchive.Core.Schedule;

/// <summary>
/// The last good schedule as the web app saves it in its cache volume (cache/schedule/&lt;station&gt;.json) and the analysis
/// service reads it (mounted read-only). Slot times are the station page's local times; nothing here is shown.
/// </summary>
public sealed record SavedScheduleFile(string Station, string? Url, string? Timezone, DateTimeOffset FetchedUtc, List<SavedScheduleSlot> Slots)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static SavedScheduleFile From(string station, string? url, string? timezone, WeeklySchedule schedule, DateTimeOffset fetched) => new(
        station, url, timezone, fetched.ToUniversalTime(),
        schedule.Slots.Select(x => new SavedScheduleSlot(x.Day.ToString(), x.LocalStart.ToString("HH:mm", CultureInfo.InvariantCulture), (int)x.Duration.TotalMinutes, x.Title)).ToList());

    public static SavedScheduleFile? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<SavedScheduleFile>(File.ReadAllText(path), Json) : null;

    public WeeklySchedule? ToSchedule()
    {
        if (Slots is null || Slots.Count == 0) return null;
        var slots = new List<ScheduleSlot>();
        foreach (var s in Slots)
        {
            if (!Enum.TryParse<DayOfWeek>(s.Day, out var day)
                || !TimeOnly.TryParseExact(s.LocalStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || s.Minutes <= 0 || string.IsNullOrWhiteSpace(s.Title))
                return null;
            slots.Add(new ScheduleSlot(day, start, TimeSpan.FromMinutes(s.Minutes), s.Title));
        }

        return new WeeklySchedule(slots);
    }
}

public sealed record SavedScheduleSlot(string Day, string LocalStart, int Minutes, string Title);
