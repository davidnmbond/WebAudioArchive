using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class CoverageReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-cov-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 10, 15, h, m, s, TimeSpan.Zero);

    void Segment(string name, DateTimeOffset start, DateTimeOffset end, params SegmentGap[] gaps)
    {
        var day = Path.Combine(_dir, start.ToString("yyyy"), start.ToString("MM-dd"));
        Directory.CreateDirectory(day);
        SegmentMetaStore.Write(Path.Combine(day, name + ".json"), new SegmentMeta(
            1, "i", "burst", name + ".mp3", start, end, 0, 0, (end - start).TotalSeconds, null, null, [], gaps, "hour-boundary"));
    }

    [Fact]
    public void Coverage_is_segment_time_minus_gaps_inside_the_window()
    {
        Segment("100000", At(10, 0), At(10, 30));
        Segment("103500", At(10, 35), At(11, 0), new SegmentGap(At(10, 40), At(10, 41), 60, "x"));

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));

        Assert.Equal(1800 + 1500 - 60, c.CoveredSeconds, 3);
        Assert.Equal(3240.0 / 3600, c.Fraction, 6);
    }

    [Fact]
    public void Coverage_clips_segments_to_the_window()
    {
        Segment("100000", At(10, 0), At(10, 30));
        Segment("103500", At(10, 35), At(11, 0), new SegmentGap(At(10, 40), At(10, 41), 60, "x"));

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromMinutes(30));

        Assert.Equal(1440, c.CoveredSeconds, 3);
        Assert.Equal(0.8, c.Fraction, 6);
    }

    [Fact]
    public void Empty_directory_is_zero_coverage()
    {
        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));
        Assert.Equal(0, c.CoveredSeconds);
        Assert.Equal(0, c.Fraction);
    }

    [Fact]
    public void Unreadable_or_incomplete_sidecars_are_skipped_and_never_throw()
    {
        Segment("100000", At(10, 0), At(10, 30));
        var day = Path.Combine(_dir, "2026", "10-15");
        File.WriteAllText(Path.Combine(day, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(day, "nullgaps.json"),
            """{"startUtc":"2026-10-15T10:30:00+00:00","endUtc":"2026-10-15T10:40:00+00:00","gaps":null,"titles":null}""");
        File.WriteAllText(Path.Combine(day, "empty.json"), "{}");
        var lockedPath = Path.Combine(day, "locked.json");
        File.WriteAllText(lockedPath, "{}");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));

        Assert.Equal(1800 + 600, c.CoveredSeconds, 3); // the good segment plus the one with null gaps
    }

    [Fact]
    public void Several_windows_in_one_pass_match_separate_reads()
    {
        Segment("100000", At(10, 0), At(10, 30));
        Segment("103500", At(10, 35), At(11, 0), new SegmentGap(At(10, 40), At(10, 41), 60, "x"));

        var many = CoverageReader.Read(_dir, At(11, 0), [TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)]);

        Assert.Equal(2, many.Count);
        Assert.Equal(CoverageReader.Read(_dir, At(11, 0), TimeSpan.FromHours(1)), many[0]);
        Assert.Equal(CoverageReader.Read(_dir, At(11, 0), TimeSpan.FromMinutes(30)), many[1]);
    }

    [Fact]
    public void Day_folders_that_end_well_before_the_window_are_not_read()
    {
        Segment("100000", At(10, 0), At(10, 30));
        // A sidecar filed under a day long before the window. Its times are deliberately inside the window, so it
        // would count if the reader opened it; the reader must skip the folder by name without opening it.
        var old = Path.Combine(_dir, "2026", "09-01");
        Directory.CreateDirectory(old);
        SegmentMetaStore.Write(Path.Combine(old, "100000.json"), new SegmentMeta(
            1, "i", "burst", "100000.mp3", At(10, 30), At(10, 40), 0, 0, 600, null, null, [], [], "hour-boundary"));

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));

        Assert.Equal(1800, c.CoveredSeconds, 3);
    }
}
