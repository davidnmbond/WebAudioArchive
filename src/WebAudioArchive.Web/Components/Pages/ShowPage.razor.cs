using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PanoramicData.Blazor.Models;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Web.Analysis;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Components.Shared;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Components.Pages;

/// <summary>
/// One scheduled show (or off-air stretch): a toolbar with its UTC times, coverage and timeline settings, the player
/// with margins either side, and a splitter with the events (margins, the show, gaps, talk, detected events and songs)
/// on the left and the transcript, or the selected song's details, on the right. The show is found again from the id
/// (station and UTC start) on every load, so a deep link works after a restart or a reload; <c>at</c> selects the
/// moment playing at that UTC time and starts the player just before it.
/// </summary>
public sealed partial class ShowPage
{
    [Inject] public required ScheduleService Schedule { get; set; }
    [Inject] public required SegmentCatalog Catalog { get; set; }
    [Inject] public required ClipService Clips { get; set; }
    [Inject] public required AnalysisReader Analysis { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }
    [Inject] public required IJSRuntime Js { get; set; }
    [Inject] public required ILogger<ShowPage> Log { get; set; }

    [Parameter] public string? Id { get; set; }

    [SupplyParameterFromQuery(Name = "margin")] public string? QueryMargin { get; set; }
    [SupplyParameterFromQuery(Name = "recorder")] public string? QueryRecorder { get; set; }
    [SupplyParameterFromQuery(Name = "at")] public string? QueryAt { get; set; }

    /// <summary>A recorder choice for the combo box: auto, or one configured recorder.</summary>
    public sealed record RecorderChoice(string Id, string Text)
    {
        public static bool Matches(RecorderChoice c, string search) =>
            string.IsNullOrWhiteSpace(search) || c.Text.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    ScheduleSnapshot Snapshot { get; set; } = ScheduleSnapshot.Empty;
    ShowOccurrence? Show { get; set; }
    ShowCoverageResult? Coverage { get; set; }
    PreparedClip? Clip { get; set; }
    IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> ClipTimeline { get; set; } = [];
    ShowTimeline? Timeline { get; set; }
    IReadOnlyList<PDMediaMarker> Markers { get; set; } = [];
    IReadOnlyList<ShowEvent> Events { get; set; } = [];
    IReadOnlyList<ShowLine> Lines { get; set; } = [];
    ShowEvent? Selected { get; set; }
    IReadOnlySet<int> SelectedLines { get; set; } = new HashSet<int>();
    string? AnalysisNote { get; set; }
    TimeSpan? StartAt { get; set; }
    List<string> Warnings { get; } = [];
    string? Error { get; set; }
    int MarginMinutes { get; set; } = 5;
    string Recorder { get; set; } = "auto";
    DateTimeOffset? At { get; set; }
    string? _loadedFor;
    ClipPlayer? _player;
    string? _playingId;
    int? _playingLine;
    string? _scrollTo;

    List<RecorderChoice> RecorderChoices =>
        [new("auto", "Auto (best coverage)"), .. (Settings.MergedRoot is null ? Enumerable.Empty<RecorderChoice>() : [new RecorderChoice(WebSettings.MergedId, "The merged copy")]), .. Settings.Recorders.Select(r => new RecorderChoice(r.Id, "Recorder " + r.Label))];

    RecorderChoice SelectedRecorder => RecorderChoices.FirstOrDefault(c => c.Id == Recorder) ?? RecorderChoices[0];

    void OnRecorderChanged(RecorderChoice? choice) => Recorder = choice?.Id ?? "auto";

    string MarginText => MarginMinutes.ToString(CultureInfo.InvariantCulture);

