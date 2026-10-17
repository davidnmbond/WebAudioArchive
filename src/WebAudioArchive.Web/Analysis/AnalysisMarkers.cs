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
        [EventTypes.DeadAir] = "var(--bs-dark)",
        [EventTypes.Song] = "var(--bs-primary)",
        [Talk] = "var(--bs-info)",
    };

    public static IReadOnlyList<PDMediaMarker> Markers(IEnumerable<AnalysisEvent> events, ClipClock clock) => events
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
        var list = events
            .Where(e => e.Type == EventTypes.DeadAir)
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
        foreach (var l in lines)
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
