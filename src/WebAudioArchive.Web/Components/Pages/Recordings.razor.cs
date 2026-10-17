using Microsoft.AspNetCore.Components;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Enums;
using PanoramicData.Blazor.Models;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Recordings
{
    [Inject] public required RecordingsQuery Query { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }
    [Inject] public required NavigationManager Nav { get; set; }

    PDTable<RecordingRow>? Table { get; set; }
    RecordingsDataProvider Provider { get; } = new();
    bool Filtering { get; set; }
    DateOnly? FilterDate { get; set; }
    DateOnly Today => DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);

    static readonly SortCriteria Sort = new("start", SortDirection.Descending);
    static readonly uint[] PageSizes = [25, 50, 100, 500];
    readonly PageCriteria Paging = new(1, 50);

    static readonly IReadOnlyList<CalendarView> CalendarViews = [CalendarView.Week, CalendarView.Month, CalendarView.Agenda];
    IReadOnlyList<RecordingCalendarItem> CalendarItems { get; set; } = [];
    (DateTimeOffset From, DateTimeOffset To) CalendarRange { get; set; }
    // The week grid starts here (data-scroll-hour, js/calendar-state.js) in the frame it is drawn, rather than through
    // PDCalendar's ScrollToHour, which scrolls after a frame at midnight has been painted.
    int ScrollHour => Math.Max(0, Time.GetUtcNow().UtcDateTime.Hour - 3);

    // PDTable fetches its rows only after its first render, so until then it would say there are none.
    bool TableReady { get; set; }

    protected override void OnInitialized()
    {
        Provider.Rows = Query.Rows(null);
        var today = Today;
        var monday = today.AddDays(-WebAudioArchive.Core.Schedule.WeeklySchedule.DayIndex(today.DayOfWeek));
        var from = new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        OnCalendarRangeChanged((from, from.AddDays(7)));
    }

    void OnCalendarRangeChanged((DateTimeOffset From, DateTimeOffset To) range)
    {
        if (range == CalendarRange) return;
        CalendarRange = range;
        CalendarItems = Query.CalendarItems(range.From, range.To);
    }

    // The same as the table's Play button: the Player, prefilled to that segment (or a minute either side of a gap).
    void OpenShow(RecordingCalendarItem item) => Nav.NavigateTo(item.PlayUrl);

    async Task OnFilteringChangedAsync(bool on)
    {
        Filtering = on;
        FilterDate ??= Today;
        await ReloadAsync();
    }

    async Task OnDateChangedAsync(DateOnly? date)
    {
        FilterDate = date;
        Filtering = date is not null;
        await ReloadAsync();
    }

    async Task ReloadAsync()
    {
        Provider.Rows = Query.Rows(Filtering ? FilterDate : null);
        CalendarItems = Query.CalendarItems(CalendarRange.From, CalendarRange.To);
        if (Table is not null) await Table.RefreshAsync();
    }
}