    void OnMarginChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) MarginMinutes = m;
    }

    string NotFoundText => ShowOccurrence.TryParseId(Id, out var station, out var start)
        ? $"No scheduled show of station '{station}' starts at {Utc(start)} in the current schedule."
        : $"'{Id}' is not a show id (station-yyyyMMddTHHmmZ, the UTC start).";

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{Id}|{QueryMargin}|{QueryRecorder}|{QueryAt}";
        if (key == _loadedFor) return;
        _loadedFor = key;

        MarginMinutes = int.TryParse(QueryMargin, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : Settings.DefaultMarginMinutes;
        Recorder = string.IsNullOrWhiteSpace(QueryRecorder) ? "auto" : QueryRecorder.Trim().ToLowerInvariant();
        At = QueryAt is { } a && a.EndsWith('Z') && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        Snapshot = await Schedule.WaitForScheduleAsync(TimeSpan.FromSeconds(10));
        Show = Schedule.Find(Id);
        Clip = null;
        Timeline = null;
        if (Show is null) return;

        Coverage = ShowCoverage.Classify(Show, Catalog.All(), Time.GetUtcNow());
        if (Coverage.State != ShowState.Future) Prepare();
    }

    void Prepare()
    {
        if (Show is null) return;
        Error = null;
        Warnings.Clear();
        try
        {
            var now = Time.GetUtcNow();
            Coverage = ShowCoverage.Classify(Show, Catalog.All(), now);
            var request = ShowTimeline.Request(Show, MarginMinutes, Recorder, now);
            // Nothing is copied (the clip is streamed from the segments when played), so this is quick enough for the render.
            var clip = Clips.Prepare(null, request);
            if (Coverage.State == ShowState.Partial)
                Warnings.Add("Only part of this show was recorded; the player plays what exists, joined across the gaps.");
            else if (Coverage.NeedsBoth)
                Warnings.Add("Between them the recorders have all of this show, but no single recorder does. The player uses one recorder, so it has that recorder's gaps.");
            Warnings.AddRange(clip.Plan.Warnings);
            Timeline = ShowTimeline.Build(Show, clip, now);
            ClipTimeline = clip.Timeline();
            Clip = clip;
            LoadAnalysis(clip);
        }
        catch (ClipException ex)
        {
            Clip = null;
            Timeline = null;
            Error = ex.StatusCode == 404 ? "Nothing of this show was recorded. " + ex.Message : ex.Message;
        }
        catch (ClipIntegrityException ex)
        {
            Clip = null;
            Timeline = null;
            Error = "The clip is not whole MP3 frames at a join between segments: " + ex.Message;
            Log.LogError(ex, "Clip integrity failure for show {Id}", Id);
        }
    }

    /// <summary>The events and transcript inside the clip. Analysis that is missing or unreadable leaves the show's own rows and a note.</summary>
    void LoadAnalysis(PreparedClip clip)
    {
        var clock = clip.Clock();
        IReadOnlyList<AnalysisEvent> events = [];
        IReadOnlyList<TimedLine> lines = [];
        AnalysisNote = null;
        try
        {
            events = Analysis.Events(clip.Plan.StartUtc, clip.Plan.EndUtc, null);
            lines = Analysis.Transcript(clip.Plan.StartUtc, clip.Plan.EndUtc);
            if (lines.Count == 0) AnalysisNote = "No transcript for this clip yet.";
        }
        catch (AnalysisQueryException ex)
        {
            AnalysisNote = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnalysisNote = "The analysis files could not be read.";
            Log.LogWarning(ex, "Analysis for show {Id} could not be read", Id);
        }

        Lines = ShowEvents.Lines(lines, clock);
        Events = ShowEvents.Build(Timeline!, clock, events, Lines);
        Markers = [.. Timeline!.Markers, .. AnalysisMarkers.Markers(events.Where(e => !e.IsReviewOnly), clock)];
        if (At is { } at)
        {
            var position = clock.ToClip(at);
            Select(ShowEvents.At(Events, position));
            StartAt = Lead(position);
        }
        else
        {
            Select(null);
            StartAt = null;
        }
    }

    void Select(ShowEvent? e)
    {
        Selected = e;
        SelectedLines = ShowEvents.LinesFor(e, Lines);
        _scrollTo = e is null ? null : SelectedLines.Count > 0 ? $"#show-transcript [data-line=\"{SelectedLines.Min()}\"]" : null;
    }

    void ClearSelection() => Select(null);

    async Task SelectAsync(ShowEvent e)
    {
        Select(e);
        await SeekAsync(e.IsSpan ? e.Start : Lead(e.Start));
    }

    Task SeekAsync(TimeSpan position) => _player?.SeekAsync(position) ?? Task.CompletedTask;

    static TimeSpan Lead(TimeSpan position) =>
        position > TimeSpan.FromSeconds(AnalysisEvent.LinkPaddingSeconds) ? position - TimeSpan.FromSeconds(AnalysisEvent.LinkPaddingSeconds) : TimeSpan.Zero;

    /// <summary>Marks the row and line playing now; re-renders only when either changes, not on every time update.</summary>
    void OnPosition(TimeSpan position)
    {
        var playing = ShowEvents.At(Events, position)?.Id;
        var line = ShowEvents.LineAt(Lines, position)?.Index;
        if (playing == _playingId && line == _playingLine) return;
        _playingId = playing;
        if (line is not null && line != _playingLine && Selected is not { IsSong: true }) _scrollTo = $"#show-transcript [data-line=\"{line}\"]";
        _playingLine = line;
        _ = InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_scrollTo is not { } target) return;
        _scrollTo = null;
        try
        {
            await Js.InvokeVoidAsync("waaShow.reveal", target);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerendering: there is no browser yet; the selected line is revealed by the first interactive render.
            _scrollTo = target;
        }
    }

    string EventCss(ShowEvent e) =>
        "show-event" + (e.Id == Selected?.Id ? " selected" : "") + (e.Id == _playingId ? " playing" : "") + (e.IsContainer ? " container" : "");

    string LineCss(ShowLine l) =>
        "show-line" + (SelectedLines.Contains(l.Index) ? " selected" : "") + (l.Index == _playingLine ? " playing" : "");

    static string? Evidence(ShowEvent e, params string[] keys) =>
        keys.Select(k => e.Source?.Evidence?.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    static string Clock(TimeSpan t) => TimeSpan.FromSeconds(Math.Floor(Math.Max(0, t.TotalSeconds))).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    static string Covered(RecorderCoverage r) =>
        string.Create(CultureInfo.InvariantCulture, $"{Clockface(r.CoveredSeconds)} of {Clockface(r.WindowSeconds)} ({r.Fraction:0.0%})");

    static string Covered(CombinedCoverage c) =>
        string.Create(CultureInfo.InvariantCulture, $"{Clockface(c.CoveredSeconds)} of {Clockface(c.WindowSeconds)} ({c.Fraction:0.0%})");

    static string Clockface(double seconds) => TimeSpan.FromSeconds(Math.Round(seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    static string Seconds(double s) => s.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    static string Utc(DateTimeOffset t) => ClipPlanner.Utc(t);
}
