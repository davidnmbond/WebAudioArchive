using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StreamSourceTests
{
    static HttpStreamSource Source(FakeStation station)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
    }

    [Fact]
    public async Task Open_reports_stream_details_and_follows_the_redirect()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(2), MetaInt = 16000 };

        await using var opened = await Source(station).OpenAsync(CancellationToken.None);

        Assert.Equal(16000, opened.Info.IcyMetaInt);
        Assert.Equal("audio/mpeg", opened.Info.ContentType);
        Assert.Equal("Fake FM", opened.Info.IcyHeaders["icy-name"]);
        Assert.Equal("media-element", opened.Info.HowFound);
    }

    [Fact]
    public async Task Each_open_follows_the_redirect_afresh()
    {
        // Burst redirects /<name> to a short-lived tokenised URL, so the final URL must never be cached.
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(2) };
        var source = Source(station);

        await using (await source.OpenAsync(CancellationToken.None)) { }
        await using (await source.OpenAsync(CancellationToken.None)) { }

        Assert.Equal(1, station.Hits("/listen"));            // resolved once
        Assert.Equal(3, station.Hits("/redirect-stream"));   // 1 probe + 2 opens
    }

    [Fact]
    public async Task Non_mp3_streams_are_refused_clearly()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/aac") } },
        });
        var http = new HttpClient(handler);
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance),
            new Uri("https://example.org/live"), NullLogger<HttpStreamSource>.Instance);

        var ex = await Assert.ThrowsAsync<UnsupportedStreamException>(() => source.OpenAsync(CancellationToken.None));
        Assert.Contains("audio/aac", ex.Message);
    }
}
