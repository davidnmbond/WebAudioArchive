extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

/// <summary>Issue #104: the web app reads the consolidator's merged hours as another source and prefers them for clips.</summary>
public sealed class MergedReadingTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-mergedread-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = T0.AddHours(3);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    void Hours(string id, params SegmentGap[] gaps)
    {
        for (var h = -1; h < 2; h++)
        {
            var start = T0.AddHours(h);
            SegmentFiles.Live(Root(id), "burst", Epoch, start, start.AddHours(1), [.. gaps.Where(g => g.StartUtc >= start && g.EndUtc <= start.AddHours(1))]);
        }
    }

    static SegmentGap Gap(int fromSecond, int seconds) => new(T0.AddSeconds(fromSecond), T0.AddSeconds(fromSecond + seconds), seconds, "end-of-stream");

    void HoldTenOClockOriginals()
    {
        foreach (var id in new[] { "a", "b" })
            File.Delete(Path.Combine(Root(id), "burst", "master", "2026", "10-15", "100000.mp3"));
    }

    /// <summary>Merges the 10:00 hour from A and B into the merged root.</summary>
    void MergeTenOClock()
    {
        var plan = HourMerger.Plan(T0, SourceSegment.Scan(Root("a"), "burst", "a"), SourceSegment.Scan(Root("b"), "burst", "b"), [])!;
        MergedHourWriter.Write(plan, Path.Combine(Root("merged"), "burst", "master"), "burst");
    }

    SegmentCatalog Catalog()
    {
        var settings = TestSettings.Create(("a", Root("a")), ("b", Root("b"))) with { MergedRoot = Root("merged") };
        return new SegmentCatalog(settings, new ManualTimeProvider(Now), NullLogger<SegmentCatalog>.Instance);
    }

    static ClipPlan Plan(SegmentCatalog catalog, DateTimeOffset from, DateTimeOffset to) =>
        ClipPlanner.Plan(new ClipRequest(from, to, TimeSpan.Zero, TimeSpan.Zero, "auto"), catalog.All(), Now, ClipPlanner.DefaultMaxClip);

    static List<int> Seqs(ClipPlan plan) =>
        SegmentFiles.Seqs([.. plan.Pieces.SelectMany(p => File.ReadAllBytes(p.Segment.AudioPath)[(int)p.StartByte..(int)p.EndByte])]);

    [Fact]
    public void An_auto_clip_of_a_merged_hour_comes_from_the_merged_copy()
    {
        Hours("a");
        Hours("b");
        MergeTenOClock();

        var catalog = Catalog();
        var plan = Plan(catalog, T0.AddMinutes(10), T0.AddMinutes(20));

        Assert.Equal("merged", catalog.All()[0].Recorder);
        Assert.Equal("merged", plan.Recorder);
        Assert.All(plan.Pieces, p => Assert.Equal("merged", p.Segment.Recorder));
    }

    [Fact]
    public void A_clip_spanning_merged_and_unmerged_hours_joins_them_frame_by_frame()
    {
        Hours("a");
        Hours("b");
        MergeTenOClock();
        HoldTenOClockOriginals(); // held once merged: only the merged copy has that hour

        var plan = Plan(Catalog(), T0.AddMinutes(50), T0.AddMinutes(70));

        Assert.Equal("a+b", plan.Recorder);
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "merged");
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "a");
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
    }

    [Fact]
    public void Every_other_source_fills_what_the_earlier_ones_lack()
    {
        // A has a gap in the 11:00 hour that B does not.
        Hours("a", Gap(3600 + 600, 30));
        Hours("b");
        MergeTenOClock();
        HoldTenOClockOriginals();

        var plan = Plan(Catalog(), T0.AddMinutes(30), T0.AddMinutes(90));

        Assert.Equal("a+b", plan.Recorder);
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
        Assert.Empty(ClipEvents.Gaps(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("joins the merged copy and recorders A and B") || w.Contains("joins the merged copy and recorder B"));
    }

    [Fact]
    public void Three_sources_are_folded_in_turn_when_each_lacks_something_another_has()
    {
        // A and B equally covered, so A is tried first; B holds the stretch A lacks and A the stretch B lacks.
        Hours("a", Gap(3600 + 600, 30));
        Hours("b", Gap(3600 + 1200, 30));
        MergeTenOClock();
        HoldTenOClockOriginals();

        var plan = Plan(Catalog(), T0.AddMinutes(30), T0.AddMinutes(90));

        Assert.Equal("a+b", plan.Recorder);
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
        Assert.Empty(ClipEvents.Gaps(plan));
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "merged");
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "a");
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "b");
    }

    [Fact]
    public void The_merged_copy_is_never_called_a_recorder_in_text_shown_to_a_person()
    {
        Hours("a");
        Hours("b");
        MergeTenOClock();
        var catalog = Catalog();

        var plan = Plan(catalog, T0.AddMinutes(10), T0.AddMinutes(20));
        var warnings = string.Join("\n", ClipPlanner.Plan(
            new ClipRequest(T0.AddMinutes(-30), T0.AddMinutes(20), TimeSpan.Zero, TimeSpan.Zero, "merged"), catalog.All(), Now, ClipPlanner.DefaultMaxClip).Warnings);
        var rows = new RecordingsQuery(catalog, new ManualTimeProvider(Now));
        var row = rows.Rows(null).Single(r => r.Recorder == "Merged");
        var item = rows.CalendarItems(T0, T0.AddHours(1)).Single(i => i.Kind == "segment" && i.Recorder == "M");

        Assert.Equal("merged", plan.Recorder);
        Assert.Contains("the merged copy", warnings);
        Assert.DoesNotContain("ecorder MERGED", warnings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recorder merged", warnings, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("The merged copy, ", item.Tooltip);
        Assert.Equal("Merged", row.Recorder);
    }

    [Fact]
    public void Show_coverage_names_the_merged_copy_not_a_recorder()
    {
        var segments = new[]
        {
            new RecorderSegments("merged", [Cat("merged", T0, T0.AddHours(1))]),
            new RecorderSegments("a", [Cat("a", T0, T0.AddHours(1))]),
        };
        var show = new WebAudioArchive.Core.Schedule.ShowOccurrence("burst", "t", T0, T0.AddHours(1));

        var summary = web::WebAudioArchive.Web.Schedule.ShowCoverage.Classify(show, segments, Now).Summary;

        Assert.Contains("the merged copy and A", summary);
        Assert.DoesNotContain("MERGED", summary);
    }

    static CatalogSegment Cat(string recorder, DateTimeOffset start, DateTimeOffset end) =>
        new(recorder, "burst", "x.mp3", start, end, 1, (end - start).TotalSeconds, 128, [], "hour-boundary", false, 1, DateTime.UtcNow);

    [Fact]
    public void Held_originals_are_not_listed()
    {
        Hours("a");
        Hours("b");
        MergeTenOClock();
        var master = Path.Combine(Root("a"), "burst", "master", "2026", "10-15");
        var held = Path.Combine(Root("a"), "burst", "held", "2026", "10-15");
        Directory.CreateDirectory(held);
        foreach (var f in Directory.EnumerateFiles(master, "100000.*"))
            File.Move(f, Path.Combine(held, Path.GetFileName(f)));

        var catalog = Catalog();

        Assert.DoesNotContain(catalog.Segments("a"), s => s.AudioPath.Contains("held"));
        Assert.Equal(2, catalog.Segments("a").Count);
        Assert.All(catalog.All().SelectMany(r => r.Segments), s => Assert.DoesNotContain("held", s.AudioPath));
        Assert.Single(catalog.Segments("merged"));
    }
}
