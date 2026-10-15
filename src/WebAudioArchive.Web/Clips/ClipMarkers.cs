using System.Globalization;
using PanoramicData.Blazor.Models;

namespace WebAudioArchive.Web.Clips;

/// <summary>
/// The player's markers, from real data only: the requested window's start and end, the recording gaps and the
/// recorder restarts inside the clip. Positions are seconds into the clip, worked out from UTC times.
/// </summary>
public static class ClipMarkers
{
    public const string Window = "window";
    public const string Gap = "gap";
    public const string Restart = "restart";
    public const string Show = "show";
    public const string Margin = "margin";

    public static readonly IReadOnlyDictionary<string, string> KindLabels = new Dictionary<string, string>
    {
        [Window] = "Requested window",
        [Gap] = "Recording gap",
        [Restart] = "Recorder restart",
        [Show] = "Show",
        [Margin] = "Margin",
    };

    // Bootstrap's own colour variables, so both themes keep their contrast.
    public static readonly IReadOnlyDictionary<string, string> KindColors = new Dictionary<string, string>
    {
        [Window] = "var(--bs-info)",
        [Gap] = "var(--bs-danger)",
        [Restart] = "var(--bs-warning)",
        [Show] = "var(--bs-success)",
        [Margin] = "var(--bs-secondary)",
    };

    /// <summary>The Player page's markers: requested start and end (inside any margins), gaps and restarts.</summary>
    public static IReadOnlyList<PDMediaMarker> ForPlayer(PreparedClip clip)
    {
        var clock = clip.Clock();
        var list = new List<PDMediaMarker>();
        var plan = clip.Plan;
        if (plan.RequestedFrom >= plan.StartUtc)
            list.Add(Point("requested-start", clock, plan.RequestedFrom, Window, "Requested start " + Time(plan.RequestedFrom)));
        if (plan.RequestedTo <= plan.EndUtc)
            list.Add(Point("requested-end", clock, plan.RequestedTo, Window, "Requested end " + Time(plan.RequestedTo)));
        list.AddRange(GapsAndRestarts(clip, clock));
        return list;
    }

    public static IEnumerable<PDMediaMarker> GapsAndRestarts(PreparedClip clip, ClipClock clock)
    {
        var i = 0;
        foreach (var g in clip.Gaps())
        {
            yield return new PDMediaMarker
            {
                Id = "gap-" + i++,
                Position = clock.ToClip(g.StartUtc),
                Kind = Gap,
                Label = string.Create(CultureInfo.InvariantCulture, $"Gap {Time(g.StartUtc)} to {Time(g.EndUtc)} ({g.Seconds:0.0} s)"),
                Detail = $"Recorder {clip.Plan.Recorder.ToUpperInvariant()}: {g.Reason}. The audio jumps here; no silence is inserted.",
            };
        }

        i = 0;
        foreach (var r in clip.Restarts())
        {
            yield return new PDMediaMarker
            {
                Id = "restart-" + i++,
                Position = clock.ToClip(r.AtUtc),
                Kind = Restart,
                Label = "Recorder restart at " + Time(r.AtUtc),
                Detail = $"Recorder {clip.Plan.Recorder.ToUpperInvariant()}'s segment ended by {r.EndedBy}.",
            };
        }
    }

    public static PDMediaMarker Point(string id, ClipClock clock, DateTimeOffset utc, string kind, string label) =>
        new() { Id = id, Position = clock.ToClip(utc), Kind = kind, Label = label };

    /// <summary>"19:00:00 UTC".</summary>
    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
