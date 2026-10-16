using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PanoramicData.Blazor.Enums;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Components.Pages;

/// <summary>A scheduled show on the calendar with its coverage.</summary>
public sealed record ShowItem(ShowOccurrence Show, ShowCoverageResult Coverage);

public sealed partial class Shows
{
    [Inject] public required ScheduleService Schedule { get; set; }
    [Inject] public required SegmentCatalog Catalog { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }
    [Inject] public required NavigationManager Nav { get; set; }
    [Inject] public required IJSRuntime JS { get; set; }

    /// <summary>?view=week|month|agenda and ?date=yyyy-MM-dd (a UTC date) pick what is shown first.</summary>
    [SupplyParameterFromQuery(Name = "view")] public string? QueryView { get; set; }
    [SupplyParameterFromQuery(Name = "date")] public string? QueryDate { get; set; }

    static readonly IReadOnlyList<CalendarView> Views = [CalendarView.Week, CalendarView.Month, CalendarView.Agenda];

    static readonly (string Css, string Icon, string Text)[] Legend =
    [
        ("show-full", "fas fa-check-circle", "Recorded"),
        ("show-partial", "fas fa-adjust", "Partly recorded"),
        ("show-missing", "fas fa-times-circle", "Not recorded"),
        ("show-live", "fas fa-circle", "On air now"),
        ("show-future", "far fa-clock", "In the future"),
    ];

    ScheduleSnapshot Snapshot { get; set; } = ScheduleSnapshot.Empty;
    IReadOnlyList<ShowItem> Items { get; set; } = [];
    (DateTimeOffset From, DateTimeOffset To) Range { get; set; }
    CalendarView View { get; set; } = CalendarView.Week;
    DateOnly? FocusDate { get; set; }
    bool Loading { get; set; }

    protected override async Task OnInitializedAsync()
    {
        View = QueryView?.ToLowerInvariant() switch
        {
            "month" => CalendarView.Month,
            "agenda" => CalendarView.Agenda,
            _ => CalendarView.Week,
        };
        if (DateOnly.TryParseExact(QueryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) FocusDate = d;

        Snapshot = await Schedule.WaitForScheduleAsync(TimeSpan.FromSeconds(10));

        // The calendar asks for its range as it is first shown; load the week it will show now, so the first render has items.
        var date = FocusDate ?? DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);
        var monday = date.AddDays(-WeeklySchedule.DayIndex(date.DayOfWeek));
        var from = new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Load((from, from.AddDays(7)));
    }

    Task OnRangeChangedAsync((DateTimeOffset From, DateTimeOffset To) range)
    {
        if (range != Range) Load(range);
        return Task.CompletedTask;
    }

    void Load((DateTimeOffset From, DateTimeOffset To) range)
    {
        Loading = true;
        try
        {
            Snapshot = Schedule.Current;
            Range = range;
            var now = Time.GetUtcNow();
            var recorders = Catalog.All();
            Items = Schedule.Occurrences(range.From, range.To)
                .Select(o => new ShowItem(o, ShowCoverage.Classify(o, recorders, now)))
                .ToList();
        }
        finally
        {
            Loading = false;
        }
    }

    // The calendar raises these, then OnVisibleRangeChanged, then renders once. They must not await anything, or the
    // calendar would render the new week with the old week's items (an empty grid) before the new items are loaded.
    void OnViewChanged(CalendarView view)
    {
        View = view;
        KeepInAddress();
    }

    void OnDateChanged(DateOnly? date)
    {
        FocusDate = date;
        KeepInAddress();
    }

    /// <summary>The address of the shown view and date, such as shows?view=month&amp;date=2026-10-19 (a UTC date).</summary>
    public static string AddressFor(CalendarView view, DateOnly date) =>
        $"shows?view={view.ToString().ToLowerInvariant()}&date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    // Puts the shown view and date in the address without a navigation (so nothing re-renders and focus stays on the
    // calendar), so that Back from a show comes back to the same week and view. Not awaited; see above.
    void KeepInAddress()
    {
        var date = FocusDate ?? DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);
        _ = ReplaceAddressAsync(AddressFor(View, date));
    }

    async Task ReplaceAddressAsync(string url)
    {
        try
        {
            await JS.InvokeVoidAsync("waaCalendar.replaceUrl", url);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Only the address is not updated; Back then shows the current week.
        }
    }

    void Open(ShowItem item) => Nav.NavigateTo("/shows/" + item.Show.Id);

    static string Tooltip(ShowItem i) =>
        $"{i.Show.Title}\n{Utc(i.Show.StartUtc)} to {Utc(i.Show.EndUtc)}\n{i.Coverage.Summary}";

    static string Utc(DateTimeOffset t) => ClipPlanner.Utc(t);
}
