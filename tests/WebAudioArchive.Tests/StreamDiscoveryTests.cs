using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StreamDiscoveryTests
{
    static readonly Uri Listen = new("https://www.burstradio.org/listen");

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Burst_listen_page_yields_the_radiojar_stream_first()
    {
        // Expected value observed on the live page on 2026-10-15: streamName 'nkyraup3h3nwv'.
        var candidates = StreamCandidates.FromHtml(Fixture("burst-listen-2026-10-15.html"), Listen);

        Assert.NotEmpty(candidates);
        Assert.Equal(new Uri("https://stream.rcs.revma.com/nkyraup3h3nwv"), candidates[0].Url);
        Assert.Equal("radiojar-widget", candidates[0].How);
    }

    [Fact]
    public void Audio_element_with_relative_src_is_resolved_against_the_page()
    {
        var c = StreamCandidates.FromHtml("""<audio controls src="/live.mp3"></audio>""", Listen);

        Assert.Equal(new Uri("https://www.burstradio.org/live.mp3"), c[0].Url);
        Assert.Equal("media-element", c[0].How);
    }

    [Fact]
    public void Page_without_a_stream_yields_nothing()
    {
        Assert.Empty(StreamCandidates.FromHtml("<html><body>hello</body></html>", Listen));
    }

    [Fact]
    public async Task Resolver_returns_first_candidate_that_answers_with_audio()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(Fixture("burst-listen-2026-10-15.html")),
            "https://stream.rcs.revma.com/nkyraup3h3nwv" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://stream.rcs.revma.com/nkyraup3h3nwv"), r.Url);
        Assert.Equal("radiojar-widget", r.How);
    }

    [Fact]
    public async Task Resolver_skips_a_dead_candidate_and_uses_the_next()
    {
        const string page = """<script>x({ streamName: 'dead' })</script><audio src="https://example.org/live.mp3"></audio>""";
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(page),
            "https://example.org/live.mp3" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://example.org/live.mp3"), r.Url);
    }

    [Fact]
    public async Task A_direct_stream_url_is_used_as_is()
    {
        var handler = new StubHandler(_ => StubHandler.Audio());
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(new Uri("https://example.org/live"), CancellationToken.None);

        Assert.Equal(new Uri("https://example.org/live"), r.Url);
        Assert.Equal("direct-stream", r.How);
    }

    [Fact]
    public async Task Resolver_fails_clearly_when_nothing_works()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri == "https://www.burstradio.org/listen"
            ? StubHandler.Html("<html></html>")
            : StubHandler.NotFound());
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var ex = await Assert.ThrowsAsync<StreamNotFoundException>(() => resolver.ResolveAsync(Listen, CancellationToken.None));
        Assert.Contains("burstradio.org/listen", ex.Message);
    }

    [Fact]
    public async Task A_pls_playlist_is_followed_to_its_stream()
    {
        const string page = """<a href="x"></a><script>var u = "https://example.org/radio.pls";</script>""";
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(page),
            "https://example.org/radio.pls" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[playlist]\nNumberOfEntries=1\nFile1=https://example.org:8000/live\n"),
            },
            "https://example.org:8000/live" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://example.org:8000/live"), r.Url);
        Assert.EndsWith("+playlist", r.How);
    }
}
