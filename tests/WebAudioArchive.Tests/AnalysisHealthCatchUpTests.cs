using System.Text.Json;
using WebAudioArchive.Analysis;

namespace WebAudioArchive.Tests;

/// <summary>I6: the first catch-up is healthy while jobs finish; 503 "lagging" only when no job has finished within the stall window.</summary>
public sealed class AnalysisHealthCatchUpTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Alarm = TimeSpan.FromHours(6);

    static MetricsSnapshot Snap(TimeSpan lag, TimeSpan? lastDoneAgo, string? paused = null, double backlogHours = 0, double? averageRtf = null) =>
        new(Now.AddHours(-10), Now.AddSeconds(-30), 5, 0, Now - lag, 3, 0, null, averageRtf, 0, null, paused, true, 0, 0, 0, 1, 0,
            LastActivityUtc: Now.AddSeconds(-30), BacklogAudioSeconds: backlogHours * 3600, LastJobDoneUtc: lastDoneAgo is { } a ? Now - a : null);

    static (int Code, string Status) Pair(MetricsSnapshot m, TimeSpan? window = null)
    {
        var h = AnalysisHealth.Build(m, Alarm, Now, window);
        return (h.StatusCode, JsonSerializer.SerializeToElement(h.Body).GetProperty("status").GetString()!);
    }

    static JsonElement Body(MetricsSnapshot m) => JsonSerializer.SerializeToElement(AnalysisHealth.Build(m, Alarm, Now).Body);

    [Fact]
    public void Lag_up_to_the_alarm_is_ok_whether_or_not_jobs_are_finishing()
    {
        Assert.Equal((200, "ok"), Pair(Snap(Alarm, null)));
        Assert.Equal((200, "ok"), Pair(Snap(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void Lag_over_the_alarm_while_jobs_finish_is_catching_up_and_healthy()
    {
        Assert.Equal((200, "catching-up"), Pair(Snap(Alarm + TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10))));
    }

    [Fact]
    public void Lag_over_the_alarm_with_no_job_ever_done_is_lagging_503()
    {
        Assert.Equal((503, "lagging"), Pair(Snap(TimeSpan.FromHours(7), null)));
    }

    [Fact]
    public void The_progress_window_boundary_is_inclusive_and_defaults_to_the_job_stall_time()
    {
        var lag = TimeSpan.FromHours(7);
        var window = AnalysisHealth.JobStallAfter;

        Assert.Equal((200, "catching-up"), Pair(Snap(lag, window)));
        Assert.Equal((503, "lagging"), Pair(Snap(lag, window + TimeSpan.FromSeconds(1))));

        var custom = TimeSpan.FromHours(5);
        Assert.Equal((200, "catching-up"), Pair(Snap(lag, custom), custom));
        Assert.Equal((503, "lagging"), Pair(Snap(lag, custom + TimeSpan.FromSeconds(1)), custom));
    }

    [Fact]
    public void A_pause_inside_the_progress_window_is_paused_and_after_it_is_lagging()
    {
        Assert.Equal((200, "paused"), Pair(Snap(TimeSpan.FromHours(7), TimeSpan.FromMinutes(30), paused: "free disk below 20 GB")));
        Assert.Equal((503, "lagging"), Pair(Snap(TimeSpan.FromHours(7), TimeSpan.FromHours(4), paused: "free disk below 20 GB")));
        Assert.Equal((200, "paused"), Pair(Snap(TimeSpan.FromHours(1), null, paused: "whisper is not ready")));
    }

    [Fact]
    public void Stalled_and_failing_still_win_over_catching_up()
    {
        var stalled = Snap(TimeSpan.FromHours(7), TimeSpan.FromMinutes(10)) with { LastPassUtc = Now.AddHours(-1), LastActivityUtc = Now.AddHours(-1) };
        Assert.Equal((503, "stalled"), Pair(stalled));
        var failing = Snap(TimeSpan.FromHours(7), TimeSpan.FromMinutes(10)) with { LastPassUtc = Now.AddMinutes(-20), LastPassError = "IOException: gone", LastActivityUtc = Now.AddSeconds(-5) };
        Assert.Equal((503, "failing"), Pair(failing));
    }

    [Fact]
    public void The_report_gives_the_backlog_in_audio_hours_and_an_eta_from_the_average_realtime_factor()
    {
        var body = Body(Snap(TimeSpan.FromHours(7), TimeSpan.FromMinutes(1), backlogHours: 10, averageRtf: 0.05));
        Assert.Equal(10, body.GetProperty("backlogAudioHours").GetDouble(), 2);
        // The factor is transcription seconds per audio second (0.05 is 20 times faster than real time): 10 hours of audio is half an hour of work.
        Assert.Equal(0.5, body.GetProperty("catchUpEtaHours").GetDouble(), 2);

        var none = Body(Snap(TimeSpan.FromHours(7), null, backlogHours: 10, averageRtf: null));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("catchUpEtaHours").ValueKind);
        var empty = Body(Snap(TimeSpan.Zero, TimeSpan.FromMinutes(1), backlogHours: 0, averageRtf: 0.05));
        Assert.Equal(0, empty.GetProperty("catchUpEtaHours").GetDouble(), 2);
    }
}
