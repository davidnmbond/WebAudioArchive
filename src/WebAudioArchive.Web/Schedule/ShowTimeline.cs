using PanoramicData.Blazor.Models;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Schedule;

/// <summary>
/// The show page's plan: a clip of the show with a margin either side (5 minutes by default), the show proper as the
/// player's shaded range and as a "show" segment between two "margin" segments, markers at the show's start and end,
/// and the recording gaps and recorder restarts inside the clip. Positions are seconds into the clip, worked out from
/// UTC times through the clip's timeline.
/// </summary>
public sealed record ShowTimeline(
    TimeSpan? RangeStart,
    TimeSpan? RangeEnd,
    IReadOnlyList<PDMediaMarker> Markers,
    IReadOnlyList<PDMediaSegment> Segments,
    IReadOnlyList<string> Notes)
{
    public const int MaxMarginMinutes = 60;

    /// <summary>
    /// The clip request for a show: its start and end (or now, while on air), with the margin either side. Throws
    /// <see cref="ClipException"/> for a show that has not started yet.
    /// </summary>
    public static ClipRequest Request(ShowOccurrence show, int marginMinutes, string recorder, DateTimeOffset now)
    {
        if (now <= show.StartUtc) throw new ClipException(400, $"{show.Title} has not been broadcast yet (it starts at {ClipPlanner.Utc(show.StartUtc)}).");
        if (marginMinutes is < 0 or > MaxMarginMinutes) throw new ClipException(400, $"The margin must be from 0 to {MaxMarginMinutes} minutes.");
        var margin = TimeSpan.FromMinutes(marginMinutes);
        var to = now < show.EndUtc ? now : show.EndUtc;
        return new ClipRequest(show.StartUtc, to, margin, margin, string.IsNullOrWhiteSpace(recorder) ? "auto" : recorder);
    }

    public static ShowTimeline Build(ShowOccurrence show, PreparedClip clip, DateTimeOffset now)
    {
        var clock = clip.Clock();
        var duration = TimeSpan.FromSeconds(clip.File.Seconds);
        var plan = clip.Plan;
        var notes = new List<string>();

        var startsInClip = show.StartUtc >= plan.StartUtc;
        var endsInClip = show.EndUtc <= plan.EndUtc;
        TimeSpan? rangeStart = startsInClip ? clock.ToClip(show.StartUtc) : null;
        TimeSpan? rangeEnd = endsInClip ? clock.ToClip(show.EndUtc) : null;
        if (!startsInClip) notes.Add($"The recording starts after the show began ({ClipPlanner.Utc(plan.StartUtc)}), so there is no lead-in.");
        if (!endsInClip && now < show.EndUtc) notes.Add($"The show is still on air: the clip ends at {ClipPlanner.Utc(plan.EndUtc)}. Prepare it again later for more.");
        else if (!endsInClip) notes.Add($"The recording ends before the show ended ({ClipPlanner.Utc(plan.EndUtc)}), so there is no tail.");

        var markers = new List<PDMediaMarker>();
        if (rangeStart is not null) markers.Add(new PDMediaMarker { Id = "show-start", Position = rangeStart.Value, Kind = ClipMarkers.Show, Label = "Show starts " + ClipMarkers.Time(show.StartUtc), Detail = show.Title });
        if (rangeEnd is not null) markers.Add(new PDMediaMarker { Id = "show-end", Position = rangeEnd.Value, Kind = ClipMarkers.Show, Label = "Show ends " + ClipMarkers.Time(show.EndUtc), Detail = show.Title });
        markers.AddRange(ClipMarkers.GapsAndRestarts(clip, clock));

        var showStart = rangeStart ?? TimeSpan.Zero;
        var showEnd = rangeEnd ?? duration;
        var segments = new List<PDMediaSegment>();
        if (showStart > TimeSpan.Zero)
            segments.Add(new PDMediaSegment { Id = "margin-before", Start = TimeSpan.Zero, End = showStart, Kind = ClipMarkers.Margin, Label = "Lead-in", Detail = $"{ClipMarkers.Time(plan.StartUtc)} to {ClipMarkers.Time(show.StartUtc)}" });
        segments.Add(new PDMediaSegment { Id = "show", Start = showStart, End = showEnd, Kind = ClipMarkers.Show, Label = show.Title, Detail = $"{ClipMarkers.Time(show.StartUtc)} to {ClipMarkers.Time(show.EndUtc)}" });
        if (showEnd < duration)
            segments.Add(new PDMediaSegment { Id = "margin-after", Start = showEnd, End = duration, Kind = ClipMarkers.Margin, Label = "Tail", Detail = $"{ClipMarkers.Time(show.EndUtc)} to {ClipMarkers.Time(plan.EndUtc)}" });

        return new ShowTimeline(rangeStart, rangeEnd, markers, segments, notes);
    }
}
