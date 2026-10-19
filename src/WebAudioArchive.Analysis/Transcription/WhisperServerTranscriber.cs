using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Enrichment;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>Settings of the whisper.cpp server client (decision D19).</summary>
/// <param name="BaseUrl">The server, for example http://pdl-rune-02.panoramicdata.com:8080/ (host rule: see AnalysisSettings).</param>
/// <param name="Model">The model the server was started with. whisper.cpp's server does not report it, so it is recorded for information only.</param>
/// <param name="Language">Overrides the language of the analysis config when set (WAA_WHISPER_LANGUAGE).</param>
/// <param name="Timeout">How long one request may take (at least 10 minutes in the settings).</param>
/// <param name="Concurrency">Requests in flight; the server handles one at a time.</param>
/// <param name="Token">Optional bearer token, sent as Authorization and never logged.</param>
/// <param name="Retries">Retries of a transient failure inside one chunk before the job waits.</param>
public sealed record WhisperServerOptions(Uri BaseUrl, string Model, string? Language, TimeSpan Timeout, int Concurrency, string? Token, int Retries)
{
    /// <summary>The largest answer read (a 300 s chunk is well under 1 MB of verbose_json).</summary>
    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>How many times the same chunk may end in the server's "failed to process audio" answer (a failed whisper_full, usually a busy GPU) before it counts as a failed attempt.</summary>
    public int MaxProcessFailures { get; init; } = 10;

    /// <summary>How long the readiness check (GET /health) may take.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The parsed answer plus what the live contract check needs to know about it.</summary>
public sealed record WhisperServerResult(IReadOnlyList<RawSegment> Segments, int SegmentsWithoutScores, int SegmentsWithoutWords);

/// <summary>
/// Speech to text over HTTP against a whisper.cpp server (decision D19): <c>POST /inference</c>, one request per chunk,
/// <c>verbose_json</c> with token times. The server runs on the owner's GPU servers, so the audio chunks leave this host;
/// the settings only accept loopback, private and explicitly allow-listed hosts and the client never follows a redirect,
/// uses no proxy and never logs a token, audio or transcript text.
///
/// A server that is unreachable, loading (503), behind a bad gateway (502, 504), out of GPU memory or too slow is a
/// <see cref="TransientTranscriptionException"/> after the retries (the job waits, no attempt is used). A 4xx, a redirect,
/// an answer that is not the expected JSON, an answer that is too large and a 500 that is not a memory error fail the
/// chunk (<see cref="TranscriptionFailedException"/>, an attempt is used). A cancellation from the caller aborts the
/// request and is never retried.
/// </summary>
public sealed class WhisperServerTranscriber : ITranscriber
{
    /// <summary>The longest wait between retries.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    static readonly string[] MemoryMarkers = ["out of memory", "cuda", "cublas", "alloc", "memory"];

    readonly HttpClient _http;
    readonly WhisperServerOptions _options;
    readonly ILogger? _log;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Func<double> _jitter;
    readonly SemaphoreSlim _gate;
    readonly string _host;
    readonly Uri _baseUrl;
    readonly object _processFailuresLock = new();
    readonly Dictionary<string, int> _processFailures = [];
    readonly Queue<string> _processFailureOrder = new();

    /// <summary>The most chunks remembered by the process-failure counter (oldest forgotten first).</summary>
    const int MaxRememberedChunks = 512;
    volatile string _device = "remote";

    public WhisperServerTranscriber(HttpClient http, WhisperServerOptions options, ILogger? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? jitter = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _delay = delay ?? Task.Delay;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        _host = $"{options.BaseUrl.Host}:{options.BaseUrl.Port}";
        _baseUrl = WithTrailingSlash(options.BaseUrl);
    }

    /// <summary>For information, recorded in the transcript: the engine and the server's host and port (never the path or any user info).</summary>
    public string Engine => $"whisper.cpp server at {_host}";

    /// <summary>The engine part of the job version. A stored "whisper.net 1" counts as equal (<see cref="Work.JobRules"/>): the same whisper.cpp engine and model file.</summary>
    public string Identity => "whisper.cpp";

    public string Model => _options.Model;

    /// <summary>"remote" unless the server's /health says where it runs ("device"; whisper.cpp v1.9.5 does not).</summary>
    public string Device => _device;

