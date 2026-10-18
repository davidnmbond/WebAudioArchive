using System.Net;
using System.Text;

namespace WebAudioArchive.Tests.Support;

/// <summary>One request as the stub saw it: the multipart parts are read before the content is disposed.</summary>
public sealed record SeenRequest(HttpMethod Method, string Path, string? Authorization, IReadOnlyDictionary<string, string> Fields, byte[]? File, string? FileName, string? FileType);

/// <summary>An HttpMessageHandler for tests: records each request (including the multipart parts) and answers from a function.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    readonly object _lock = new();
    readonly List<SeenRequest> _seen = [];
    int _inFlight;

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_lock) return [.. _seen];
        }
    }

    public int Calls => Seen.Count;

    public int MaxInFlight { get; private set; }

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((r, _) => Task.FromResult(respond(r)))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>();
        byte[]? file = null;
        string? fileName = null, fileType = null;
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                if (part.Headers.ContentDisposition?.FileName is { } f)
                {
                    file = await part.ReadAsByteArrayAsync(ct);
                    fileName = f.Trim('"');
                    fileType = part.Headers.ContentType?.MediaType;
                }
                else
                {
                    fields[name] = await part.ReadAsStringAsync(ct);
                }
            }
        }

        lock (_lock)
        {
            _seen.Add(new SeenRequest(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), fields, file, fileName, fileType));
            MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
        }

        try
        {
            return await respond(request, ct);
        }
        finally
        {
            lock (_lock) _inFlight--;
        }
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
