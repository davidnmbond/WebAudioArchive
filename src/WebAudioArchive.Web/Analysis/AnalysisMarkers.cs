using System.Globalization;
using PanoramicData.Blazor.Models;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Analysis;

/// <summary>
/// Analysis results as PDMediaPlayer markers and segments for a clip (wired into the pages in plan 4). Positions come
/// from UTC through the clip's clock, so they share the clip's accuracy (about a second). Every label says detected.
/// A swear hit that detection set aside as probable hallucination is its own kind, <see cref="SwearReview"/>, never a swear.
/// </summary>
public static class AnalysisMarkers
{
    public const string Talk = "talk";

    /// <summary>The marker kind for a suppressed swear hit: probably repeated or invented text, for review only.</summary>
    public const string SwearReview = "swear-review";

    public static readonly IReadOnlyDictionary<string, string> KindLabels = new Dictionary<string, string>
    {
        [EventTypes.Swear] = "Swearing (detected)",
        [SwearReview] = "Possible swearing, probably invented text (review only)",
        [EventTypes.Clipping] = "Clipping (detected)",
        [EventTypes.Filler] = "Filler word (estimate)",
        [EventTypes.DeadAir] = "Dead air (detected)",
        [EventTypes.Song] = "Song",
        [Talk] = "Talk (transcript)",
    };

    public static readonly IReadOnlyDictionary<string, string> KindColors = new Dictionary<string, string>
    {
        [EventTypes.Swear] = "var(--bs-danger)",
        [SwearReview] = "var(--bs-gray-500)",
        [EventTypes.Clipping] = "var(--bs-warning)",
        [EventTypes.Filler] = "var(--bs-secondary)",
        // The body text colour, so it shows in both themes (black on light, white on dark).
        [EventTypes.DeadAir] = "var(--bs-emphasis-color)",
        [EventTypes.Song] = "var(--bs-primary)",
        [Talk] = "var(--bs-info)",
    };

    /// <summary>The UTC span of the clip's audio, or null when the clock has no timeline. Anything outside it is dropped: the clock would clamp it to the clip's edges and draw it there.</summary>
    static (DateTimeOffset From, DateTimeOffset To)? Window(ClipClock clock) =>
        clock.ToUtc(TimeSpan.Zero) is { } from && clock.ToUtc(TimeSpan.FromSeconds(clock.Seconds)) is { } to ? (from, to) : null;

    public static IReadOnlyList<PDMediaMarker> Markers(IEnumerable<AnalysisEvent> events, ClipClock clock) =>
        Window(clock) is not { } w ? [] : events
        .Where(e => e.StartUtc >= w.From && e.StartUtc <= w.To)
        .Select(e => new PDMediaMarker
        {
            Id = "event-" + e.Id,
            Position = clock.ToClip(e.StartUtc),
            Kind = e.IsReviewOnly ? SwearReview : e.Type,
            Label = $"{e.Label ?? e.Type} at {ClipMarkers.Time(e.StartUtc)}",
            Detail = string.Create(CultureInfo.InvariantCulture, $"{e.Severity}, confidence {e.Confidence:0.00}, {e.Source} ({e.Detector} {e.DetectorVersion}). Detected, not proven: listen to confirm.")
                + (e.IsReviewOnly ? " Probably repeated or invented text: review only, not counted as swearing." : ""),
        })
        .ToList();

    public static IReadOnlyList<PDMediaSegment> Segments(IEnumerable<AnalysisEvent> events, IEnumerable<TimedLine> lines, ClipClock clock)
    {
        if (Window(clock) is not { } w) return [];
        var list = events
            .Where(e => e.Type == EventTypes.DeadAir && e.EndUtc > w.From && e.StartUtc < w.To)
            .Select(e => new PDMediaSegment
            {
                Id = "dead-" + e.Id,
                Start = clock.ToClip(e.StartUtc),
                End = clock.ToClip(e.EndUtc),
                Kind = EventTypes.DeadAir,
                Label = e.Label ?? "Dead air",
                Detail = $"{ClipMarkers.Time(e.StartUtc)} to {ClipMarkers.Time(e.EndUtc)}",
            })
            .ToList();
        var i = 0;
        foreach (var l in lines.Where(l => l.EndUtc > w.From && l.StartUtc < w.To))
        {
            list.Add(new PDMediaSegment
            {
                Id = "talk-" + i++,
                Start = clock.ToClip(l.StartUtc),
                End = clock.ToClip(l.EndUtc),
                Kind = Talk,
                Label = l.Text.Length <= 80 ? l.Text : l.Text[..80] + "...",
                Detail = $"{ClipMarkers.Time(l.StartUtc)}: {l.Text}",
            });
        }

        return list;
    }
}
