using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace WebAudioArchive.Web.Schedule;

/// <summary>
/// Reads Burst Radio's schedule page: a server-rendered table whose header is an empty cell then Monday to Sunday, and
/// whose body rows each start with an hourly local time (09:00, 10:00, ...) followed by one cell per day. A cell's title
/// is its data-show attribute (the page's own name for the show; the visible text has had typos) or else its text. A
/// cell may span hours with rowspan; an empty cell is an hour with no show. A show that fills consecutive hours of one
/// day, by rowspan or by the same title repeated, is one slot. Times on the page are local to the station (the page
/// states no zone; the station's configured zone is applied later, when slots are expanded to UTC instants).
///
/// The parser fails closed: anything it does not recognise (no such table, a different header, a time that is not on
/// the hour or not one hour after the last, a row with the wrong number of cells, colspan, no shows at all) gives an
/// error and no schedule, never a guess.
/// </summary>
public static partial class BurstScheduleParser
{
    static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    static readonly DayOfWeek[] DayValues =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The page lists at most a day of hours; more rows than this is not the table we know.</summary>
    const int MaxRows = 24;

    public static ScheduleParseResult Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return ScheduleParseResult.Fail("The schedule page is empty.");

        var doc = new HtmlParser().ParseDocument(html);
        var tables = doc.QuerySelectorAll("table").OfType<IHtmlTableElement>().Where(HasDayHeader).ToList();
        if (tables.Count == 0)
            return ScheduleParseResult.Fail("No table with a header of an empty cell then Monday to Sunday was found on the schedule page.");
        if (tables.Count > 1)
            return ScheduleParseResult.Fail($"{tables.Count} tables with a Monday to Sunday header were found; expected exactly one.");

        var table = tables[0];
        var rows = table.Bodies.SelectMany(b => b.Rows).ToList();
        if (rows.Count == 0) return ScheduleParseResult.Fail("The schedule table has no body rows.");
        if (rows.Count > MaxRows) return ScheduleParseResult.Fail($"The schedule table has {rows.Count} body rows; at most {MaxRows} hourly rows were expected.");

        // grid[row, day] is the title in that hour (empty for no show). A rowspan fills the hours below it.
        var grid = new string?[rows.Count, 7];
        var starts = new TimeOnly[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Cells.ToList();
            if (cells.Count == 0) return ScheduleParseResult.Fail($"Row {r + 1} of the schedule table has no cells.");

            var timeText = Text(cells[0]);
            if (!TimePattern().IsMatch(timeText))
                return ScheduleParseResult.Fail($"Row {r + 1} starts with '{Short(timeText)}', not an hourly time such as 09:00.");
            starts[r] = TimeOnly.ParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture);
            if (r > 0 && starts[r] != starts[r - 1].AddHours(1))
                return ScheduleParseResult.Fail($"Row {r + 1} is at {timeText}, not one hour after {starts[r - 1]:HH:mm}.");
            if (r > 0 && starts[r] < starts[r - 1])
                return ScheduleParseResult.Fail($"Row {r + 1} at {timeText} wraps past midnight, which this parser does not expect.");

            var day = 0;
            foreach (var cell in cells.Skip(1))
            {
                while (day < 7 && grid[r, day] is not null) day++;
                if (day >= 7) return ScheduleParseResult.Fail($"Row {r + 1} ({timeText}) has more than 7 day cells.");
                if (cell.ColumnSpan != 1) return ScheduleParseResult.Fail($"Row {r + 1} ({timeText}) has a cell spanning {cell.ColumnSpan} days, which this parser does not expect.");

                var span = Math.Max(1, cell.RowSpan);
                if (r + span > rows.Count)
                    return ScheduleParseResult.Fail($"A cell in row {r + 1} ({timeText}) spans {span} hours, past the end of the table.");
                var title = Title(cell);
                for (var k = 0; k < span; k++)
                {
                    if (grid[r + k, day] is not null)
                        return ScheduleParseResult.Fail($"Cells overlap at row {r + k + 1}, {Days[day]}.");
                    grid[r + k, day] = title;
                }

                day++;
            }

            for (var d = 0; d < 7; d++)
            {
                if (grid[r, d] is null)
                    return ScheduleParseResult.Fail($"Row {r + 1} ({timeText}) has no cell for {Days[d]}.");
            }
        }

        var slots = new List<ScheduleSlot>();
        for (var d = 0; d < 7; d++)
        {
            var r = 0;
            while (r < rows.Count)
            {
                var title = grid[r, d]!;
                var end = r + 1;
                while (end < rows.Count && grid[end, d] == title) end++;
                if (title.Length > 0) slots.Add(new ScheduleSlot(DayValues[d], starts[r], TimeSpan.FromHours(end - r), title));
                r = end;
            }
        }

        if (slots.Count == 0) return ScheduleParseResult.Fail("The schedule table was recognised but lists no shows.");
        return ScheduleParseResult.Ok(new WeeklySchedule(slots));
    }

    static bool HasDayHeader(IHtmlTableElement table)
    {
        var header = table.Head?.Rows.FirstOrDefault() ?? table.Rows.FirstOrDefault();
        if (header is null) return false;
        var texts = header.Cells.Select(Text).ToList();
        return texts.Count == 8 && texts[0].Length == 0 && texts.Skip(1).SequenceEqual(Days, StringComparer.OrdinalIgnoreCase);
    }

    static string Title(IElement cell)
    {
        var attr = Collapse(cell.GetAttribute("data-show") ?? "");
        return attr.Length > 0 ? attr : Text(cell);
    }

    static string Text(IElement e) => Collapse(e.TextContent);

    static string Collapse(string s) => Whitespace().Replace(s, " ").Trim();

    static string Short(string s) => s.Length <= 40 ? s : s[..40] + "...";

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):00$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
