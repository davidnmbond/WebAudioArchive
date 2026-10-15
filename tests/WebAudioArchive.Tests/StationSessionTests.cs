using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StationSessionTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);
    static readonly SessionOptions Fast = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    static StationSession NewSession(FakeStation station, RecordingSink sink, ManualClock clock, StationHealth health)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
        return new StationSession(source, sink, clock, health, NullLogger<StationSession>.Instance, Fast);
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Frames_titles_and_metadata_arrive_and_icy_blocks_are_removed()
    {
        await using var station = await FakeStation.StartAsync();
        var audio = Mp3TestData.Frames(100);
        station.Plan = _ => new StreamPlan { Audio = audio, MetaInt = 4000, TitleForBlock = b => b == 1 ? "Artist - Song" : null };
        var sink = new RecordingSink();
        var health = new StationHealth();
        var session = NewSession(station, sink, new ManualClock(T0), health);
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 100, "100 frames");
        cts.Cancel();
        await run;

        Assert.Equal(audio, sink.Frames.SelectMany(f => f).ToArray());
        Assert.Equal(["Artist - Song"], sink.Titles.ToArray());
        Assert.True(health.LastByteUtc is not null);
        Assert.False(health.Connected);
    }

    [Fact]
    public async Task Dropped_connection_is_reconnected_and_the_gap_recorded()
    {
        await using var station = await FakeStation.StartAsync();
        var gate = new TaskCompletionSource();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(50), DropAfterSend = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(50, startSeq: 50), Gate = gate.Task };
        var clock = new ManualClock(T0);
        var sink = new RecordingSink();
        var session = NewSession(station, sink, clock, new StationHealth());
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 50, "first 50 frames");
        await WaitFor(() => station.StreamConnections >= 2, "the reconnect");
        clock.Advance(TimeSpan.FromSeconds(7));
        gate.SetResult();
        await WaitFor(() => sink.Frames.Count >= 100, "second 50 frames");
        cts.Cancel();
        await run;

        var gap = Assert.Single(sink.Gaps);
        Assert.Equal(T0, gap.StartUtc);
        Assert.Equal(T0.AddSeconds(7), gap.EndUtc);
        Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        Assert.Equal(Mp3TestData.Frames(100), sink.Frames.SelectMany(f => f).ToArray());
        Assert.Equal(2, sink.Connections);
    }

    [Fact]
    public async Task Stalled_connection_is_dropped_and_reconnected()
    {
        // The server keeps the socket open and sends nothing: only a read timeout can notice.
        await using var station = await FakeStation.StartAsync();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(10), HoldOpen = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(10, startSeq: 10), HoldOpen = true };
        var sink = new RecordingSink();
        var health = new StationHealth();
        var session = NewSession(station, sink, new ManualClock(T0), health);
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => station.StreamConnections >= 2 && sink.Frames.Count >= 20, "the reconnect after a stall");
        cts.Cancel();
        await run;

        Assert.True(health.Reconnects >= 1);
        Assert.Equal(Mp3TestData.Frames(20), sink.Frames.SelectMany(f => f).ToArray());
    }

    [Fact]
    public async Task A_failing_server_is_retried_without_a_gap_before_any_audio()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = n => n < 2
            ? new StreamPlan { Status = 503 }
            : new StreamPlan { Audio = Mp3TestData.Frames(10) };
        var sink = new RecordingSink();
        var session = NewSession(station, sink, new ManualClock(T0), new StationHealth());
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 10, "audio after two failures");
        cts.Cancel();
        await run;

        Assert.Empty(sink.Gaps);
    }
}
