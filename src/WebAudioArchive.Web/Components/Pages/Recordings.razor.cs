using Microsoft.AspNetCore.Components;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Recordings
{
    [Inject] public required RecordingsQuery Query { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }

    PDTable<RecordingRow>? Table { get; set; }
    RecordingsDataProvider Provider { get; } = new();
    bool Filtering { get; set; }
    DateOnly? FilterDate { get; set; }
    DateOnly Today => DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);

    static readonly SortCriteria Sort = new("start", SortDirection.Descending);
    static readonly uint[] PageSizes = [25, 50, 100, 500];
    readonly PageCriteria Paging = new(1, 50);

    protected override void OnInitialized() => Provider.Rows = Query.Rows(null);

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
        if (Table is not null) await Table.RefreshAsync();
    }
}
