using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Web.Analysis;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Schedule;

/// <summary>
/// One row of the show page's events pane: a margin, the show, a recording gap or restart, a talk section, a detected
/// event or a song. Start and End are positions in the clip; a point (a gap, a restart) has End equal to Start.
/// </summary>
public sealed record ShowEvent(
    string Id,
    string Kind,
    string Label,
    string? Detail,
    TimeSpan Start,
    TimeSpan End,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    AnalysisEvent? Source)
{
    /// <summary>The margins and the show span everything inside them, so any other row there is the more specific one.</summary>
    public bool IsContainer => Kind is ClipMarkers.Show or ClipMarkers.Margin;

    public bool IsSong => Kind == EventTypes.Song;

    /// <summary>A talk section, a song or a container starts where it should be heard from; anything else is played with a lead-in.</summary>
    public bool IsSpan => IsContainer || IsSong || Kind == AnalysisMarkers.Talk;

    public bool Contains(TimeSpan position) => position >= Start && (position < End || position == Start);
}

/// <summary>A transcript line placed in the clip.</summary>
public sealed record ShowLine(int Index, TimeSpan Start, TimeSpan End, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Text);

/// <summary>
/// The show page's events pane and transcript, worked out from the show's timeline, the analysis events and the
/// transcript lines. Positions come from UTC through the clip's clock, so they share its accuracy (about a second), and
/// anything outside the clip's audio is left out. Review-only swear hits are never listed.
/// </summary>
public static class ShowEvents
{
    /// <summary>Transcript lines closer together than this are one talk section.</summary>
    public static readonly TimeSpan TalkJoin = TimeSpan.FromSeconds(15);

    const int LabelLength = 80;

    public static readonly IReadOnlyDictionary<string, string> KindLabels = Merge(ClipMarkers.KindLabels, AnalysisMarkers.KindLabels);

    public static readonly IReadOnlyDictionary<string, string> KindColors = Merge(ClipMarkers.KindColors, AnalysisMarkers.KindColors);

    public static string KindLabel(string kind) => KindLabels.GetValueOrDefault(kind, kind);

    public static string KindColor(string kind) => KindColors.GetValueOrDefault(kind, "var(--bs-secondary)");

    public static IReadOnlyList<ShowLine> Lines(IEnumerable<TimedLine> lines, ClipClock clock)
    {
        if (Window(clock) is not { } w) return [];
        return lines
            .Where(l => l.EndUtc > w.From && l.StartUtc < w.To)
            .OrderBy(l => l.StartUtc)
            .Select((l, i) => new ShowLine(i, clock.ToClip(l.StartUtc), clock.ToClip(l.EndUtc), l.StartUtc, l.EndUtc, l.Text))
            .ToList();
    }

    public static IReadOnlyList<ShowEvent> Build(ShowTimeline timeline, ClipClock clock, IEnumerable<AnalysisEvent> events, IReadOnlyList<ShowLine> lines)
    {
        if (Window(clock) is not { } w) return [];
        DateTimeOffset Utc(TimeSpan p) => clock.ToUtc(p) ?? w.From;

        var list = timeline.Segments
            .Select(s => new ShowEvent(s.Id, s.Kind, s.Label ?? KindLabel(s.Kind), s.Detail, s.Start, s.End, Utc(s.Start), Utc(s.End), null))
            .Concat(timeline.Markers
                .Where(m => m.Kind is ClipMarkers.Gap or ClipMarkers.Restart)
                .Select(m => new ShowEvent(m.Id, m.Kind, m.Label ?? KindLabel(m.Kind), m.Detail, m.Position, m.Position, Utc(m.Position), Utc(m.Position), null)))
            .Concat(events
                .Where(e => !e.IsReviewOnly && e.EndUtc >= w.From && e.StartUtc <= w.To)
                .Select(e => new ShowEvent("event-" + e.Id, e.Type, e.Label ?? KindLabel(e.Type), null,
                    clock.ToClip(e.StartUtc), clock.ToClip(e.EndUtc), e.StartUtc, e.EndUtc, e)))
            .Concat(TalkSections(lines))
            .ToList();

        return list
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.IsContainer)
            .ThenByDescending(e => e.End)
            .ToList();
    }

    /// <summary>The most specific row playing at a position: the shortest one that contains it, else the margin or show around it.</summary>
    public static ShowEvent? At(IReadOnlyList<ShowEvent> events, TimeSpan position) =>
        events.Where(e => !e.IsContainer && e.Contains(position)).OrderBy(e => e.End - e.Start).FirstOrDefault()
        ?? events.FirstOrDefault(e => e.IsContainer && e.Contains(position));

    /// <summary>The line playing at a position, or null between lines.</summary>
    public static ShowLine? LineAt(IReadOnlyList<ShowLine> lines, TimeSpan position) =>
        lines.LastOrDefault(l => l.Start <= position && position < l.End);

    /// <summary>
    /// The transcript lines a selected row covers: those overlapping it, or for a detected event with none, the nearest
    /// line. A margin or the show highlights nothing (it would be all of it).
    /// </summary>
    public static IReadOnlySet<int> LinesFor(ShowEvent? selected, IReadOnlyList<ShowLine> lines)
    {
        if (selected is null || selected.IsContainer || lines.Count == 0) return new HashSet<int>();
        var overlapping = lines
            .Where(l => selected.End > selected.Start ? l.Start < selected.End && l.End > selected.Start : l.Start <= selected.Start && selected.Start <= l.End)
            .Select(l => l.Index)
            .ToHashSet();
        if (overlapping.Count > 0 || selected.IsSong) return overlapping;
        return new HashSet<int> { lines.MinBy(l => (l.Start - selected.Start).Duration())!.Index };
    }

    static IEnumerable<ShowEvent> TalkSections(IReadOnlyList<ShowLine> lines)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var first = lines[i];
            var last = first;
            while (i + 1 < lines.Count && lines[i + 1].Start - last.End <= TalkJoin) last = lines[++i];
            i++;
            var count = last.Index - first.Index + 1;
            yield return new ShowEvent(
                "talk-" + first.Index, AnalysisMarkers.Talk, Shorten(first.Text), count == 1 ? "1 line" : $"{count} lines",
                first.Start, last.End, first.StartUtc, last.EndUtc, null);
        }
    }

    static string Shorten(string text) => text.Length <= LabelLength ? text : text[..LabelLength] + "...";

    /// <summary>The UTC span of the clip's audio, or null when the clock has no timeline.</summary>
    static (DateTimeOffset From, DateTimeOffset To)? Window(ClipClock clock) =>
        clock.ToUtc(TimeSpan.Zero) is { } from && clock.ToUtc(TimeSpan.FromSeconds(clock.Seconds)) is { } to ? (from, to) : null;

    static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>[] maps)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in maps)
            foreach (var (k, v) in map)
                merged[k] = v;
        return merged;
    }
}
