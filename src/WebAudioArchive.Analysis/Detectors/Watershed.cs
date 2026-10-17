using System.Globalization;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// The broadcast watershed in the station's local time (UK: 21:00 to 05:30). "Before the watershed" is the protected
/// daytime period, local time from End up to Start. The zone is used only to decide this; times stay UTC.
/// </summary>
public sealed class Watershed(TimeZoneInfo zone, TimeOnly start, TimeOnly end)
{
    public static Watershed From(SwearOptions o, TimeZoneInfo zone) => new(
        zone,
        TimeOnly.ParseExact(o.WatershedStart, "HH:mm", CultureInfo.InvariantCulture),
        TimeOnly.ParseExact(o.WatershedEnd, "HH:mm", CultureInfo.InvariantCulture));

    public bool IsBeforeWatershed(DateTimeOffset utc)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        var inWatershed = start <= end ? local >= start && local < end : local >= start || local < end;
        return !inWatershed;
    }

    /// <summary>The UTC instant the watershed starts on the local day containing <paramref name="utc"/>.</summary>
    public DateTimeOffset StartUtcOn(DateTimeOffset utc)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        var local = localDate.ToDateTime(start);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
