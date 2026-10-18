extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Consolidation;
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

    void Hours(string id)
    {
        for (var h = -1; h < 2; h++)
            SegmentFiles.Live(Root(id), "burst", Epoch, T0.AddHours(h), T0.AddHours(h + 1));
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
        // The 10:00 originals are held once merged: only the merged copy has that hour.
        foreach (var id in new[] { "a", "b" })
            File.Delete(Path.Combine(Root(id), "burst", "master", "2026", "10-15", "100000.mp3"));

        var plan = Plan(Catalog(), T0.AddMinutes(50), T0.AddMinutes(70));

        Assert.Equal("a+b", plan.Recorder);
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "merged");
        Assert.Contains(plan.Pieces, p => p.Segment.Recorder == "a");
        var seqs = Seqs(plan);
        Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
    }

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
