using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public class HealthRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, 200, null, true)]      // after the grace period and never wrote a frame
    [InlineData(true, 200, 121, true)]       // last frame too old
    [InlineData(true, 200, 120, false)]      // exactly at the limit is still fine
    [InlineData(true, 200, 5, false)]
    [InlineData(true, 179, null, false)]     // inside the grace period
    [InlineData(true, 180, null, true)]
    [InlineData(false, 500, null, false)]    // disabled stations never make the host unhealthy
    public void Verdict_is_based_on_the_last_frame_written(bool enabled, int uptimeSeconds, int? secondsSinceFrame, bool expected)
    {
        DateTimeOffset? last = secondsSinceFrame is null ? null : Now.AddSeconds(-secondsSinceFrame.Value);
        Assert.Equal(expected, HealthRules.IsUnhealthy(enabled, TimeSpan.FromSeconds(uptimeSeconds), last, Now));
    }

    [Fact]
    public void A_sink_error_is_cleared_by_the_next_written_frame()
    {
        var health = new StationHealth();
        health.NoteSinkError("disk full", Now);
        Assert.Equal("disk full", health.LastError!.Message);
        health.NoteFrameWritten(Now.AddSeconds(1));
        Assert.Null(health.LastError);
        Assert.Equal(Now.AddSeconds(1), health.LastFrameWrittenUtc);
    }
}
