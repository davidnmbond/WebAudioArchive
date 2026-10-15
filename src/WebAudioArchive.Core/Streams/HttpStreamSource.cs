using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Core.Streams;

public sealed record StreamInfo(
    Uri Url,
    string ContentType,
    int IcyMetaInt,
    IReadOnlyDictionary<string, string> IcyHeaders,
    string HowFound);

public sealed class OpenedStream : IAsyncDisposable
{
    HttpResponseMessage? _response;

    public required Stream Body { get; init; }
    public required StreamInfo Info { get; init; }

    internal void Attach(HttpResponseMessage response) => _response = response;

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        _response?.Dispose();
    }
}

public interface IStreamSource
{
    Task<OpenedStream> OpenAsync(CancellationToken ct);
}

public sealed class UnsupportedStreamException(string message) : Exception(message);

public sealed class HttpStreamSource(HttpClient http, StreamResolver resolver, Uri listenUrl, ILogger<HttpStreamSource> log)
    : IStreamSource
{
    const int ReResolveAfterFailures = 3;
    static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(20);

    ResolvedStream? _resolved;
    int _failures;

    public async Task<OpenedStream> OpenAsync(CancellationToken ct)
    {
        try
        {
            if (_resolved is null || _failures >= ReResolveAfterFailures)
            {
                _resolved = await resolver.ResolveAsync(listenUrl, ct);
                _failures = 0;
                log.LogInformation("Resolved {Listen} to {Stream} ({How})", listenUrl, _resolved.Url, _resolved.How);
            }

            var opened = await OpenCoreAsync(_resolved, ct);
            _failures = 0;
            return opened;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failures++;
            throw;
        }
    }

    async Task<OpenedStream> OpenCoreAsync(ResolvedStream resolved, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HeaderTimeout);

        // The resolved URL is requested afresh each time: stations redirect to short-lived tokenised URLs.
        var req = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        req.Headers.Referrer = listenUrl;

        var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        try
        {
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedStreamException($"Only MP3 (audio/mpeg) streams are supported; {resolved.Url} sent '{contentType}'");

            var icy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
                if (h.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase)) icy[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            var metaInt = icy.TryGetValue("icy-metaint", out var mi) && int.TryParse(mi, out var parsed) ? parsed : 0;

            var body = await response.Content.ReadAsStreamAsync(ct);
            var info = new StreamInfo(response.RequestMessage?.RequestUri ?? resolved.Url, contentType, metaInt, icy, resolved.How);
            var opened = new OpenedStream { Body = body, Info = info };
            opened.Attach(response);
            return opened;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
