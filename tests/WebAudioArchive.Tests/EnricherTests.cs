using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class EnricherTests : IDisposable
{
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    const string Job = "a/2026/10-15/190000";
    readonly string _root = Directory.CreateTempSubdirectory("waa-enrich-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));
    readonly FakeLlm _llm = new();
    static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config", "analysis");
    static readonly EnrichmentOptions Options = new();

    static readonly string[] Said =
    [
        "Good evening and welcome to Backseat Aux on Burst Radio, I'm Amy and with me as always is Raff.",
        "Hi everyone. Tonight we have Priya Shah from the Students Union in the studio.",
        "That was Mr Brightside by The Killers, an absolute classic.",
        "Tickets for the winter ball are fifteen pounds.",
        "Up next is Espresso by Sabrina Carpenter.",
    ];

    const string Answer = """
        {
          "hosts": [
            { "value": "Amy", "quote": "I'm Amy and with me as always is Raff", "confidence": 1 },
            { "value": "Raff", "quote": "Hi everyone", "confidence": 0.9 }
          ],
          "guests": [
            { "value": "Priya Shah", "detail": "Students Union", "quote": "Priya Shah from the Students Union", "confidence": 7 },
            { "value": "Tom Hanks", "quote": "Tom Hanks is in the studio", "confidence": 0.8 }
          ],
          "topics": [ { "value": "winter ball tickets", "quote": "Tickets for the winter ball are fifteen pounds", "confidence": 0.9 } ],
          "artists": [ { "value": "The Killers", "quote": "Mr Brightside by The Killers", "confidence": 0.9 } ],
          "locations": [],
          "callsToAction": [],
          "songs": [
            { "title": "Mr Brightside", "artist": "The Killers", "quote": "That was Mr Brightside by The Killers", "confidence": 0.95 },
            { "title": "Espresso", "artist": "Taylor Swift", "quote": "Up next is Espresso by Sabrina Carpenter", "confidence": 0.9 }
          ],
          "language": "en",
          "summary": "Amy and Raff present Backseat Aux with a guest from the Students Union."
        }
        """;

    public EnricherTests() => _llm.Respond = (_, _) => Answer;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    static IReadOnlyList<TimedLine> Lines() => TranscriptSlicer.Slice([TranscriptFixtures.Of(Show, Said)], Show, Show.AddHours(1), ["a"]);

    ShowAnalysis Analysed(params string[] lines)
    {
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(Job), AnalysisPaths.TranscriptFile), TranscriptFixtures.Of(Show, lines) with { JobId = Job });
        return new ShowAnalysis(1, "burst-20261015T1900Z", "burst", "Backseat Aux", Show, Show.AddHours(1), Show.AddMinutes(-5), Show.AddMinutes(65),
            _clock.UtcNow, true, [new ShowSource("a", Job, Show, Show.AddHours(1))],
            new ShowCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 3600, 0), []);
    }

    Enricher MakeEnricher(EnrichmentOptions? options = null) => new(Paths, _llm, EnrichmentPrompt.Load(ConfigDir, options ?? Options), options ?? Options,
        "qwen3.6:latest", ["a", "b"], _clock, NullLogger.Instance);

    [Fact]
    public void Invented_claims_are_dropped()
    {
        var parts = EnrichmentValidator.Validate(Answer, Lines(), 40);

        Assert.Equal(new[] { "Amy" }, parts.Hosts.Select(h => h.Value));
        Assert.Equal(new[] { "Priya Shah" }, parts.Guests.Select(g => g.Value));
        Assert.Equal(1.0, parts.Guests[0].Confidence);
        Assert.Equal(new[] { "Mr Brightside", "Espresso" }, parts.Songs.Select(s => s.Title));
        Assert.Null(parts.Songs[1].Artist);
        Assert.Equal("The Killers", parts.Songs[0].Artist);
        Assert.Equal(Show, parts.Hosts[0].QuoteUtc);
        Assert.Equal(Show.AddSeconds(20), parts.Songs[0].QuoteUtc);
        Assert.Equal(3, parts.Dropped);
        Assert.Contains(parts.DropReasons, r => r.Contains("Raff") && r.Contains("does not contain"));
        Assert.Contains(parts.DropReasons, r => r.Contains("Tom Hanks") && r.Contains("not in the transcript"));
        Assert.Contains(parts.DropReasons, r => r.Contains("Taylor Swift"));
    }

    [Theory]
    [InlineData("```json\n{ \"hosts\": [], \"summary\": \"x\" }\n```")]
    [InlineData("<think>let me see</think>\n{ \"hosts\": [], \"summary\": \"x\" }")]
    [InlineData("  { \"hosts\": [], \"summary\": \"x\" }  ")]
    public void Fenced_json_is_accepted(string content)
    {
        Assert.Equal("x", EnrichmentValidator.Validate(content, Lines(), 40).Summary);
    }

    [Theory]
    [InlineData("Sorry, I cannot help with that.")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"hosts\": \"Amy\" }")]
    public void Answers_that_are_not_the_schema_are_invalid(string content)
    {
        Assert.Throws<EnrichmentInvalidException>(() => EnrichmentValidator.Validate(content, Lines(), 40));
    }

    [Fact]
    public async Task A_finished_show_is_enriched_with_model_digest_and_prompt_version()
    {
        var show = Analysed(Said);

        var result = await MakeEnricher().EnrichAsync(show, CancellationToken.None);

        Assert.Equal(EnrichmentStatus.Done, result.Status);
        var d = result.Derived!;
        Assert.Equal("qwen3.6:latest", d.Model);
        Assert.StartsWith("07d35212", d.ModelDigest);
        Assert.StartsWith("enrich-v1+", d.PromptVersion);
        Assert.Equal("Amy", Assert.Single(d.Hosts).Value);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile)));

        var request = Assert.Single(_llm.Requests);
        Assert.True(request.DisableThinking);
        Assert.Equal(0.1, request.Temperature);
        Assert.Equal(32768, request.NumCtx);
        Assert.Contains("Backseat Aux", request.User);
        Assert.Contains("[19:00:20] That was Mr Brightside by The Killers", request.User);
        Assert.Contains("Answer with JSON only", request.System);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, request.Schema.ValueKind);
    }

    [Fact]
    public async Task Ollama_down_leaves_the_show_pending_and_retries_later()
    {
        var show = Analysed(Said);
        _llm.Down = true;

        var pending = await MakeEnricher().EnrichAsync(show, CancellationToken.None);

        Assert.Equal(EnrichmentStatus.Pending, pending.Status);
        Assert.Equal(1, pending.Unavailable);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), pending.NextAttemptUtc);
        Assert.False(MakeEnricher().IsDue(show));

        _llm.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(MakeEnricher().IsDue(show));
        Assert.Equal(EnrichmentStatus.Done, (await MakeEnricher().EnrichAsync(show, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Invalid_answers_fail_after_three_attempts_and_keep_the_raw_reply()
    {
        var show = Analysed(Said);
        _llm.Respond = (_, _) => "not json at all";

        for (var i = 0; i < Enricher.MaxInvalidAttempts; i++)
        {
            await MakeEnricher().EnrichAsync(show, CancellationToken.None);
            _clock.Advance(TimeSpan.FromHours(7));
        }

        var stored = AnalysisJson.Read<ShowEnrichment>(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile))!;
        Assert.Equal(EnrichmentStatus.Failed, stored.Status);
        Assert.Equal(3, stored.Attempts);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), "enrichment.raw.txt")));
        Assert.False(MakeEnricher().IsDue(show));
    }

    [Fact]
    public async Task It_runs_again_only_when_the_transcript_or_prompt_changes()
    {
        var show = Analysed(Said);
        await MakeEnricher().EnrichAsync(show, CancellationToken.None);
        Assert.False(MakeEnricher().IsDue(show));
        await MakeEnricher().EnrichAsync(show, CancellationToken.None);
        Assert.Single(_llm.Requests);

        Analysed([.. Said, "One more line about the ball."]);
        Assert.True(MakeEnricher().IsDue(show));
        await MakeEnricher().EnrichAsync(show, CancellationToken.None);
        Assert.Equal(2, _llm.Requests.Count);
    }

    [Fact]
    public async Task A_long_transcript_is_sent_in_parts_and_the_claims_are_merged()
    {
        var show = Analysed(Said);
        _llm.Respond = (r, n) => n == 0 ? Answer : """{ "hosts": [ { "value": "AMY", "quote": "I'm Amy", "confidence": 0.5 } ], "summary": "" }""";

        var result = await MakeEnricher(Options with { MaxTranscriptChars = 150 }).EnrichAsync(show, CancellationToken.None);

        Assert.True(_llm.Requests.Count >= 2);
        Assert.Contains("part 1 of", _llm.Requests[0].User);
        Assert.Equal("Amy", Assert.Single(result.Derived!.Hosts).Value);
    }

    [Fact]
    public void Prompt_files_load_with_a_content_version_and_missing_files_are_named()
    {
        var a = EnrichmentPrompt.Load(ConfigDir, Options);
        Assert.Equal(a.Version, EnrichmentPrompt.Load(ConfigDir, Options).Version);
        Assert.Matches("^enrich-v1\\+[0-9a-f]{12}$", a.Version);
        Assert.Contains("enrich-v9.system.md", Assert.Throws<AnalysisConfigException>(() => EnrichmentPrompt.Load(ConfigDir, Options with { PromptName = "enrich-v9" })).Message);
    }

    [Fact]
    public async Task The_ollama_client_asks_for_structured_output_without_thinking()
    {
        string? chatBody = null;
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json("""{ "models": [ { "name": "qwen3.6:latest", "digest": "07d35212" } ] }"""),
            "/api/show" => Json("""{ "capabilities": ["completion", "thinking"] }"""),
            "/api/chat" => Capture(req, b => chatBody = b, """{ "message": { "role": "assistant", "content": "{}" }, "prompt_eval_count": 10, "eval_count": 5, "total_duration": 2000000000 }"""),
            _ => StubHandler.NotFound(),
        });
        var client = new OllamaClient(new HttpClient(handler), new Uri("http://host.docker.internal:11434"));

        var model = await client.DescribeAsync("qwen3.6:latest", CancellationToken.None);
        var schema = EnrichmentPrompt.Load(ConfigDir, Options).Schema;
        var reply = await client.ChatAsync(new LlmRequest("qwen3.6:latest", "sys", "user", schema, 0.1, 42, 32768, model!.Thinking), CancellationToken.None);

        Assert.Equal("07d35212", model.Digest);
        Assert.True(model.Thinking);
        Assert.Equal("{}", reply.Content);
        Assert.Equal(2.0, reply.Seconds, 6);
        Assert.Contains("\"think\":false", chatBody);
        Assert.Contains("\"format\":{", chatBody);
        Assert.Contains("\"num_ctx\":32768", chatBody);
        Assert.Contains("\"stream\":false", chatBody);
        Assert.Null(await client.DescribeAsync("missing:model", CancellationToken.None));
    }

    [Fact]
    public async Task The_ollama_client_reports_unreachable_and_missing_models_as_unavailable()
    {
        var down = new OllamaClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused"))), new Uri("http://host.docker.internal:11434"));
        await Assert.ThrowsAsync<LlmUnavailableException>(() => down.DescribeAsync("qwen3.6:latest", CancellationToken.None));

        var noModel = new OllamaClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"model 'x' not found\"}") })), new Uri("http://host.docker.internal:11434"));
        var schema = EnrichmentPrompt.Load(ConfigDir, Options).Schema;
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => noModel.ChatAsync(new LlmRequest("x", "s", "u", schema, 0, 1, 2048, false), CancellationToken.None));
        Assert.Contains("ollama pull x", ex.Message);
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    static HttpResponseMessage Capture(HttpRequestMessage req, Action<string> keep, string body)
    {
        keep(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return Json(body);
    }
}
