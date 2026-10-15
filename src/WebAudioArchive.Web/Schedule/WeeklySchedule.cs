using System.Globalization;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Web.Schedule;

/// <summary>
/// One weekly recurring show as the station's page gives it: a day of the week, a start in the station's local wall
/// time, a duration and a title. Local time lives only here; everything built from a slot is a UTC instant.
/// </summary>
public sealed record ScheduleSlot(DayOfWeek Day, TimeOnly LocalStart, TimeSpan Duration, string Title);

/// <summary>A parsed weekly schedule: the slots in day (Monday first) and start order.</summary>
public sealed record WeeklySchedule(IReadOnlyList<ScheduleSlot> Slots)
{
    /// <summary>Monday is day 0, as on the station's page.</summary>
    public static int DayIndex(DayOfWeek d) => ((int)d + 6) % 7;
}

/// <summary>The outcome of parsing a schedule page: a schedule, or why the page was not understood (never both).</summary>
public sealed record ScheduleParseResult(WeeklySchedule? Schedule, string? Error)
{
    public static ScheduleParseResult Ok(WeeklySchedule s) => new(s, null);
    public static ScheduleParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// One dated broadcast of a slot. The id is derived from the station and the UTC start only, so a link to a show
/// still works after a restart or a page reload.
/// </summary>
public sealed partial record ShowOccurrence(string Station, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public string Id => IdFor(Station, StartUtc);

    public TimeSpan Duration => EndUtc - StartUtc;

    public static string IdFor(string station, DateTimeOffset startUtc) =>
        station + "-" + startUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture);

    /// <summary>Reads an id such as burst-20261015T1900Z back into its station and UTC start.</summary>
    public static bool TryParseId(string? id, out string station, out DateTimeOffset startUtc)
    {
        station = "";
        startUtc = default;
        if (id is null) return false;
        var m = IdPattern().Match(id);
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups[2].Value, "yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return false;
        station = m.Groups[1].Value;
        startUtc = new DateTimeOffset(t, TimeSpan.Zero);
        return true;
    }

    [GeneratedRegex("^([a-z0-9][a-z0-9-]{0,31})-([0-9]{8}T[0-9]{4}Z)$")]
    private static partial Regex IdPattern();
}
