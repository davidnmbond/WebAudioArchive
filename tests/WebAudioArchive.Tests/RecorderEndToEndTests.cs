using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class RecorderEndToEndTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Fake_station_with_icy_titles_a_drop_and_a_gap_is_archived_exactly()
    {
        await using var station = await FakeStation.StartAsync();
        var gate = new TaskCompletionSource();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(100), MetaInt = 16000, TitleForBlock = b => b == 0 ? "A - B" : null, DropAfterSend = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(100, startSeq: 100), MetaInt = 16000, Gate = gate.Task };

        var clock = new ManualClock(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero));
        var recorder = new MasterRecorder(_root, "fake", "e2e", clock, NullLogger<MasterRecorder>.Instance);
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
        var health = new StationHealth();
        var session = new StationSession(source, recorder, clock, health, NullLogger<StationSession>.Instance,
            new SessionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await Wait(() => recorder.FramesWritten >= 100);
        await Wait(() => station.StreamConnections >= 2);
        clock.Advance(TimeSpan.FromSeconds(7));
        gate.SetResult();
        await Wait(() => recorder.FramesWritten >= 200);
        cts.Cancel();
        await run;
        recorder.Dispose();

        var master = Path.Combine(_root, "fake", "master");
        var mp3 = Assert.Single(Directory.EnumerateFiles(master, "*.mp3", SearchOption.AllDirectories));
        Assert.Equal(Mp3TestData.Frames(200), File.ReadAllBytes(mp3));

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal(200, meta.Frames);
        Assert.Equal("shutdown", meta.EndedBy);
        Assert.Equal("A - B", Assert.Single(meta.Titles).Title);
        Assert.Equal(7, Assert.Single(meta.Gaps).Seconds, 3);
        Assert.Equal(16000, meta.Stream!.IcyMetaInt);
        Assert.Equal("Fake FM", meta.Stream.IcyName);
        Assert.Equal("media-element", meta.Stream.HowFound);
    }

    static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for the recorder");
            await Task.Delay(10);
        }
    }
}
