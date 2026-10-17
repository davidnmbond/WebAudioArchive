using System.Net.Http.Json;
using System.Text.Json;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// Ollama's HTTP API (0.40): /api/tags for the digest, /api/show for capabilities, /api/chat with a JSON schema in
/// "format", stream off, a fixed seed and low temperature, "think": false for thinking models (the reasoning text is
/// not wanted and costs minutes), and keep_alive 10 minutes so a run of shows does not reload the model each time.
/// </summary>
public sealed class OllamaClient(HttpClient http, Uri baseUrl) : ILlmClient
{
    public const int MaxReplyBytes = 4 * 1024 * 1024;

    /// <summary>The shortest request timeout allowed: an enrichment of a long show takes minutes, and the HttpClient default is 100 s.</summary>
    public static readonly TimeSpan MinTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The only way the service builds the client's HttpClient: no automatic redirects (a 307 or 308 would resend the POST body, the
    /// transcript, to another host), no proxy (the transcript goes straight to the configured host) and a timeout of at least
    /// <see cref="MinTimeout"/>.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        if (timeout < MinTimeout) throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"the Ollama timeout must be at least {MinTimeout.TotalMinutes:0} minutes");
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = timeout };
    }

    HttpRequestMessage Post(string path, object body) => new(HttpMethod.Post, new Uri(baseUrl, path)) { Content = JsonContent.Create(body) };

    /// <summary>An untagged name matches its :latest model (Ollama does the same).</summary>
    static bool NameMatches(string? name, string model) =>
        name == model || (!model.Contains(':') && name == model + ":latest");

    static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxReplyBytes) throw new EnrichmentInvalidException($"Ollama's reply is larger than {MaxReplyBytes} bytes");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxReplyBytes) throw new EnrichmentInvalidException($"Ollama's reply is larger than {MaxReplyBytes} bytes");
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
    public async Task<LlmModel?> DescribeAsync(string model, CancellationToken ct)
    {
        try
        {
            using var tags = await http.GetAsync(new Uri(baseUrl, "api/tags"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!tags.IsSuccessStatusCode) throw new LlmUnavailableException($"Ollama at {baseUrl} answered {(int)tags.StatusCode} to /api/tags");
            using var tagsDoc = JsonDocument.Parse(await ReadLimitedAsync(tags.Content, ct));
            string? digest = null;
            var found = false;
            if (tagsDoc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && NameMatches(name.GetString(), model))
                    {
                        found = true;
                        digest = m.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    }
                }
            }

            if (!found) return null;
            var thinking = false;
            using var show = await http.SendAsync(Post("api/show", new { model }), HttpCompletionOption.ResponseHeadersRead, ct);
            if (show.IsSuccessStatusCode)
            {
                using var showDoc = JsonDocument.Parse(await ReadLimitedAsync(show.Content, ct));
                if (showDoc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                    thinking = caps.EnumerateArray().Any(c => c.GetString() == "thinking");
            }

            return new LlmModel(model, digest, thinking);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} did not answer in time", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} answered with something that is not JSON: {ex.Message}", ex);
        }
    }

    public async Task<LlmReply> ChatAsync(LlmRequest r, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = r.Model,
            ["stream"] = false,
            ["format"] = r.Schema,
            ["keep_alive"] = "10m",
            ["options"] = new Dictionary<string, object> { ["temperature"] = r.Temperature, ["seed"] = r.Seed, ["num_ctx"] = r.NumCtx, ["num_predict"] = r.NumPredict },
            ["messages"] = new object[] { new { role = "system", content = r.System }, new { role = "user", content = r.User } },
        };
        if (r.DisableThinking) body["think"] = false;

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(Post("api/chat", body), HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} did not answer in time", ex);
        }

        using (response)
        {
            var text = await ReadLimitedAsync(response.Content, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new LlmUnavailableException($"Ollama has no model '{r.Model}' ({text.Trim()}); on the host run: ollama pull {r.Model}");
            if ((int)response.StatusCode >= 500)
                throw new LlmUnavailableException($"Ollama answered {(int)response.StatusCode}: {text.Trim()}");
            if (!response.IsSuccessStatusCode)
                throw new EnrichmentInvalidException($"Ollama refused the request ({(int)response.StatusCode}): {text.Trim()}");

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var content = root.GetProperty("message").GetProperty("content").GetString() ?? "";
                int Count(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                var seconds = root.TryGetProperty("total_duration", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() / 1e9 : 0;
                return new LlmReply(content, Count("prompt_eval_count"), Count("eval_count"), seconds);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new EnrichmentInvalidException($"Ollama's reply was not the expected chat response: {ex.Message}");
            }
        }
    }
}
