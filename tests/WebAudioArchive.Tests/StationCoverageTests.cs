using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class StationCoverageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-stcov-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_known_before_the_first_refresh()
    {
        Assert.Null(new StationCoverage().Latest);
    }

    [Fact]
    public void A_refresh_keeps_the_24_hour_and_30_day_figures_and_when_they_were_worked_out()
    {
        var day = Path.Combine(_dir, "2026", "10-15");
        Directory.CreateDirectory(day);
        SegmentMetaStore.Write(Path.Combine(day, "110000.json"), new SegmentMeta(
            1, "i", "burst", "110000.mp3", Now.AddHours(-1), Now, 0, 0, 3600, null, null, [], [], "hour-boundary"));
        var coverage = new StationCoverage();

        coverage.Refresh(_dir, Now);

        var latest = Assert.IsType<CoverageSnapshot>(coverage.Latest);
        Assert.Equal(1.0 / 24, latest.Fraction24h, 6);
        Assert.Equal(1.0 / (24 * 30), latest.Fraction30d, 6);
        Assert.Equal(Now, latest.ComputedUtc);
    }
}
