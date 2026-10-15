using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StationSessionTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);
    static readonly SessionOptions Fast = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    static StationSession NewSession(FakeStation station, IFrameSink sink, ManualClock clock, StationHealth health, SessionOptions? options = null)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
        return new StationSession(source, sink, clock, health, NullLogger<StationSession>.Instance, options ?? Fast);
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
        // The last frame is held until the stream ends or the session is cancelled, so wait for all but one.
        await WaitFor(() => sink.Frames.Count >= 99, "99 frames");
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
        // The last frame is held until the stream ends or the session is cancelled, so wait for all but one.
        await WaitFor(() => sink.Frames.Count >= 99, "second 50 frames");
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
        var session = NewSession(station, sink, new ManualClock(T0), health, new SessionOptions(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));
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
        await WaitFor(() => sink.Frames.Count >= 9, "audio after two failures");
        cts.Cancel();
        await run;

        Assert.Empty(sink.Gaps);
    }

    [Fact]
    public async Task Written_frames_set_the_frame_written_time()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(20) };
        var health = new StationHealth();
        var session = NewSession(station, new RecordingSink(), new ManualClock(T0), health);
        using var cts = new CancellationTokenSource();

        Assert.Null(health.LastFrameWrittenUtc);
        var run = session.RunAsync(cts.Token);
        await WaitFor(() => health.LastFrameWrittenUtc is not null, "a written frame");
        cts.Cancel();
        await run;

        Assert.Equal(T0, health.LastFrameWrittenUtc);
        Assert.Null(health.LastError);
    }

    [Fact]
    public async Task Gap_starts_at_the_last_data_not_at_the_disconnect()
    {
        await using var station = await FakeStation.StartAsync();
        var dropGate = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(50), DropAfterSend = true, DropGate = dropGate.Task }
            : new StreamPlan { Audio = Mp3TestData.Frames(50, startSeq: 50), Gate = gate.Task };
        var clock = new ManualClock(T0);
        var sink = new RecordingSink();
        var session = NewSession(station, sink, clock, new StationHealth());
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 49, "the audio before the drop"); // the last frame is held until the stream ends
        clock.Advance(TimeSpan.FromSeconds(3));                                   // time passes with no data, before the drop
        dropGate.SetResult();
        await WaitFor(() => station.StreamConnections >= 2, "the reconnect");
        clock.Advance(TimeSpan.FromSeconds(7));
        gate.SetResult();
        // The last frame is held until the stream ends or the session is cancelled, so wait for all but one.
        await WaitFor(() => sink.Frames.Count >= 99, "second 50 frames");
        cts.Cancel();
        await run;

        var gap = Assert.Single(sink.Gaps);
        Assert.Equal(T0, gap.StartUtc);
        Assert.Equal(T0.AddSeconds(10), gap.EndUtc);
    }

    sealed class FailingSink : IFrameSink
    {
        readonly Func<int, bool> _failOnAttempt;
        readonly object _lock = new();
        public List<DateTime> ConnectTimes { get; } = [];
        public List<byte[]> FrameAttempts { get; } = [];
        public List<byte[]> FramesWritten { get; } = [];
        public List<GapRecord> Gaps { get; } = [];

        public int ConnectCount { get { lock (_lock) return ConnectTimes.Count; } }
        public int WrittenCount { get { lock (_lock) return FramesWritten.Count; } }

        public FailingSink(Func<int, bool>? failOnAttempt = null) => _failOnAttempt = failOnAttempt ?? (_ => true);

        public void OnConnected(StreamInfo info, DateTimeOffset utc)
        {
            lock (_lock) ConnectTimes.Add(DateTime.UtcNow);
        }

        public void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc)
        {
            lock (_lock)
            {
                FrameAttempts.Add(frame.ToArray());
                if (_failOnAttempt(FrameAttempts.Count)) throw new IOException("No space left on device");
                FramesWritten.Add(frame.ToArray());
            }
        }

        public void OnTitle(string title, DateTimeOffset utc) { }

        public void OnGap(GapRecord gap)
        {
            lock (_lock) Gaps.Add(gap);
        }
    }

    [Fact]
    public async Task A_failing_sink_is_reported_not_healthy_and_backed_off_without_reset()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(20) };
        var sink = new FailingSink();
        var health = new StationHealth();
        var options = new SessionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800));
        var session = NewSession(station, sink, new ManualClock(T0), health, options);
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.ConnectCount >= 4, "four connection attempts");
        cts.Cancel();
        await run;

        Assert.Null(health.LastFrameWrittenUtc);
        Assert.NotNull(health.LastByteUtc);
        Assert.Contains("No space left on device", health.LastError!.Message);
        Assert.Contains("recorder write failed", health.LastError.Message);
        Assert.False(health.Connected);

        // Delays grow 100, 200, 400 ms. A backoff that reset after each data-bearing attempt would stay near 100 ms.
        var t = sink.ConnectTimes;
        Assert.True((t[3] - t[2]).TotalMilliseconds >= 300, $"third interval was {(t[3] - t[2]).TotalMilliseconds} ms");
        Assert.True((t[3] - t[0]).TotalMilliseconds >= 650);

        var gap = Assert.Single(sink.Gaps.Take(1));
        Assert.StartsWith("recorder write failed", gap.Reason);
    }

    [Fact]
    public async Task After_a_sink_failure_the_same_frame_is_never_offered_twice()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(20) }
            : new StreamPlan { Audio = Mp3TestData.Frames(20, startSeq: 20) };
        var sink = new FailingSink(attempt => attempt == 6);
        var session = NewSession(station, sink, new ManualClock(T0), new StationHealth(),
            new SessionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.WrittenCount >= 20, "frames from the second connection");
        cts.Cancel();
        await run;

        var attempts = sink.FrameAttempts.Select(f => Mp3TestData.SeqOf(f)).ToList();
        Assert.Equal(attempts.Count, attempts.Distinct().Count());
        Assert.Contains(5, attempts); // the frame that failed was offered once, and not again
    }
}
