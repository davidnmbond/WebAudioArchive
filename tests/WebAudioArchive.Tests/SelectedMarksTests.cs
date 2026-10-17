extern alias web;

using PanoramicData.Blazor.Models;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Components.Shared;

namespace WebAudioArchive.Tests;

/// <summary>The clicked segment or marker keeps a class of its own (#107): PDMediaPlayer only highlights the playback position.</summary>
public class SelectedMarksTests
{
    static readonly IReadOnlyList<PDMediaSegment> Segments =
    [
        new() { Id = "a", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Kind = "margin", Label = "Lead-in", CssClass = "mine", Color = "red", Detail = "d" },
        new() { Id = "b", Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Kind = "show", Label = "Show", Lane = 1, Confidence = 0.5, Source = "s" },
        new() { Id = "c", Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Kind = "margin", Label = "Tail" },
    ];

    static readonly IReadOnlyList<PDMediaMarker> Markers =
    [
        new() { Id = "m1", Position = TimeSpan.FromSeconds(1), Kind = "show", Label = "One", CssClass = "x" },
        new() { Id = "m2", Position = TimeSpan.FromSeconds(2), Kind = "show", Label = "Two", Length = TimeSpan.FromSeconds(3) },
    ];

    static string[] Selected(IEnumerable<PDMediaSegment> list) => [.. list.Where(s => s.CssClass?.Split(' ').Contains(SelectedMarks.SelectedClass) == true).Select(s => s.Id)];

    [Fact]
    public void No_selection_hands_back_the_same_list()
    {
        Assert.Same(Segments, SelectedMarks.Mark(Segments, null));
        Assert.Same(Segments, SelectedMarks.Mark(Segments, ""));
        Assert.Same(Markers, SelectedMarks.Mark(Markers, null));
        Assert.Null(SelectedMarks.Mark((IReadOnlyList<PDMediaSegment>?)null, "a"));
    }

    [Fact]
    public void Exactly_the_chosen_segment_gets_the_class_and_the_rest_are_the_same_instances()
    {
        var marked = SelectedMarks.Mark(Segments, "c")!;

        Assert.Equal(3, marked.Count);
        Assert.Equal(["c"], Selected(marked));
        Assert.Same(Segments[0], marked[0]);
        Assert.Same(Segments[1], marked[1]);
        Assert.NotSame(Segments[2], marked[2]);
        Assert.Null(Segments[2].CssClass);
        Assert.Equal("Tail", marked[2].Label);
        Assert.Equal(Segments[2].Start, marked[2].Start);
        Assert.Equal(Segments[2].End, marked[2].End);
    }

    [Fact]
    public void Choosing_another_segment_moves_the_class()
    {
        Assert.Equal(["a"], Selected(SelectedMarks.Mark(Segments, "a")!));
        Assert.Equal(["b"], Selected(SelectedMarks.Mark(Segments, "b")!));
        Assert.Empty(Selected(SelectedMarks.Mark(Segments, "nope")!));
    }

    [Fact]
    public void The_copy_keeps_every_field_and_the_items_own_class()
    {
        var a = SelectedMarks.Mark(Segments, "a")![0];
        Assert.Equal("mine waa-selected", a.CssClass);
        Assert.Equal(("red", "d", "margin"), (a.Color, a.Detail, a.Kind));

        var b = SelectedMarks.Mark(Segments, "b")![1];
        Assert.Equal((1, 0.5, "s"), (b.Lane, b.Confidence, b.Source));
    }

    [Fact]
    public void Markers_work_the_same_way()
    {
        var marked = SelectedMarks.Mark(Markers, "m2")!;
        Assert.Equal("x", marked[0].CssClass);
        Assert.Equal(SelectedMarks.SelectedClass, marked[1].CssClass);
        Assert.Same(Markers[0], marked[0]);
        Assert.Equal((Markers[1].Position, Markers[1].Length, Markers[1].Label), (marked[1].Position, marked[1].Length, marked[1].Label));

        var moved = SelectedMarks.Mark(Markers, "m1")!;
        Assert.Equal("x waa-selected", moved[0].CssClass);
        Assert.Null(moved[1].CssClass);
    }

    [Fact]
    public void The_stylesheet_styles_the_class_for_the_row_and_the_lane_bar_from_bootstrap_variables()
    {
        var css = RepoFiles.Read("src/WebAudioArchive.Web/wwwroot/css/app.css");
        var start = css.IndexOf(".pd-media-player .pdmp-marker-list .waa-selected", StringComparison.Ordinal);
        Assert.True(start > 0);
        var block = css[start..];
        Assert.Contains(".pdmp-segment-bar.waa-selected", block);
        Assert.DoesNotMatch("#[0-9a-fA-F]{3,8}\b", block);
        Assert.DoesNotContain("rgb(", block);
    }
}
