using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Core.Streams;

public sealed record ResolvedStream(Uri Url, string How);

public sealed class StreamNotFoundException(string message) : Exception(message);

public sealed class StreamResolver(HttpClient http, ILogger<StreamResolver> log)
{
    public async Task<ResolvedStream> ResolveAsync(Uri listenUrl, CancellationToken ct)
    {
        using var page = await GetHeadersAsync(listenUrl, listenUrl, ct);
        if (IsAudio(page)) return new ResolvedStream(listenUrl, "direct-stream");
        if (!page.IsSuccessStatusCode)
            throw new StreamNotFoundException($"{listenUrl} answered {(int)page.StatusCode}");

        var html = await page.Content.ReadAsStringAsync(ct);
        foreach (var candidate in StreamCandidates.FromHtml(html, listenUrl))
        {
            var url = candidate.Url;
            var how = candidate.How;
            if (IsPlaylist(url))
            {
                var inner = await ReadPlaylistAsync(url, listenUrl, ct);
                if (inner is null) continue;
                url = inner;
                how += "+playlist";
            }

            if (await ProbeAsync(url, listenUrl, ct)) return new ResolvedStream(url, how);
            log.LogInformation("Candidate {Url} ({How}) did not answer with audio", url, how);
        }

        throw new StreamNotFoundException($"No working audio stream found on {listenUrl}");
    }

    async Task<HttpResponseMessage> GetHeadersAsync(Uri url, Uri referer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = referer;
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    async Task<bool> ProbeAsync(Uri url, Uri referer, CancellationToken ct)
    {
        try
        {
            using var r = await GetHeadersAsync(url, referer, ct);
            return IsAudio(r);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    async Task<Uri?> ReadPlaylistAsync(Uri url, Uri referer, CancellationToken ct)
    {
        try
        {
            using var r = await GetHeadersAsync(url, referer, ct);
            if (!r.IsSuccessStatusCode) return null;
            var text = await r.Content.ReadAsStringAsync(ct);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var value = line.StartsWith("File1=", StringComparison.OrdinalIgnoreCase) ? line[6..] : line;
                if (Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return u;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    static bool IsAudio(HttpResponseMessage r) =>
        r.IsSuccessStatusCode && r.Content.Headers.ContentType?.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    static bool IsPlaylist(Uri u) =>
        u.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
}
