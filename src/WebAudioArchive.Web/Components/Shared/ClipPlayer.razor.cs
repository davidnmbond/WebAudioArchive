using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class ClipPlayer : IAsyncDisposable
{
    public static readonly double[] Speeds = [0.75, 1, 1.25, 1.5, 2];

    [Inject] public required IJSRuntime Js { get; set; }

    [Parameter, EditorRequired] public required string Src { get; set; }
    [Parameter, EditorRequired] public required string DownloadUrl { get; set; }
    [Parameter, EditorRequired] public required string DownloadName { get; set; }
    [Parameter] public double Seconds { get; set; }

    /// <summary>Where each joined piece starts in the clip and its UTC wall time, so the shown wall time follows jumps.</summary>
    [Parameter, EditorRequired] public required IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> Timeline { get; set; }

    ElementReference _root;
    IJSObjectReference? _module;
    string? _initialisedFor;

    string TimelineJson => JsonSerializer.Serialize(Timeline.Select(t => new { at = t.ClipSecond, utc = t.Utc.ToUnixTimeMilliseconds() }));

    string StartText => Timeline.Count == 0 ? "-" : Timeline[0].Utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    string DurationText => TimeSpan.FromSeconds(Math.Round(Seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_initialisedFor == Src) return;
        _module ??= await Js.InvokeAsync<IJSObjectReference>("import", "./js/clipplayer.js");
        await _module.InvokeVoidAsync("init", _root);
        _initialisedFor = Src;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
