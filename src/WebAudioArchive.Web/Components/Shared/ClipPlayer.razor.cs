using System.Globalization;
using Microsoft.AspNetCore.Components;
using PanoramicData.Blazor.Models;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class ClipPlayer
{
    public static readonly IReadOnlyList<double> Speeds = [0.75, 1, 1.25, 1.5, 2];

    [Inject] public required ILogger<ClipPlayer> Log { get; set; }

    [Parameter, EditorRequired] public required string Src { get; set; }
    [Parameter, EditorRequired] public required string DownloadUrl { get; set; }
    [Parameter, EditorRequired] public required string DownloadName { get; set; }
    [Parameter] public double Seconds { get; set; }
    [Parameter] public string? Title { get; set; }

    /// <summary>Where each joined piece starts in the clip and its UTC wall time, so the shown wall time follows jumps.</summary>
    [Parameter, EditorRequired] public required IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> Timeline { get; set; }

    [Parameter] public IReadOnlyList<PDMediaMarker>? Markers { get; set; }
    [Parameter] public IReadOnlyList<PDMediaSegment>? Segments { get; set; }
    [Parameter] public TimeSpan? RangeStart { get; set; }
    [Parameter] public TimeSpan? RangeEnd { get; set; }
    [Parameter] public bool MarkerListExpanded { get; set; } = true;

    TimeSpan Position { get; set; }
    string? _shownSrc;

    ClipClock Clock => new(Timeline, Seconds);

    string WallText => Clock.ToUtc(Position) is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";

    string PositionText => Clockface(Position.TotalSeconds);

    string DurationText => Clockface(Seconds);

    static string Clockface(double seconds) => TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds))).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    protected override void OnParametersSet()
    {
        if (_shownSrc == Src) return;
        _shownSrc = Src;
        Position = TimeSpan.Zero;
    }

    void OnTimeUpdate(TimeSpan position) => Position = position;


    // The player shows the error itself (ShowErrors); it is also logged here for the operator.
    void OnError(MediaPlayerError error) => Log.LogWarning("Clip {Src} could not be played: {Kind} {Message}", Src, error.Kind, error.Message);
}