    /// <summary>Why the last readiness check failed, for the log; null when ready.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The only way the service builds this client's HttpClient: no automatic redirects (a 307 or 308 would resend the audio to
    /// another host), no proxy (the audio goes straight to the configured host), pooled connections that follow DNS changes,
    /// and a timeout that is a backstop behind the per-request timeout (at least 10 minutes).
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        if (timeout < OllamaClient.MinTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"the whisper timeout must be at least {OllamaClient.MinTimeout.TotalMinutes:0} minutes");
        return new HttpClient(CreateHandler()) { Timeout = timeout + TimeSpan.FromMinutes(1) };
    }

    /// <summary>The handler behind <see cref="CreateHttpClient"/>: no redirects, no proxy, pooled connections that follow DNS changes.</summary>
    internal static SocketsHttpHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>2 s, 4 s, 8 s, 16 s, capped at 30 s, plus up to a quarter of that again as jitter (<paramref name="jitter01"/> from 0 to 1).</summary>
    public static TimeSpan Backoff(int retry, double jitter01)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, 2 * Math.Pow(2, Math.Max(0, retry - 1)));
        return TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, seconds * (1 + 0.25 * Math.Clamp(jitter01, 0, 1))));
    }

    /// <summary>A base URL with a path prefix and no trailing slash would lose the prefix when a relative path is added; "http://h/whisper" becomes "http://h/whisper/".</summary>
    public static Uri WithTrailingSlash(Uri url) =>
        url.AbsolutePath.EndsWith('/') ? url : new UriBuilder(url) { Path = url.AbsolutePath + "/" }.Uri;

    Uri Url(string path) => new(_baseUrl, path);

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ReadyTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("health"));
            Authorize(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"the whisper server at {_host} answered {(int)response.StatusCode} to /health (loading the model, or not healthy)";
                return false;
            }

            var body = await ReadLimitedAsync(response.Content, 64 * 1024, timeout.Token);
            if (TryReadDevice(body) is { } device) _device = device;
            LastError = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LastError = $"the whisper server at {_host} did not answer /health within {_options.ReadyTimeout.TotalSeconds:0} seconds";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ResponseTooLargeException)
        {
            ct.ThrowIfCancellationRequested();
            LastError = $"the whisper server at {_host} is not reachable: {Describe(ex)}";
            return false;
        }
    }

    static string? TryReadDevice(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("device", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                && d.GetString() is { Length: > 0 and <= 64 } text
                    ? text
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct) =>
        (await InspectAsync(request, ct)).Segments;

    /// <summary>Transcribes one chunk and also reports what the answer lacked (used by the live contract check).</summary>
    public async Task<WhisperServerResult> InspectAsync(TranscriptionRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var chunk = Convert.ToHexString(SHA256.HashData(request.Wav));
            for (var retry = 0; ; retry++)
            {
                ct.ThrowIfCancellationRequested();
                var (result, failure) = await AttemptAsync(request, ct);
                if (result is not null)
                {
                    ForgetProcessFailures(chunk);
                    return result;
                }

                if (failure!.Final is { } final) throw final;
                if (retry >= _options.Retries) throw failure.ProcessFailure ? ProcessFailureAfterRetries(chunk, failure) : failure.AfterRetries();

                var wait = Backoff(retry + 1, _jitter());
                _log?.LogWarning("The whisper server at {Host} failed ({Reason}); retry {Retry} of {Max} in {Seconds:0.#} s", _host, failure.Reason, retry + 1, _options.Retries, wait.TotalSeconds);
                await _delay(wait, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    sealed record Failure(string Reason, Exception? Final, bool Transient, bool MemoryError = false, bool ProcessFailure = false)
    {
        public Exception AfterRetries() => Transient || MemoryError
            ? new TransientTranscriptionException($"the whisper server is not available: {Reason}")
            : new TranscriptionFailedException($"the whisper server could not transcribe this chunk: {Reason}");
    }

    /// <summary>
    /// The server answered 500 {"error":"failed to process audio"}: whisper_full failed, which on a GPU shared with Ollama usually
    /// means contention, not a bad chunk. That waits (transient, no attempt used). The same chunk failing this way more than
    /// WAA_WHISPER_MAX_PROCESS_FAILURES times (counted per chunk, by the hash of its audio, in memory: it starts again after a restart
    /// and a success clears it) becomes a counted failure so a chunk that can never be processed is eventually parked.
    /// </summary>
    Exception ProcessFailureAfterRetries(string chunk, Failure failure)
    {
        int count;
        lock (_processFailuresLock)
        {
            count = _processFailures.GetValueOrDefault(chunk) + 1;
            if (!_processFailures.ContainsKey(chunk))
            {
                _processFailureOrder.Enqueue(chunk);
                while (_processFailureOrder.Count > MaxRememberedChunks) _processFailures.Remove(_processFailureOrder.Dequeue());
            }

            _processFailures[chunk] = count;
        }

        if (count > _options.MaxProcessFailures)
        {
            return new TranscriptionFailedException(
                $"the whisper server at {_host} failed to process the audio of this chunk {count} times (answer: failed to process audio), so it is treated as a bad chunk. Raise WAA_WHISPER_MAX_PROCESS_FAILURES if the GPU is only busy");
        }

        return failure.AfterRetries();
    }

    void ForgetProcessFailures(string chunk)
    {
        lock (_processFailuresLock) _processFailures.Remove(chunk);
    }

    MultipartFormDataContent Build(TranscriptionRequest request)
    {
        var file = new ByteArrayContent(request.Wav);
        file.Headers.ContentType = new("audio/wav");
        var form = new MultipartFormDataContent { { file, "file", "chunk.wav" } };
        void Field(string name, string value) => form.Add(new StringContent(value, Encoding.UTF8), name);
        Field("response_format", "verbose_json");
        Field("language", _options.Language ?? request.Language);
        if (request.Prompt.Length > 0)
        {
            Field("prompt", request.Prompt);
            Field("carry_initial_prompt", "true");
        }

        Field("temperature", "0");
        Field("temperature_inc", "0.2");
        Field("token_timestamps", "true");
        Field("no_language_probabilities", "true");
        return form;
    }

    void Authorize(HttpRequestMessage message)
    {
        if (_options.Token is { Length: > 0 } token) message.Headers.Authorization = new("Bearer", token);
    }

    async Task<(WhisperServerResult? Result, Failure? Failure)> AttemptAsync(TranscriptionRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Url("inference")) { Content = Build(request) };
            Authorize(message);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                return (null, new Failure($"answered {status}, a redirect; redirects are not followed because the audio would go to another host. Check WAA_WHISPER_URL", Final: Failed($"the whisper server at {_host} answered {status}, a redirect. Redirects are not followed because the audio would be sent to another host; check WAA_WHISPER_URL"), Transient: false));
            }

            var body = await ReadLimitedAsync(response.Content, _options.MaxResponseBytes, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return (WhisperServerResponse.Parse(body), null);
                }
                catch (WhisperServerResponse.FormatException ex)
                {
                    return (null, new Failure(ex.Message, Failed($"the whisper server at {_host} did not answer with the expected verbose_json: {ex.Message}"), Transient: false));
                }
            }

            var excerpt = Excerpt(body);
            if (status is 502 or 503 or 504)
                return (null, new Failure($"{_host} answered {status}{(excerpt.Length > 0 ? ": " + excerpt : "")}", Final: null, Transient: true));
            if (status >= 500)
            {
                if (body.Contains("failed to process audio", StringComparison.OrdinalIgnoreCase))
                    return (null, new Failure($"{_host} answered {status}: {excerpt}", Final: null, Transient: true, ProcessFailure: true));
                var memory = MemoryMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));
                return (null, new Failure($"{_host} answered {status}{(excerpt.Length > 0 ? ": " + excerpt : "")}", Final: null, Transient: false, MemoryError: memory));
            }

            return (null, new Failure($"answered {status}", Failed($"the whisper server at {_host} refused this chunk with {status}{(excerpt.Length > 0 ? ": " + excerpt : "")}"), Transient: false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, new Failure($"{_host} did not answer within {_options.Timeout.TotalMinutes:0.##} minutes", null, Transient: true) { });
        }
        catch (ResponseTooLargeException ex)
        {
            return (null, new Failure(ex.Message, Failed($"the whisper server at {_host} sent {ex.Message}"), Transient: false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested(); // a caller's cancellation can surface as an IO error: it is a cancellation, never "unavailable"
            return (null, new Failure($"{_host} is not reachable: {Describe(ex)}", null, Transient: true));
        }
    }

    static Exception Failed(string message) => new TranscriptionFailedException(message);

    /// <summary>The server's own message (whisper.cpp says "Invalid request" and the like; it never echoes transcript text), short and on one line.</summary>
    static string Excerpt(string body)
    {
        var text = string.Join(' ', body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    /// <summary>The reason for a log line or a message: the exception's own text, never the request (so never the token or the audio).</summary>
    static string Describe(Exception ex) => (ex.InnerException?.Message ?? ex.Message) is { Length: > 0 } m ? m : ex.GetType().Name;

    sealed class ResponseTooLargeException(int limit) : Exception($"an answer larger than {limit} bytes");

    static async Task<string> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new ResponseTooLargeException(limit);
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new ResponseTooLargeException(limit);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
