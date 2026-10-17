using System.Globalization;
using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Components.Pages;

/// <summary>
/// One scheduled show: its UTC times, coverage per recorder with gaps, and its timeline in the player with margins
/// either side. The show is found again from the id (station and UTC start) on every load, so a deep link works after
/// a restart or a reload.
/// </summary>
public sealed partial class ShowPage
{
    [Inject] public required ScheduleService Schedule { get; set; }
    [Inject] public required SegmentCatalog Catalog { get; set; }
    [Inject] public required ClipService Clips { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }
    [Inject] public required ILogger<ShowPage> Log { get; set; }

    [Parameter] public string? Id { get; set; }

    [SupplyParameterFromQuery(Name = "margin")] public string? QueryMargin { get; set; }
    [SupplyParameterFromQuery(Name = "recorder")] public string? QueryRecorder { get; set; }

    ScheduleSnapshot Snapshot { get; set; } = ScheduleSnapshot.Empty;
    ShowOccurrence? Show { get; set; }
    ShowCoverageResult? Coverage { get; set; }
    PreparedClip? Clip { get; set; }
    IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> ClipTimeline { get; set; } = [];
    ShowTimeline? Timeline { get; set; }
    List<string> Warnings { get; } = [];
    string? Error { get; set; }
    bool Busy { get; set; }
    int MarginMinutes { get; set; } = 5;
    string Recorder { get; set; } = "auto";
    string? _loadedFor;

    List<Player.RecorderChoice> RecorderChoices =>
        [new("auto", "Auto (best coverage)"), .. Settings.Recorders.Select(r => new Player.RecorderChoice(r.Id, "Recorder " + r.Label))];

    Player.RecorderChoice SelectedRecorder => RecorderChoices.FirstOrDefault(c => c.Id == Recorder) ?? RecorderChoices[0];

    void OnRecorderChanged(Player.RecorderChoice? choice) => Recorder = choice?.Id ?? "auto";

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
        var key = $"{Id}|{QueryMargin}|{QueryRecorder}";
        if (key == _loadedFor) return;
        _loadedFor = key;

        MarginMinutes = int.TryParse(QueryMargin, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : Settings.DefaultMarginMinutes;
        Recorder = string.IsNullOrWhiteSpace(QueryRecorder) ? "auto" : QueryRecorder.Trim().ToLowerInvariant();
        Snapshot = await Schedule.WaitForScheduleAsync(TimeSpan.FromSeconds(10));
        Show = Schedule.Find(Id);
        Clip = null;
        Timeline = null;
        if (Show is null) return;

        Coverage = ShowCoverage.Classify(Show, Catalog.All(), Time.GetUtcNow());
        if (Coverage.State != ShowState.Future) await PrepareAsync();
    }

    async Task PrepareAsync()
    {
        if (Show is null) return;
        Busy = true;
        Error = null;
        Warnings.Clear();
        try
        {
            var now = Time.GetUtcNow();
            Coverage = ShowCoverage.Classify(Show, Catalog.All(), now);
            var request = ShowTimeline.Request(Show, MarginMinutes, Recorder, now);
            // A clip already in the cache (always, after the prerender built it) is shown in this same render, so the page
            // does not draw the busy overlay without a player and then the player; only a new clip is built off the render thread.
            var clip = Clips.TryPrepareCached(null, request) ?? await Task.Run(() => Clips.Prepare(null, request));
            if (Coverage.State == ShowState.Partial)
                Warnings.Add("Only part of this show was recorded; the player plays what exists, joined across the gaps listed above.");
            else if (Coverage.NeedsBoth)
                Warnings.Add("Between them the recorders have all of this show, but no single recorder does. The player uses one recorder, so it has that recorder's gaps.");
            Warnings.AddRange(clip.Plan.Warnings);
            Timeline = ShowTimeline.Build(Show, clip, now);
            ClipTimeline = clip.Timeline();
            Clip = clip;
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
            Error = "The clip could not be built as whole MP3 frames: " + ex.Message;
            Log.LogError(ex, "Clip integrity failure for show {Id}", Id);
        }
        finally
        {
            Busy = false;
        }
    }

    static string Covered(RecorderCoverage r) =>
        string.Create(CultureInfo.InvariantCulture, $"{Clockface(r.CoveredSeconds)} of {Clockface(r.WindowSeconds)} ({r.Fraction:0.0%})");

    static string Covered(CombinedCoverage c) =>
        string.Create(CultureInfo.InvariantCulture, $"{Clockface(c.CoveredSeconds)} of {Clockface(c.WindowSeconds)} ({c.Fraction:0.0%})");

    static string Clockface(double seconds) => TimeSpan.FromSeconds(Math.Round(seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    static string Seconds(double s) => s.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    static string Utc(DateTimeOffset t) => ClipPlanner.Utc(t);
}
