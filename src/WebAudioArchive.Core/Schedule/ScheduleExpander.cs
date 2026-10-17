namespace WebAudioArchive.Core.Schedule;

/// <summary>
/// Turns weekly slots into dated occurrences with UTC start and end instants. The station's zone (Europe/London for
/// Burst) is used only here, to read the page's local wall times: in British Summer Time a 20:00 show is 19:00 UTC, in
/// GMT it is 20:00 UTC. A local time that does not exist (the hour skipped when the clocks go forward) moves to the next
/// valid instant; a local time that happens twice (the hour repeated when the clocks go back) takes the first.
/// </summary>
public static class ScheduleExpander
{
    /// <summary>Every occurrence that overlaps [fromUtc, toUtc), in start order.</summary>
    public static IReadOnlyList<ShowOccurrence> Expand(
        WeeklySchedule schedule, TimeZoneInfo zone, string station, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var list = new List<ShowOccurrence>();
        if (toUtc <= fromUtc) return list;

        // Local dates around the range: a slot can start the local day before (long shows, zone offsets) and still overlap.
        var longest = schedule.Slots.Count == 0 ? TimeSpan.Zero : schedule.Slots.Max(s => s.Duration);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromUtc, zone).DateTime).AddDays(-1 - (int)Math.Ceiling(longest.TotalDays));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(toUtc, zone).DateTime).AddDays(1);

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            foreach (var slot in schedule.Slots.Where(s => s.Day == date.DayOfWeek))
            {
                var o = Occurrence(slot, date, zone, station);
                if (o.EndUtc > fromUtc && o.StartUtc < toUtc) list.Add(o);
            }
        }

        return list.OrderBy(o => o.StartUtc).ThenBy(o => o.Title, StringComparer.Ordinal).ToList();
    }

    public static ShowOccurrence Occurrence(ScheduleSlot slot, DateOnly localDate, TimeZoneInfo zone, string station)
    {
        var localStart = localDate.ToDateTime(slot.LocalStart);
        var start = ToUtc(localStart, zone);
        var end = ToUtc(localStart + slot.Duration, zone);
        if (end <= start) end = start + slot.Duration;
        return new ShowOccurrence(station, slot.Title, start, end);
    }

    /// <summary>The UTC instant of a local wall time in <paramref name="zone"/>.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Skipped by a forward change: step to the first valid whole minute, which is the moment of the change.
            var t = new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Unspecified);
            var guard = 0;
            while (zone.IsInvalidTime(t) && guard++ < 24 * 60) t = t.AddMinutes(1);
            return new DateTimeOffset(t, zone.GetUtcOffset(t)).ToUniversalTime();
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Repeated by a backward change: the first of the two is the one with the larger offset (summer time).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
