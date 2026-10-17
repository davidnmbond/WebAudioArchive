using PanoramicData.Blazor.Models;

namespace WebAudioArchive.Web.Components.Shared;

/// <summary>
/// PDMediaPlayer highlights the segment or marker that playback is at, not the one the user clicked. These helpers
/// copy the lists with <see cref="SelectedClass"/> added to the chosen item's CssClass, which the player puts on both
/// the list row and the seek bar lane bar. Call them only when the source list or the selection changes: the player
/// compares items by reference, so a copy made on every render would redo its layout every time.
/// </summary>
public static class SelectedMarks
{
    public const string SelectedClass = "waa-selected";

    public static IReadOnlyList<PDMediaSegment>? Mark(IReadOnlyList<PDMediaSegment>? segments, string? selectedId)
    {
        if (segments is null || string.IsNullOrEmpty(selectedId)) return segments;
        return [.. segments.Select(s => s.Id == selectedId ? Selected(s) : s)];
    }

    public static IReadOnlyList<PDMediaMarker>? Mark(IReadOnlyList<PDMediaMarker>? markers, string? selectedId)
    {
        if (markers is null || string.IsNullOrEmpty(selectedId)) return markers;
        return [.. markers.Select(m => m.Id == selectedId ? Selected(m) : m)];
    }

    static string WithClass(string? existing) => string.IsNullOrEmpty(existing) ? SelectedClass : $"{existing} {SelectedClass}";

    static PDMediaSegment Selected(PDMediaSegment s) => new()
    {
        Id = s.Id, Start = s.Start, End = s.End, Kind = s.Kind, Label = s.Label, Detail = s.Detail, Text = s.Text, Lines = s.Lines,
        Lane = s.Lane, CssClass = WithClass(s.CssClass), Color = s.Color, Confidence = s.Confidence, Source = s.Source,
    };

    static PDMediaMarker Selected(PDMediaMarker m) => new()
    {
        Id = m.Id, Position = m.Position, Length = m.Length, Kind = m.Kind, Label = m.Label, Detail = m.Detail,
        CssClass = WithClass(m.CssClass), Color = m.Color,
    };
}
