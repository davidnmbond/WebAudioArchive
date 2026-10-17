using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

/// <summary>Issue #104: an hour is merged only once its sources have been closed for 24 hours and analysis has finished with them.</summary>
public sealed class EligibilityTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-elig-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset H10 = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static SourceSegment Seg(string recorder, DateTimeOffset start, DateTimeOffset end, string stem = "100000")
    {
        var master = Path.Combine("x", recorder, "burst", "master");
        var meta = new SegmentMeta(1, recorder, "burst", stem + ".mp3", start, end, 0, 0, 0, null, null, [], [], "hour-boundary");
        return new SourceSegment(recorder, master, Path.Combine(master, "2026", "10-10", stem + ".mp3"), meta);
    }

    static IReadOnlyList<DateTimeOffset> Hours(IReadOnlyList<SourceSegment> a, DateTimeOffset now, IReadOnlySet<DateTimeOffset>? merged = null, DateTimeOffset? open = null)
        => Eligibility.Hours(a, [], merged ?? new HashSet<DateTimeOffset>(), open, now, _ => true);

    [Fact]
    public void An_hour_closed_less_than_24_hours_ago_is_not_eligible()
    {
        var a = new[] { Seg("a", H10, H10.AddHours(1)) };
        Assert.Empty(Hours(a, H10.AddHours(1).Add(Eligibility.MinAge).AddMinutes(-1)));
    }

    [Fact]
    public void An_hour_closed_at_24_hours_is_eligible()
    {
        var a = new[] { Seg("a", H10, H10.AddHours(1)) };
        Assert.Equal([H10], Hours(a, H10.AddHours(1).Add(Eligibility.MinAge)));
    }

    [Fact]
    public void An_hour_waits_until_an_open_part_has_moved_past_it()
    {
        var a = new[] { Seg("a", H10, H10.AddHours(1)) };
        var now = H10.AddDays(3);
        Assert.Empty(Hours(a, now, open: H10.AddMinutes(30)));
        Assert.Equal([H10], Hours(a, now, open: H10.AddHours(1)));
    }

    [Theory]
    [InlineData(JobStatus.Running, false)]
    [InlineData(JobStatus.Waiting, false)]
    [InlineData(JobStatus.Done, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(null, false)]
    public void An_hour_waits_until_analysis_has_finished_with_it(string? status, bool expected)
    {
        var s = Seg("a", H10, H10.AddHours(1));
        var dir = new AnalysisPaths(_root, "burst").SegmentDir("a/2026/10-10/100000");
        if (status is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, AnalysisPaths.StateFile), $$"""{"status":"{{status}}"}""");
        }

        Assert.Equal(expected, Eligibility.AnalysisSettled(_root, "burst", s));
    }

    [Fact]
    public void Analysis_is_not_required_when_there_is_no_analysis_root() =>
        Assert.True(Eligibility.AnalysisSettled(null, "burst", Seg("a", H10, H10.AddHours(1))));

    [Fact]
    public void An_unreadable_state_file_is_not_settled()
    {
        var dir = new AnalysisPaths(_root, "burst").SegmentDir("a/2026/10-10/100000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AnalysisPaths.StateFile), "{not json");
        Assert.False(Eligibility.AnalysisSettled(_root, "burst", Seg("a", H10, H10.AddHours(1))));
    }

    [Fact]
    public void An_hour_needs_every_overlapping_segment_settled()
    {
        var a = new[] { Seg("a", H10, H10.AddHours(1)) };
        var b = new[] { Seg("b", H10, H10.AddHours(1)) };
        var now = H10.AddDays(3);
        Assert.Empty(Eligibility.Hours(a, b, new HashSet<DateTimeOffset>(), null, now, s => s.Recorder == "a"));
    }

    [Fact]
    public void Merged_hours_are_skipped_and_the_rest_come_oldest_first()
    {
        var a = new[] { Seg("a", H10.AddHours(2), H10.AddHours(3), "120000"), Seg("a", H10, H10.AddHours(2), "100000") };
        var merged = new HashSet<DateTimeOffset> { H10.AddHours(1) };
        Assert.Equal([H10, H10.AddHours(2)], Hours(a, H10.AddDays(5), merged));
    }
}
