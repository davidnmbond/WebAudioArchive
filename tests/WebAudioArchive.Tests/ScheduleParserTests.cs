extern alias web;

using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

/// <summary>The Burst schedule parser against the saved page (tests/fixtures), and pages it must refuse.</summary>
public sealed class ScheduleParserTests
{
    public static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "burst-schedule-2026-10-15.html"));

    static WeeklySchedule ParseOk(string html)
    {
        var result = BurstScheduleParser.Parse(html);
        Assert.True(result.Schedule is not null, result.Error);
        Assert.Null(result.Error);
        return result.Schedule!;
    }

    static string FailWith(string html)
    {
        var result = BurstScheduleParser.Parse(html);
        Assert.Null(result.Schedule);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        return result.Error!;
    }

    static ScheduleSlot Slot(WeeklySchedule s, DayOfWeek day, string time) =>
        Assert.Single(s.Slots, x => x.Day == day && x.LocalStart == TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void The_fixture_gives_every_hourly_slot_from_09_00_to_22_00_on_each_day()
    {
        var s = ParseOk(Fixture);

        // 13 hourly rows x 7 days, all filled; Sunday's SRA Chart Show fills 14:00 and 15:00, so it is one two hour slot.
        Assert.Equal(90, s.Slots.Count);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var slots = s.Slots.Where(x => x.Day == day).ToList();
            Assert.Equal(TimeOnly.Parse("09:00"), slots.Min(x => x.LocalStart));
            Assert.Equal(TimeSpan.FromHours(13), TimeSpan.FromTicks(slots.Sum(x => x.Duration.Ticks)));
            Assert.Equal(TimeOnly.Parse("22:00"), slots.Max(x => x.LocalStart.Add(x.Duration)));
        }
    }

    [Fact]
    public void The_fixture_titles_and_times_are_read_exactly()
    {
        var s = ParseOk(Fixture);

        Assert.Equal("Breakfast with Mackenzie", Slot(s, DayOfWeek.Monday, "09:00").Title);
        Assert.Equal("Sunday Hangover", Slot(s, DayOfWeek.Sunday, "09:00").Title);
        Assert.Equal("Backseat Aux", Slot(s, DayOfWeek.Thursday, "20:00").Title);
        Assert.Equal("The Noise Exchange", Slot(s, DayOfWeek.Thursday, "21:00").Title);
        Assert.Equal("Dazed", Slot(s, DayOfWeek.Sunday, "21:00").Title);
        Assert.Equal(TimeSpan.FromHours(1), Slot(s, DayOfWeek.Thursday, "20:00").Duration);

        // Entities and typographic apostrophes are decoded; the data-show name wins over a typo in the visible text.
        Assert.Equal("Mags & Lou: Live from Whipperton", Slot(s, DayOfWeek.Wednesday, "14:00").Title);
        Assert.Equal("Mayson’s Musical Menagerie", Slot(s, DayOfWeek.Saturday, "12:00").Title);
        Assert.Equal("Infinite Frequency with Holly and Olivia", Slot(s, DayOfWeek.Thursday, "11:00").Title);
        Assert.Equal("What's the 'Diff: with Nye & Albert", Slot(s, DayOfWeek.Saturday, "16:00").Title);

        var chart = Slot(s, DayOfWeek.Sunday, "14:00");
        Assert.Equal("SRA Chart Show", chart.Title);
        Assert.Equal(TimeSpan.FromHours(2), chart.Duration);
        Assert.DoesNotContain(s.Slots, x => x.Day == DayOfWeek.Sunday && x.LocalStart == TimeOnly.Parse("15:00"));
    }

    static string Table(string header, params string[] rows) =>
        $"<html><body><table><thead><tr>{header}</tr></thead><tbody>{string.Join("", rows.Select(r => "<tr>" + r + "</tr>"))}</tbody></table></body></html>";

    const string Header = "<th><br></th><th>Monday</th><th>Tuesday</th><th>Wednesday</th><th>Thursday</th><th>Friday</th><th>Saturday</th><th>Sunday</th>";

    static string Cells(params string[] titles) => string.Join("", titles.Select(t => $"<td class=\"show-cell\">{t}</td>"));

    [Fact]
    public void Rowspan_cells_span_hours_and_empty_cells_are_hours_without_a_show()
    {
        var html = Table(Header,
            "<td>20:00</td>" + Cells("Mon 20", "") + "<td rowspan=\"2\">Long Wednesday</td>" + Cells("Thu", "Fri", "Sat", "Sun"),
            "<td>21:00</td>" + Cells("Mon 21", "Tue 21", "Thu 21", "Fri 21", "Sat 21", "Sun 21"));

        var s = ParseOk(html);

        var wednesday = Slot(s, DayOfWeek.Wednesday, "20:00");
        Assert.Equal("Long Wednesday", wednesday.Title);
        Assert.Equal(TimeSpan.FromHours(2), wednesday.Duration);
        Assert.Equal("Tue 21", Slot(s, DayOfWeek.Tuesday, "21:00").Title);
        Assert.DoesNotContain(s.Slots, x => x.Day == DayOfWeek.Tuesday && x.LocalStart == TimeOnly.Parse("20:00"));
        Assert.Equal("Thu 21", Slot(s, DayOfWeek.Thursday, "21:00").Title);
        Assert.Equal(12, s.Slots.Count); // 6 in the first row (Tuesday empty), 6 in the second (Wednesday continues)
    }

    [Fact]
    public void A_repeated_title_on_another_day_or_not_adjacent_stays_separate()
    {
        var html = Table(Header,
            "<td>09:00</td>" + Cells("Same", "Same", "A", "A", "A", "A", "A"),
            "<td>10:00</td>" + Cells("Other", "x", "B", "B", "B", "B", "B"),
            "<td>11:00</td>" + Cells("Same", "y", "C", "C", "C", "C", "C"));

        var s = ParseOk(html);

        Assert.Equal(2, s.Slots.Count(x => x.Day == DayOfWeek.Monday && x.Title == "Same"));
        Assert.Equal("Same", Slot(s, DayOfWeek.Tuesday, "09:00").Title);
    }

    [Theory]
    [InlineData("<th>Monday</th>", "<th>Mon</th>")]          // a day renamed
    [InlineData("<th>Sunday</th>", "")]                      // a day removed
    [InlineData("<th>Monday</th>", "<th>Lundi</th>")]        // another language
    public void A_changed_header_fails_closed(string replace, string with)
    {
        var html = Fixture.Replace(replace, with, StringComparison.Ordinal);
        Assert.NotEqual(Fixture, html);
        Assert.Contains("Monday to Sunday", FailWith(html));
    }

    [Fact]
    public void A_mutated_fixture_fails_closed_instead_of_returning_garbage()
    {
        // The 13:00 row loses its Wednesday cell: every later column would shift by a day if this were accepted.
        var mutated = Fixture.Replace("<td class=\"show-cell\" data-show=\"Groove Central Station\">Groove Central Station</td>", "", StringComparison.Ordinal);
        Assert.NotEqual(Fixture, mutated);
        Assert.Contains("no cell for Sunday", FailWith(mutated));
    }

    [Fact]
    public void A_time_that_is_not_an_hourly_HH_mm_fails_closed()
    {
        Assert.Contains("not an hourly time", FailWith(Fixture.Replace("<td>13:00</td>", "<td>1pm</td>", StringComparison.Ordinal)));
        Assert.Contains("not an hourly time", FailWith(Fixture.Replace("<td>13:00</td>", "<td>13:30</td>", StringComparison.Ordinal)));
        Assert.Contains("not one hour after", FailWith(Fixture.Replace("<td>13:00</td>", "<td>14:00</td>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extra_cells_colspan_and_overrunning_rowspan_fail_closed()
    {
        Assert.Contains("more than 7", FailWith(Table(Header, "<td>09:00</td>" + Cells("1", "2", "3", "4", "5", "6", "7", "8"))));
        Assert.Contains("spanning 2 days", FailWith(Table(Header, "<td>09:00</td><td colspan=\"2\">x</td>" + Cells("3", "4", "5", "6", "7"))));
        Assert.Contains("past the end", FailWith(Table(Header, "<td>09:00</td><td rowspan=\"3\">x</td>" + Cells("2", "3", "4", "5", "6", "7"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><p>Schedule coming soon</p></body></html>")]
    [InlineData("<html><body><table><tr><td>09:00</td></tr></table></body></html>")]
    public void A_page_without_the_table_fails_closed(string html)
    {
        FailWith(html);
    }

    [Fact]
    public void A_table_with_no_shows_or_two_schedule_tables_fails_closed()
    {
        Assert.Contains("no shows", FailWith(Table(Header, "<td>09:00</td>" + Cells("", "", "", "", "", "", ""))));
        var one = Table(Header, "<td>09:00</td>" + Cells("1", "2", "3", "4", "5", "6", "7"));
        Assert.Contains("exactly one", FailWith(one.Replace("</body>", one[(one.IndexOf("<table>", StringComparison.Ordinal))..one.IndexOf("</body>", StringComparison.Ordinal)] + "</body>", StringComparison.Ordinal)));
    }
}
