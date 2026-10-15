namespace WebAudioArchive.Tests.Support;

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Html(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Audio() =>
        new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xFB, 0x90, 0x00]) { Headers = { ContentType = new("audio/mpeg") } } };

    public static HttpResponseMessage NotFound() => new(System.Net.HttpStatusCode.NotFound);
}
