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
    public async Task<LlmModel?> DescribeAsync(string model, CancellationToken ct)
    {
        try
        {
            using var tags = await http.GetAsync(new Uri(baseUrl, "api/tags"), ct);
            if (!tags.IsSuccessStatusCode) throw new LlmUnavailableException($"Ollama at {baseUrl} answered {(int)tags.StatusCode} to /api/tags");
            using var tagsDoc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(ct));
            string? digest = null;
            var found = false;
            if (tagsDoc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) && name.GetString() == model)
                    {
                        found = true;
                        digest = m.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    }
                }
            }

            if (!found) return null;
            var thinking = false;
            using var show = await http.PostAsJsonAsync(new Uri(baseUrl, "api/show"), new { model }, ct);
            if (show.IsSuccessStatusCode)
            {
                using var showDoc = JsonDocument.Parse(await show.Content.ReadAsStringAsync(ct));
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
            ["options"] = new Dictionary<string, object> { ["temperature"] = r.Temperature, ["seed"] = r.Seed, ["num_ctx"] = r.NumCtx },
            ["messages"] = new object[] { new { role = "system", content = r.System }, new { role = "user", content = r.User } },
        };
        if (r.DisableThinking) body["think"] = false;

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(baseUrl, "api/chat"), body, ct);
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
            var text = await response.Content.ReadAsStringAsync(ct);
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
