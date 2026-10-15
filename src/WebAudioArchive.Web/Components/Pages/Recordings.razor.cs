using Microsoft.AspNetCore.Components;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Recordings
{
    [Inject] public required RecordingsQuery Query { get; set; }
    [Inject] public required WebSettings Settings { get; set; }

    PDTable<RecordingRow>? Table { get; set; }
    RecordingsDataProvider Provider { get; } = new();
    DateOnly? FilterDate { get; set; }

    static readonly SortCriteria Sort = new("start", SortDirection.Descending);
    static readonly uint[] PageSizes = [25, 50, 100, 500];
    readonly PageCriteria Paging = new(1, 50);

    protected override void OnInitialized() => Provider.Rows = Query.Rows(FilterDate);

    async Task OnDateChangedAsync(DateOnly? date)
    {
        FilterDate = date;
        await ReloadAsync();
    }

    async Task ClearDateAsync()
    {
        FilterDate = null;
        await ReloadAsync();
    }

    async Task ReloadAsync()
    {
        Provider.Rows = Query.Rows(FilterDate);
        if (Table is not null) await Table.RefreshAsync();
    }
}
