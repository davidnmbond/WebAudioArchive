extern alias web;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests;

/// <summary>The web app end to end in memory: settings, endpoints and the server-rendered pages, over synthetic data.</summary>
public sealed class WebAppTests : IAsyncLifetime
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-web-" + Guid.NewGuid().ToString("N"));
    readonly ManualTimeProvider _time = new(T0.AddHours(1).AddMinutes(30));
    WebApplicationFactory<WebEntryPoint> _factory = null!;
    HttpClient _client = null!;

    string DataA => Path.Combine(_root, "data", "a");
    string DataB => Path.Combine(_root, "data", "b");
    string Cache => Path.Combine(_root, "cache");

    const string HealthA = """
        { "instance": "home-a", "uptimeSeconds": 574, "stations": [ { "id": "burst", "enabled": true, "connected": true,
          "secondsSinceLastFrameWritten": 0.2, "lastError": null, "reconnects": 0,
          "currentSegmentStartUtc": "2026-10-15T17:00:00.25+01:00" } ] }
        """;

    public Task InitializeAsync()
    {
        // Recorder A: two back-to-back segments (15:50 to 16:00 and 16:00 to 16:10), the first after a connect (5 s burst). Recorder B: only the second hour.
        var s1 = SegmentFiles.Closed(DataA, "burst", T0.AddMinutes(50), 600, firstSeq: 0, burstSeconds: 5);
        SegmentFiles.Closed(DataA, "burst", T0.AddHours(1), 600, firstSeq: s1.Frames, endedBy: "shutdown");
        SegmentFiles.Closed(DataB, "burst", T0.AddHours(1), 600, firstSeq: 0, burstSeconds: 5,
            gaps: [new SegmentGap(T0.AddHours(1).AddMinutes(2), T0.AddHours(1).AddMinutes(2).AddSeconds(3), 3, "end-of-stream")]);

        var stub = new StubHandler(req => req.RequestUri!.Host == "recorder-a"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HealthA) }
            : throw new HttpRequestException("Connection refused (recorder-b:8080)"));

        _factory = new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WAA_STATION", "burst");
            b.UseSetting("WAA_RECORDERS", $"a={DataA},b={DataB}");
            b.UseSetting("WAA_RECORDER_HEALTH", "a=http://recorder-a:8080/healthz,b=http://recorder-b:8080/healthz");
            b.UseSetting("WAA_CACHE", Cache);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddHttpClient(StatusService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    async Task<JsonElement> ClipJson(string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.GetAsync("/api/clip?" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Clip_api_returns_the_clip_description_as_json()
    {
        var json = await ClipJson("station=burst&from=2026-10-15T15:59:30Z&to=2026-10-15T16:00:30Z&recorder=auto");

        var id = json.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal($"/media/clip/{id}.mp3", json.GetProperty("url").GetString());
        Assert.Equal("a", json.GetProperty("recorder").GetString());
        Assert.Equal("burst_20261015T155930Z_20261015T160030Z_A.mp3", json.GetProperty("downloadName").GetString());
        Assert.InRange(json.GetProperty("seconds").GetDouble(), 59.95, 60.1);
        Assert.Equal(new FileInfo(Path.Combine(Cache, id + ".mp3")).Length, json.GetProperty("bytes").GetInt64());
        Assert.EndsWith("Z", json.GetProperty("from").GetString());
        Assert.InRange((DateTimeOffset.Parse(json.GetProperty("from").GetString()!) - T0.AddSeconds(3570)).TotalSeconds, -0.05, 0.001);
        Assert.InRange((DateTimeOffset.Parse(json.GetProperty("to").GetString()!) - T0.AddSeconds(3630)).TotalSeconds, -0.001, 0.05);
        Assert.Equal(2, json.GetProperty("pieces").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("warnings").ValueKind);
        Assert.Contains(json.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("about a second"));
    }

    [Fact]
    public async Task Margins_and_a_forced_recorder_are_applied_and_warned()
    {
        var json = await ClipJson("from=2026-10-15T16:01:00Z&to=2026-10-15T16:03:00Z&marginBefore=1&marginAfter=1&recorder=b");

        Assert.Equal("b", json.GetProperty("recorder").GetString());
        Assert.Equal("2026-10-15T16:00:00Z", json.GetProperty("windowFrom").GetString());
        Assert.Equal("2026-10-15T16:04:00Z", json.GetProperty("windowTo").GetString());
        var warnings = json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("16:02:00 UTC") && w.Contains("16:02:03 UTC"));
        Assert.Contains(warnings, w => w.Contains("Recorder A would have covered more"));
    }

    [Fact]
    public async Task Media_endpoint_serves_ranges_with_206_and_a_download_name()
    {
        var json = await ClipJson("from=2026-10-15T15:55:00Z&to=2026-10-15T15:55:20Z");
        var url = json.GetProperty("url").GetString()!;
        var length = json.GetProperty("bytes").GetInt64();
        var file = await File.ReadAllBytesAsync(Path.Combine(Cache, json.GetProperty("id").GetString() + ".mp3"));

        var full = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("audio/mpeg", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());
        Assert.Null(full.Content.Headers.ContentDisposition);
        Assert.Equal(file, await full.Content.ReadAsByteArrayAsync());
        SegmentFiles.Seqs(file);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(1000, 1999);
        var part = await _client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal($"bytes 1000-1999/{length}", part.Content.Headers.ContentRange!.ToString());
        Assert.Equal(file[1000..2000], await part.Content.ReadAsByteArrayAsync());

        var download = await _client.GetAsync(url + "?download=1");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("burst_20261015T155500Z_20261015T155520Z_A.mp3", download.Content.Headers.ContentDisposition.FileName?.Trim('"'));
    }

    [Theory]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:10:00Z", "must be before")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:09:00Z", "must be before")]
    [InlineData("to=2026-10-15T15:10:00Z", "from is required")]
    [InlineData("from=yesterday&to=2026-10-15T15:10:00Z", "is not a time")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T19:10:01Z", "future")]
    [InlineData("from=2026-10-15T10:00:00Z&to=2026-10-15T14:00:01Z", "maximum clip length is 4 hours")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:11:00Z&recorder=c", "Unknown recorder")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:11:00Z&recorder=../../etc", "not auto or a recorder id")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:11:00Z&station=../secret", "Unknown station")]
    [InlineData("from=2026-10-15T15:10:00Z&to=2026-10-15T15:11:00Z&marginBefore=-1", "marginBefore")]
    public async Task Invalid_clip_requests_are_400_with_a_clear_message(string query, string message)
    {
        var json = await ClipJson(query, HttpStatusCode.BadRequest);
        Assert.Contains(message, json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_window_with_no_recording_is_404_naming_what_exists()
    {
        var json = await ClipJson("from=2026-10-15T12:00:00Z&to=2026-10-15T12:01:00Z", HttpStatusCode.NotFound);
        var error = json.GetProperty("error").GetString()!;
        Assert.Contains("No recording", error);
        Assert.Contains("recorder A has audio from 2026-10-15 15:49:55 UTC", error);
    }

    [Theory]
    [InlineData("/media/clip/0123456789abcdef0123456789abcdef.mp3")]
    [InlineData("/media/clip/not-an-id.mp3")]
    [InlineData("/media/clip/..%2F..%2Fdata%2Fa%2Fburst.mp3")]
    [InlineData("/media/clip/0123456789ABCDEF0123456789ABCDEF.mp3")]
    public async Task Unknown_or_malformed_clip_ids_are_404(string url)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Healthz_is_ok_when_the_data_roots_exist()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Status_page_shows_each_recorder_with_its_state_and_utc_times()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));

        Assert.Contains("Recorder A", html);
        Assert.Contains("Recorder B", html);
        Assert.Contains("data-level=\"Green\"", html);
        Assert.Contains("data-level=\"Red\"", html);
        Assert.Contains("Health check unreachable", html);
        Assert.Contains("2026-10-15 16:00:00 UTC", html); // the recorder sent 17:00:00+01:00; shown in UTC
        Assert.Contains("All times UTC", html);
        Assert.Contains("No sign-in", html);
        Assert.Contains("(read-only)", html);
    }

    [Fact]
    public async Task Recordings_page_has_the_table_and_utc_columns()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/recordings"));

        Assert.Contains("<h1 class=\"h3\">Recordings</h1>", html);
        Assert.Contains("Start (UTC)", html);
        Assert.Contains("End (UTC)", html);
        Assert.Contains("3 segments", html);
    }

    [Fact]
    public async Task Player_deep_link_prepares_the_clip_on_the_server()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/player?from=2026-10-15T15:52:00Z&to=2026-10-15T15:53:00Z&recorder=auto"));

        Assert.Contains("Clip covers", html);
        Assert.Contains("2026-10-15 15:52:00 UTC", html);
        Assert.Contains("Download burst_20261015T155200Z_20261015T155300Z_A.mp3", html);
        Assert.Contains("id=\"clip-player\"", html);
        Assert.Contains("Wall time", html);
        foreach (var speed in new[] { "0.75x", "1x", "1.25x", "1.5x", "2x" }) Assert.Contains(speed, html);
    }

    [Fact]
    public async Task Player_page_without_a_link_shows_the_form()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/player"));

        Assert.Contains("Prepare clip", html);
        Assert.Contains("From (UTC, HH:mm:ss)", html);
        Assert.Contains("id=\"player-margin\"", html);
        Assert.Contains("id=\"player-margin-minutes\"", html);
        Assert.Contains("value=\"5\"", html);
        Assert.Contains("minutes before and after", html);
        Assert.DoesNotContain("id=\"clip-player\"", html);
    }
}
