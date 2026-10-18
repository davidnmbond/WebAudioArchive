using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The HTTP client for the whisper.cpp server (decision D19, Task 20): request shape, mapping, errors, retries, limits.</summary>
public sealed class WhisperServerTranscriberTests
{
    static readonly Uri Base = new("http://pdl-rune-02.panoramicdata.com:8080/");

    static WhisperServerOptions Options(Uri? url = null, string? token = null, int retries = 3, int concurrency = 1, TimeSpan? timeout = null) =>
        new(url ?? Base, "ggml-large-v3-turbo-q8_0", null, timeout ?? TimeSpan.FromSeconds(30), concurrency, token, retries);

    static TranscriptionRequest Request(string prompt = "Um, er, Burst Radio.") => new(WavFile.Build(16000, 1, new byte[3200]), prompt, "en");

    sealed class Delays
    {
        public List<TimeSpan> Waited { get; } = [];

        public Task Wait(TimeSpan span, CancellationToken ct)
        {
            lock (Waited) Waited.Add(span);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception) + " " + exception);
        }
    }

    static WhisperServerTranscriber Make(StubHttpHandler handler, WhisperServerOptions? options = null, ILogger? log = null, Delays? delays = null) =>
        new(new HttpClient(handler), options ?? Options(), log, (delays ?? new Delays()).Wait, () => 0);

    static StubHttpHandler Ok(string body = StubWhisperServer.CannedJson) => new(_ => StubHttpHandler.Json(body));

    // ---- Step 1: the request ----

    [Fact]
    public async Task Request_is_multipart_with_the_wav_unchanged_and_the_options()
    {
        var handler = Ok();
        var request = Request();

        await Make(handler).TranscribeAsync(request, CancellationToken.None);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/inference", seen.Path);
        Assert.Equal(request.Wav, seen.File);
        Assert.Equal("chunk.wav", seen.FileName);
        Assert.Equal("audio/wav", seen.FileType);
        Assert.Equal("verbose_json", seen.Fields["response_format"]);
        Assert.Equal("en", seen.Fields["language"]);
        Assert.Equal("Um, er, Burst Radio.", seen.Fields["prompt"]);
        Assert.Equal("true", seen.Fields["carry_initial_prompt"]);
        Assert.Equal("0", seen.Fields["temperature"]);
        Assert.Equal("0.2", seen.Fields["temperature_inc"]);
        Assert.Equal("true", seen.Fields["token_timestamps"]);
        Assert.Equal("true", seen.Fields["no_language_probabilities"]);
    }

    [Fact]
    public async Task Empty_prompt_is_not_sent()
    {
        var handler = Ok();

        await Make(handler).TranscribeAsync(Request(prompt: ""), CancellationToken.None);

        var fields = Assert.Single(handler.Seen).Fields;
        Assert.DoesNotContain("prompt", fields.Keys);
        Assert.DoesNotContain("carry_initial_prompt", fields.Keys);
    }

    [Fact]
    public async Task The_language_setting_overrides_the_config_language_and_the_url_may_have_a_path_prefix()
    {
        var handler = Ok();
        var options = Options(new Uri("http://127.0.0.1:9/whisper/")) with { Language = "fr" };

        await Make(handler, options).TranscribeAsync(Request(), CancellationToken.None);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal("fr", seen.Fields["language"]);
        Assert.Equal("/whisper/inference", seen.Path);
    }

    [Fact]
    public async Task Token_is_sent_only_when_configured()
    {
        var without = Ok();
        await Make(without).TranscribeAsync(Request(), CancellationToken.None);
        Assert.Null(Assert.Single(without.Seen).Authorization);

        var with = Ok();
        await Make(with, Options(token: "s3cret-token")).TranscribeAsync(Request(), CancellationToken.None);
        Assert.Equal("Bearer s3cret-token", Assert.Single(with.Seen).Authorization);
    }

    [Fact]
    public async Task The_token_and_the_transcript_never_reach_the_log_or_an_error_message()
    {
        var log = new ListLogger();
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{\"error\":\"nope\"}", HttpStatusCode.ServiceUnavailable));
        var t = Make(handler, Options(token: "s3cret-token", retries: 1), log);

        var ex = await Assert.ThrowsAsync<TransientTranscriptionException>(() => t.TranscribeAsync(Request(), CancellationToken.None));
        var ok = Make(Ok(), Options(token: "s3cret-token"), log);
        await ok.TranscribeAsync(Request(), CancellationToken.None);
        await ok.IsReadyAsync(CancellationToken.None);

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("s3cret-token") || l.Contains("hello and welcome") || l.Contains("Burst Radio"));
        Assert.DoesNotContain("s3cret-token", ex.Message + ex);
    }

    // ---- Step 2: the response ----

    [Fact]
    public async Task Tokens_merge_into_words_with_times_and_lowest_probability_and_shift_with_the_chunk()
    {
        var segments = await Make(Ok()).TranscribeAsync(Request(), CancellationToken.None);

        var s = Assert.Single(segments);
        Assert.Equal("Um, hello and welcome.", s.Text);
        Assert.Equal(new[] { "Um,", "hello", "and", "welcome." }, s.Words.Select(w => w.Text));
        Assert.Equal(0.1, s.Words[0].Start, 6);
        Assert.Equal(0.32, s.Words[0].End, 6);
        Assert.Equal(0.7, s.Words[3].Probability, 6);
        Assert.Equal(0, s.Start);
        Assert.Equal(3.5, s.End);
        Assert.Equal(-0.159, s.AvgLogProb, 6);
        Assert.Equal(0, s.NoSpeechProb, 6);

        var shifted = s.Shift(300);
        Assert.Equal(300.1, shifted.Words[0].Start, 6);
        Assert.Equal(303.5, shifted.End, 6);
    }

    [Fact]
    public async Task Segment_scores_feed_the_hallucination_filter_unchanged()
    {
        const string body = """{"segments":[{"text":" Thanks.","start":1,"end":2,"words":[],"avg_logprob":-1.2,"no_speech_prob":0.8}]}""";
        var o = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json")).Config.Transcription;

        var segments = await Make(Ok(body)).TranscribeAsync(Request(), CancellationToken.None);

        var reasons = TranscriptFilter.Reasons([.. segments.Select(s => (s.Text, s.NoSpeechProb, s.AvgLogProb))], o);
        Assert.Equal(TranscriptFilter.NoSpeech, Assert.Single(reasons));
    }

    [Fact]
    public async Task A_segment_without_words_or_scores_still_maps_and_is_counted_by_the_inspection()
    {
        const string body = """{"segments":[{"text":" Hi.","start":1,"end":2}]}""";

        var result = await Make(Ok(body)).InspectAsync(Request(), CancellationToken.None);

        var s = Assert.Single(result.Segments);
        Assert.Empty(s.Words);
        Assert.Equal(1, result.SegmentsWithoutWords);
        Assert.Equal(1, result.SegmentsWithoutScores);
    }

    [Fact]
    public async Task Empty_segments_is_an_empty_result()
    {
        Assert.Empty(await Make(Ok("""{"segments":[]}""")).TranscribeAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"error":"something"}""")]
    [InlineData("""{"segments":"no"}""")]
    [InlineData("""[1,2,3]""")]
    public async Task A_body_that_is_not_the_expected_json_fails_the_chunk(string body)
    {
        var handler = Ok(body);
        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() => Make(handler).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Contains("verbose_json", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    // ---- Step 3: errors, retries, backoff, cancellation ----

    [Fact]
    public async Task Unreachable_server_is_transient_after_the_retries()
    {
        var delays = new Delays();
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused)));

        var ex = await Assert.ThrowsAsync<TransientTranscriptionException>(() => Make(handler, Options(retries: 3), delays: delays).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(4, handler.Calls);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], delays.Waited);
        Assert.Contains("pdl-rune-02.panoramicdata.com", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Gateway_and_loading_statuses_are_transient_after_the_retries(HttpStatusCode status)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{\"status\":\"loading model\"}", status));

        await Assert.ThrowsAsync<TransientTranscriptionException>(() => Make(handler, Options(retries: 2)).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_server_that_recovers_during_the_retries_gives_the_answer()
    {
        var n = 0;
        var handler = new StubHttpHandler(_ => ++n < 3 ? StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : StubHttpHandler.Json(StubWhisperServer.CannedJson));

        var segments = await Make(handler).TranscribeAsync(Request(), CancellationToken.None);

        Assert.Single(segments);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Timeout_is_transient_not_failed_and_not_cancellation()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpHandler.Json("{}");
        });

        var ex = await Assert.ThrowsAsync<TransientTranscriptionException>(() =>
            Make(handler, Options(retries: 1, timeout: TimeSpan.FromMilliseconds(150))).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(2, handler.Calls);
        Assert.Contains("did not answer within", ex.Message);
    }

    [Fact]
    public async Task Server_out_of_memory_is_transient()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("CUDA error: out of memory") });

        await Assert.ThrowsAsync<TransientTranscriptionException>(() => Make(handler, Options(retries: 1)).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Another_500_is_retried_and_then_fails_the_chunk_so_a_poison_chunk_cannot_loop_for_ever()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() => Make(handler, Options(retries: 1)).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(2, handler.Calls);
        Assert.Contains("500", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Invalid request")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "too big")]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "type")]
    [InlineData(HttpStatusCode.Unauthorized, "denied")]
    [InlineData(HttpStatusCode.NotFound, "no route")]
    public async Task A_4xx_fails_the_chunk_at_once_with_a_clear_message(HttpStatusCode status, string serverSays)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(serverSays) });
        var delays = new Delays();

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() => Make(handler, delays: delays).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays.Waited);
        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Contains(serverSays, ex.Message);
        Assert.Contains("pdl-rune-02.panoramicdata.com", ex.Message);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_fails_the_chunk()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("http://elsewhere.example.com/inference");
            return r;
        });

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() => Make(handler).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elsewhere.example.com", ex.Message);
    }

    [Fact]
    public async Task Cancellation_is_not_retried_and_aborts_the_request()
    {
        var aborted = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                aborted.SetResult();
                throw;
            }

            return StubHttpHandler.Json("{}");
        });
        var delays = new Delays();
        using var cts = new CancellationTokenSource();

        var task = Make(handler, delays: delays).TranscribeAsync(Request(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays.Waited);
    }

    [Fact]
    public async Task Cancellation_during_the_backoff_stops_at_once()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        using var cts = new CancellationTokenSource();
        var t = new WhisperServerTranscriber(new HttpClient(handler), Options(), null, async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, () => 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.TranscribeAsync(Request(), cts.Token));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        var waits = Enumerable.Range(1, 7).Select(n => WhisperServerTranscriber.Backoff(n, 0).TotalSeconds).ToArray();

        Assert.Equal([2, 4, 8, 16, 30, 30, 30], waits);
        Assert.Equal(2.5, WhisperServerTranscriber.Backoff(1, 1).TotalSeconds, 6);
        Assert.Equal(30, WhisperServerTranscriber.Backoff(9, 0.9).TotalSeconds, 6);
    }

    // ---- the response is size limited ----

    [Fact]
    public async Task An_oversize_response_with_a_content_length_is_refused()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(new string(' ', 5000) + "{\"segments\":[]}"));

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() =>
            Make(handler, Options() with { MaxResponseBytes = 1000 }).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Contains("larger than 1000 bytes", ex.Message);
    }

    [Fact]
    public async Task An_oversize_response_without_a_content_length_is_cut_off()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() });

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() =>
            Make(handler, Options() with { MaxResponseBytes = 10_000 }).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Contains("larger than 10000 bytes", ex.Message);
    }

    sealed class EndlessContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var block = new byte[4096];
            Array.Fill(block, (byte)' ');
            while (true) await stream.WriteAsync(block);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new EndlessStream());
    }

    sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)' ');
            return ValueTask.FromResult(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- Step 4: readiness and identity ----

    [Fact]
    public async Task Health_ok_is_ready_and_the_reported_device_is_kept()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"status":"ok","device":"CUDA0"}"""));
        var t = Make(handler);

        Assert.True(await t.IsReadyAsync(CancellationToken.None));

        Assert.Equal("/health", Assert.Single(handler.Seen).Path);
        Assert.Equal(HttpMethod.Get, handler.Seen[0].Method);
        Assert.Null(t.LastError);
        Assert.Equal("CUDA0", t.Device);
    }

    [Fact]
    public async Task Health_503_is_not_ready_with_a_reason()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"status":"loading model"}""", HttpStatusCode.ServiceUnavailable));
        var t = Make(handler);

        Assert.False(await t.IsReadyAsync(CancellationToken.None));

        Assert.Contains("503", t.LastError);
        Assert.Contains("pdl-rune-02.panoramicdata.com", t.LastError);
    }

    [Fact]
    public async Task A_down_server_is_not_ready_and_does_not_throw()
    {
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("refused"));
        var t = Make(handler);

        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.NotNull(t.LastError);
    }

    [Fact]
    public async Task A_health_check_that_hangs_gives_up_after_the_ready_timeout()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpHandler.Json("{}");
        });
        var t = Make(handler, Options() with { ReadyTimeout = TimeSpan.FromMilliseconds(100) });

        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.Contains("did not answer", t.LastError);
    }

    [Fact]
    public async Task A_health_check_stopped_by_the_caller_is_a_cancellation()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpHandler.Json("{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(handler).IsReadyAsync(cts.Token));
    }

    [Fact]
    public void Identity_names_the_engine_the_server_host_and_the_configured_model_but_the_version_key_part_is_just_whisper_cpp()
    {
        var t = Make(Ok());

        Assert.Equal("whisper.cpp", t.Identity);
        Assert.StartsWith("whisper.cpp server", t.Engine);
        Assert.Contains("pdl-rune-02.panoramicdata.com:8080", t.Engine);
        Assert.Equal("ggml-large-v3-turbo-q8_0", t.Model);
        Assert.Equal("remote", t.Device);
        ITranscriber it = t;
        Assert.Null(it.ModelBytes);
        Assert.Equal("ggml-large-v3-turbo-q8_0|unknown", it.ModelIdentity);
        it.ResetModel();
    }

    [Fact]
    public void The_engine_never_holds_user_info_or_a_path_from_the_url()
    {
        var t = Make(Ok(), Options(new Uri("http://user:pw@127.0.0.1:8178/secret/path/")));

        Assert.DoesNotContain("pw", t.Engine);
        Assert.DoesNotContain("secret", t.Engine);
        Assert.Contains("127.0.0.1:8178", t.Engine);
    }

    // ---- Step 5: concurrency ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Requests_in_flight_never_exceed_the_concurrency(int concurrency, int expectedMax)
    {
        var gate = new TaskCompletionSource();
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return StubHttpHandler.Json(StubWhisperServer.CannedJson);
        });
        var t = Make(handler, Options(concurrency: concurrency));

        var tasks = Enumerable.Range(0, 4).Select(_ => t.TranscribeAsync(Request(), CancellationToken.None)).ToList();
        await Task.Delay(300);
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(expectedMax, handler.MaxInFlight);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task A_waiting_chunk_can_be_cancelled_while_it_waits_for_the_server()
    {
        var gate = new TaskCompletionSource();
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return StubHttpHandler.Json(StubWhisperServer.CannedJson);
        });
        var t = Make(handler);
        var first = t.TranscribeAsync(Request(), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.TranscribeAsync(Request(), cts.Token));

        gate.SetResult();
        await first;
        Assert.Equal(1, handler.Calls);
    }

    // ---- the client ----

    [Fact]
    public void The_http_client_does_not_follow_redirects_or_use_a_proxy_and_waits_at_least_ten_minutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WhisperServerTranscriber.CreateHttpClient(TimeSpan.FromMinutes(9)));

        using var client = WhisperServerTranscriber.CreateHttpClient(TimeSpan.FromMinutes(10));
        Assert.True(client.Timeout >= TimeSpan.FromMinutes(10));
    }

    // ---- a real HTTP server ----

    static WhisperServerTranscriber Real(Uri url, WhisperServerOptions? options = null, Delays? delays = null) =>
        new(WhisperServerTranscriber.CreateHttpClient(TimeSpan.FromMinutes(10)), options ?? Options(url), null, (delays ?? new Delays()).Wait, () => 0);

    [Fact]
    public async Task Multipart_body_round_trips_through_a_real_server()
    {
        await using var server = await StubWhisperServer.StartAsync();
        var request = Request();

        var segments = await Real(server.BaseUrl, Options(server.BaseUrl, token: "tok")).TranscribeAsync(request, CancellationToken.None);

        var seen = Assert.Single(server.Seen);
        Assert.Equal(request.Wav, seen.File);
        Assert.Equal("chunk.wav", seen.FileName);
        Assert.Equal("audio/wav", seen.FileType);
        Assert.Equal("verbose_json", seen.Fields["response_format"]);
        Assert.Equal("Um, er, Burst Radio.", seen.Fields["prompt"]);
        Assert.Equal("Bearer tok", seen.Authorization);
        Assert.Equal("Um, hello and welcome.", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task Large_chunk_of_10_MB_is_accepted()
    {
        await using var server = await StubWhisperServer.StartAsync();
        var wav = WavFile.Build(16000, 1, new byte[10 * 1024 * 1024]);

        await Real(server.BaseUrl).TranscribeAsync(new TranscriptionRequest(wav, "p", "en"), CancellationToken.None);

        Assert.Equal(wav.Length, Assert.Single(server.Seen).File!.Length);
    }

    [Fact]
    public async Task Slow_server_hits_the_timeout_and_is_transient()
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted);

        var ex = await Assert.ThrowsAsync<TransientTranscriptionException>(() =>
            Real(server.BaseUrl, Options(server.BaseUrl, retries: 0, timeout: TimeSpan.FromMilliseconds(300))).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Contains("did not answer within", ex.Message);
    }

    [Fact]
    public async Task Connection_dropped_mid_response_is_transient()
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength = 100_000;
            await ctx.Response.WriteAsync("{\"segments\":[");
            await ctx.Response.Body.FlushAsync();
            ctx.Abort();
        };

        await Assert.ThrowsAsync<TransientTranscriptionException>(() => Real(server.BaseUrl, Options(server.BaseUrl, retries: 1)).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Connection_refused_is_transient_and_not_ready()
    {
        var url = StubWhisperServer.ClosedPort();
        var t = Real(url, Options(url, retries: 1));

        await Assert.ThrowsAsync<TransientTranscriptionException>(() => t.TranscribeAsync(Request(), CancellationToken.None));
        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.NotNull(t.LastError);
    }

    [Fact]
    public async Task Ready_against_a_real_health_route_and_not_ready_while_it_loads()
    {
        await using var server = await StubWhisperServer.StartAsync();
        var t = Real(server.BaseUrl);
        Assert.True(await t.IsReadyAsync(CancellationToken.None));

        server.Health = ctx =>
        {
            ctx.Response.StatusCode = 503;
            return ctx.Response.WriteAsync("{\"status\":\"loading model\"}");
        };
        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.Contains("503", t.LastError);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_the_second_host_gets_no_request()
    {
        await using var target = await StubWhisperServer.StartAsync();
        await using var first = await StubWhisperServer.StartAsync();
        first.Inference = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = new Uri(target.BaseUrl, "inference").ToString();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<TranscriptionFailedException>(() => Real(first.BaseUrl).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(1, first.Requests);
        Assert.Equal(0, target.Requests);
    }

    [Fact]
    public async Task Concurrency_one_serialises_two_simultaneous_chunks_against_a_real_server()
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = async ctx =>
        {
            await Task.Delay(200);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(StubWhisperServer.CannedJson);
        };
        var t = Real(server.BaseUrl);

        await Task.WhenAll(t.TranscribeAsync(Request(), CancellationToken.None), t.TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(2, server.Requests);
        Assert.Equal(1, server.MaxInFlight);
    }

    // ---- the version key ----

    [Fact]
    public void A_transcript_made_by_the_old_engine_stays_current_under_this_one()
    {
        // The earlier ruling: the engine, model and device are not part of the key; only the package major and the transcript options are.
        var t = Make(Ok());
        var old = new Analysis.Work.JobState(1, "a/2026/10-15/190000", "fp", "whisper.net 1|opts", "d1", WebAudioArchive.Core.Analysis.JobStatus.Done, 0, null, null, [0], 1, DateTimeOffset.UnixEpoch, 3600, 10, 20);

        var decision = Analysis.Work.JobRules.Decide(old, "fp", $"{t.Identity}|opts", "d1", DateTimeOffset.UnixEpoch);

        Assert.Equal(Analysis.Work.JobAction.Skip, decision.Action);
        Assert.Equal(Analysis.Work.JobAction.Run, Analysis.Work.JobRules.Decide(old, "fp", $"{t.Identity}|other-options", "d1", DateTimeOffset.UnixEpoch).Action);
        Assert.Equal(Analysis.Work.JobAction.Run, Analysis.Work.JobRules.Decide(old with { TranscriptVersion = "whisper.net 2|opts" }, "fp", $"{t.Identity}|opts", "d1", DateTimeOffset.UnixEpoch).Action);
    }
}
