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

    void State(string jobId, string? status)
    {
        if (status is null) return;
        var dir = new AnalysisPaths(_root, "burst").SegmentDir(jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AnalysisPaths.StateFile), $$"""{"status":"{{status}}"}""");
    }

    [Theory]
    [InlineData(JobStatus.Running, SegmentAnalysis.Pending)]
    [InlineData(JobStatus.Waiting, SegmentAnalysis.Pending)]
    [InlineData(JobStatus.Done, SegmentAnalysis.Finished)]
    [InlineData(JobStatus.Failed, SegmentAnalysis.Finished)]
    [InlineData(null, SegmentAnalysis.NotQueued)]
    public void A_segment_analysis_state_is_read_from_its_state_file(string? status, SegmentAnalysis expected)
    {
        State("a/2026/10-10/100000", status);
        Assert.Equal(expected, Eligibility.AnalysisOf(_root, "burst", Seg("a", H10, H10.AddHours(1))));
    }

    [Fact]
    public void An_unreadable_state_file_counts_as_pending()
    {
        var dir = new AnalysisPaths(_root, "burst").SegmentDir("a/2026/10-10/100000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AnalysisPaths.StateFile), "{not json");
        Assert.Equal(SegmentAnalysis.Pending, Eligibility.AnalysisOf(_root, "burst", Seg("a", H10, H10.AddHours(1))));
    }

    [Fact]
    public void Analysis_is_not_required_when_there_is_no_analysis_root() =>
        Assert.True(Eligibility.HourSettled(null, "burst", [Seg("a", H10, H10.AddHours(1))]));

    // Analysis queues only the preferred recorder's segments; B gets a job only where A has a hole (WorkPlanner).
    [Theory]
    [InlineData(JobStatus.Done, null, true)]
    [InlineData(JobStatus.Failed, null, true)]
    [InlineData(null, JobStatus.Done, true)]
    [InlineData(JobStatus.Done, JobStatus.Done, true)]
    [InlineData(JobStatus.Done, JobStatus.Failed, true)]
    [InlineData(null, null, false)]
    [InlineData(JobStatus.Waiting, null, false)]
    [InlineData(JobStatus.Running, null, false)]
    [InlineData(JobStatus.Done, JobStatus.Waiting, false)]
    [InlineData(JobStatus.Done, JobStatus.Running, false)]
    public void An_hour_is_analysis_settled_when_nothing_is_pending_and_something_finished(string? a, string? b, bool expected)
    {
        State("a/2026/10-10/100000", a);
        State("b/2026/10-10/100000", b);
        var segments = new[] { Seg("a", H10, H10.AddHours(1)), Seg("b", H10, H10.AddHours(1)) };
        Assert.Equal(expected, Eligibility.HourSettled(_root, "burst", segments));
    }

    [Fact]
    public void A_pending_segment_of_either_recorder_holds_back_the_hour_even_when_another_finished()
    {
        State("a/2026/10-10/100000", JobStatus.Done);
        State("a/2026/10-10/103000", JobStatus.Waiting);
        var segments = new[] { Seg("a", H10, H10.AddMinutes(30)), Seg("a", H10.AddMinutes(30), H10.AddHours(1), "103000"), Seg("b", H10, H10.AddHours(1)) };
        Assert.False(Eligibility.HourSettled(_root, "burst", segments));
    }

    [Fact]
    public void The_hour_rule_is_given_every_overlapping_segment_of_both_recorders()
    {
        var a = new[] { Seg("a", H10, H10.AddHours(1)) };
        var b = new[] { Seg("b", H10, H10.AddHours(1)), Seg("b", H10.AddHours(1), H10.AddHours(2), "110000") };
        var now = H10.AddDays(3);
        var seen = new List<IReadOnlyList<SourceSegment>>();

        var hours = Eligibility.Hours(a, b, new HashSet<DateTimeOffset>(), null, now, segments =>
        {
            seen.Add(segments);
            return segments.Any(s => s.Recorder == "a");
        });

        Assert.Equal([H10], hours);
        Assert.Equal(["a", "b"], seen[0].Select(s => s.Recorder).Order());
        Assert.Equal(["b"], seen[1].Select(s => s.Recorder));
    }

    [Fact]
    public void Merged_hours_are_skipped_and_the_rest_come_oldest_first()
    {
        var a = new[] { Seg("a", H10.AddHours(2), H10.AddHours(3), "120000"), Seg("a", H10, H10.AddHours(2), "100000") };
        var merged = new HashSet<DateTimeOffset> { H10.AddHours(1) };
        Assert.Equal([H10, H10.AddHours(2)], Hours(a, H10.AddDays(5), merged));
    }
}
