using System.Globalization;
using Microsoft.AspNetCore.Components;
using PanoramicData.Blazor;
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
    /// <summary>Where to start when the clip has loaded (a deep link to a moment).</summary>
    [Parameter] public TimeSpan? StartAt { get; set; }

    /// <summary>Called on every time update. A plain callback, not an EventCallback, so the parent re-renders only when it decides to.</summary>
    [Parameter] public Action<TimeSpan>? PositionChanged { get; set; }

    TimeSpan Position { get; set; }
    string? _shownSrc;
    PDMediaPlayer? _player;

    public Task SeekAsync(TimeSpan position) => _player?.SeekAsync(position) ?? Task.CompletedTask;

    ClipClock Clock => new(Timeline, Seconds);

    string WallText => Clock.ToUtc(Position) is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";

    string PositionText => Clockface(Position.TotalSeconds);

    string DurationText => Clockface(Seconds);

    static string Clockface(double seconds) => TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds))).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    // The lists the player is given: the parameters, with the selected item's copy swapped in. Rebuilt only when a
    // parameter list or the selection changes, so ordinary re-renders hand the player the same instances.
    IReadOnlyList<PDMediaSegment>? _sourceSegments, ShownSegments;
    IReadOnlyList<PDMediaMarker>? _sourceMarkers, ShownMarkers;
    string? _selectedSegmentId, _selectedMarkerId;

    void OnSegmentSelected(PDMediaSegment segment)
    {
        if (_selectedSegmentId == segment.Id) return;
        _selectedSegmentId = segment.Id;
        ShownSegments = SelectedMarks.Mark(_sourceSegments, _selectedSegmentId);
    }

    void OnMarkerSelected(PDMediaMarker marker)
    {
        if (_selectedMarkerId == marker.Id) return;
        _selectedMarkerId = marker.Id;
        ShownMarkers = SelectedMarks.Mark(_sourceMarkers, _selectedMarkerId);
    }

    protected override void OnParametersSet()
    {
        // A new list (another show or clip) clears the selection.
        if (!ReferenceEquals(Segments, _sourceSegments))
        {
            _sourceSegments = Segments;
            _selectedSegmentId = null;
            ShownSegments = Segments;
        }

        if (!ReferenceEquals(Markers, _sourceMarkers))
        {
            _sourceMarkers = Markers;
            _selectedMarkerId = null;
            ShownMarkers = Markers;
        }

        if (_shownSrc == Src) return;
        _shownSrc = Src;
        Position = TimeSpan.Zero;
    }

    void OnTimeUpdate(TimeSpan position)
    {
        Position = position;
        PositionChanged?.Invoke(position);
    }


    // The player shows the error itself (ShowErrors); it is also logged here for the operator.
    void OnError(MediaPlayerError error) => Log.LogWarning("Clip {Src} could not be played: {Kind} {Message}", Src, error.Kind, error.Message);
}
