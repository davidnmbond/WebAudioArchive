extern alias web;

using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

/// <summary>Weekly slots to UTC occurrences in Europe/London, across both clock changes.</summary>
public sealed class ScheduleExpanderTests
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    static WeeklySchedule One(DayOfWeek day, string start, double hours, string title = "Show") =>
        new([new ScheduleSlot(day, TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(hours), title)]);

    static ShowOccurrence Single(WeeklySchedule s, DateTimeOffset from, DateTimeOffset to) =>
        Assert.Single(ScheduleExpander.Expand(s, London, "burst", from, to));

    [Fact]
    public void In_british_summer_time_a_20_00_show_is_19_00_utc()
    {
        var o = Single(One(DayOfWeek.Thursday, "20:00", 1), Utc(2026, 10, 15, 0), Utc(2026, 10, 16, 0));

        Assert.Equal(Utc(2026, 10, 15, 19), o.StartUtc);
        Assert.Equal(Utc(2026, 10, 15, 20), o.EndUtc);
        Assert.Equal(TimeSpan.Zero, o.StartUtc.Offset);
        Assert.Equal("burst-20261015T1900Z", o.Id);
    }

    [Fact]
    public void After_the_clocks_go_back_on_25_october_2026_the_same_show_is_an_hour_later_in_utc()
    {
        var s = One(DayOfWeek.Monday, "20:00", 1);
        var before = Single(s, Utc(2026, 10, 19, 0), Utc(2026, 10, 20, 0));
        var after = Single(s, Utc(2026, 10, 26, 0), Utc(2026, 10, 27, 0));

        Assert.Equal(Utc(2026, 10, 19, 19), before.StartUtc);
        Assert.Equal(Utc(2026, 10, 26, 20), after.StartUtc);

        // On the day itself the change is at 02:00 local, so an evening show is already in GMT.
        Assert.Equal(Utc(2026, 10, 25, 20), Single(One(DayOfWeek.Sunday, "20:00", 1), Utc(2026, 10, 25, 0), Utc(2026, 10, 26, 0)).StartUtc);
    }

    [Fact]
    public void A_local_time_repeated_when_the_clocks_go_back_takes_the_first_one()
    {
        // 25 Oct 2026: 01:30 happens twice, at 00:30 UTC (BST) and 01:30 UTC (GMT).
        var o = Single(One(DayOfWeek.Sunday, "01:30", 1), Utc(2026, 10, 25, 0), Utc(2026, 10, 25, 12));

        Assert.Equal(Utc(2026, 10, 25, 0, 30), o.StartUtc);
        Assert.Equal(Utc(2026, 10, 25, 2, 30), o.EndUtc); // 02:30 local is GMT: the wall-clock hour lasts two real hours
    }

    [Fact]
    public void In_spring_2027_a_skipped_local_time_moves_to_the_next_valid_instant()
    {
        // 28 Mar 2027: 01:00 GMT jumps to 02:00 BST, so 01:30 local does not exist; it becomes 02:00 BST = 01:00 UTC.
        var o = Single(One(DayOfWeek.Sunday, "01:30", 1), Utc(2027, 3, 27, 12), Utc(2027, 3, 28, 12));

        Assert.Equal(Utc(2027, 3, 28, 1), o.StartUtc);
        Assert.Equal(Utc(2027, 3, 28, 1, 30), o.EndUtc); // 02:30 BST

        var week = One(DayOfWeek.Monday, "20:00", 1);
        Assert.Equal(Utc(2027, 3, 22, 20), Single(week, Utc(2027, 3, 22, 0), Utc(2027, 3, 23, 0)).StartUtc);
        Assert.Equal(Utc(2027, 3, 29, 19), Single(week, Utc(2027, 3, 29, 0), Utc(2027, 3, 30, 0)).StartUtc);
    }

    [Fact]
    public void To_utc_converts_gmt_bst_and_both_kinds_of_edge()
    {
        Assert.Equal(Utc(2026, 12, 1, 20), ScheduleExpander.ToUtc(new DateTime(2026, 12, 1, 20, 0, 0), London));
        Assert.Equal(Utc(2026, 7, 1, 19), ScheduleExpander.ToUtc(new DateTime(2026, 7, 1, 20, 0, 0), London));
        Assert.Equal(Utc(2027, 3, 28, 1), ScheduleExpander.ToUtc(new DateTime(2027, 3, 28, 1, 0, 0), London));
        Assert.Equal(Utc(2027, 3, 28, 1), ScheduleExpander.ToUtc(new DateTime(2027, 3, 28, 1, 59, 30), London));
        Assert.Equal(Utc(2026, 10, 25, 0), ScheduleExpander.ToUtc(new DateTime(2026, 10, 25, 1, 0, 0), London));
    }

    [Fact]
    public void Occurrences_that_span_midnight_are_found_from_either_side()
    {
        // Friday 23:00 local for two hours, in GMT: Friday 23:00 to Saturday 01:00 UTC.
        var late = One(DayOfWeek.Friday, "23:00", 2);
        var fromSaturday = Single(late, Utc(2026, 11, 7, 0), Utc(2026, 11, 8, 0));
        Assert.Equal(Utc(2026, 11, 6, 23), fromSaturday.StartUtc);
        Assert.Equal(Utc(2026, 11, 7, 1), fromSaturday.EndUtc);

        // Saturday 00:30 local in BST is Friday 23:30 UTC: the UTC date is the day before the local one.
        var early = Single(One(DayOfWeek.Saturday, "00:30", 1), Utc(2026, 10, 16, 23), Utc(2026, 10, 17, 0));
        Assert.Equal(Utc(2026, 10, 16, 23, 30), early.StartUtc);
        Assert.Equal(DayOfWeek.Friday, early.StartUtc.UtcDateTime.DayOfWeek);
    }

    [Fact]
    public void The_fixture_expands_to_a_week_of_utc_occurrences_with_stable_ids()
    {
        var schedule = BurstScheduleParser.Parse(ScheduleParserTests.Fixture).Schedule!;
        var week = ScheduleExpander.Expand(schedule, London, "burst", Utc(2026, 10, 12, 0), Utc(2026, 10, 19, 0));

        Assert.Equal(90, week.Count);
        Assert.Equal(week.Count, week.Select(o => o.Id).Distinct().Count());
        Assert.Equal(Utc(2026, 10, 12, 8), week[0].StartUtc); // Monday 09:00 BST
        Assert.Equal("Breakfast with Mackenzie", week[0].Title);
        var thursday = week.Where(o => o.StartUtc.UtcDateTime.Date == new DateTime(2026, 10, 15)).ToList();
        Assert.Contains(thursday, o => o.Title == "Backseat Aux" && o.StartUtc == Utc(2026, 10, 15, 19) && o.EndUtc == Utc(2026, 10, 15, 20));

        // The week after the change: Monday 09:00 GMT is 09:00 UTC.
        var next = ScheduleExpander.Expand(schedule, London, "burst", Utc(2026, 10, 26, 0), Utc(2026, 11, 2, 0));
        Assert.Equal(Utc(2026, 10, 26, 9), next[0].StartUtc);
        Assert.Equal(90, next.Count);

        Assert.True(ShowOccurrence.TryParseId("burst-20261015T1900Z", out var station, out var start));
        Assert.Equal(("burst", Utc(2026, 10, 15, 19)), (station, start));
        Assert.False(ShowOccurrence.TryParseId("burst-2026-10-15", out _, out _));
        Assert.False(ShowOccurrence.TryParseId("../x-20261015T1900Z", out _, out _));
    }

    [Fact]
    public void Only_occurrences_overlapping_the_range_are_returned()
    {
        var s = One(DayOfWeek.Thursday, "20:00", 1);
        Assert.Single(ScheduleExpander.Expand(s, London, "burst", Utc(2026, 10, 15, 19, 59), Utc(2026, 10, 15, 19, 59).AddSeconds(1)));
        Assert.Empty(ScheduleExpander.Expand(s, London, "burst", Utc(2026, 10, 15, 20), Utc(2026, 10, 15, 21)));
        Assert.Empty(ScheduleExpander.Expand(s, London, "burst", Utc(2026, 10, 15, 21), Utc(2026, 10, 15, 20)));
    }
}
