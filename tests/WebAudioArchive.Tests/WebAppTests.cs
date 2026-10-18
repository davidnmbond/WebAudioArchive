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
    StubHandler _health = null!;

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

        var stub = _health = new StubHandler(req => req.RequestUri!.Host == "recorder-a"
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
        Assert.Equal((await _client.GetByteArrayAsync($"/media/clip/{id}.mp3")).Length, json.GetProperty("bytes").GetInt64());
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
        var file = await _client.GetByteArrayAsync(url);
        Assert.Equal(length, file.Length);

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
    public async Task Status_page_shows_what_neither_recorder_has_in_the_last_24_hours()
    {
        // A has 15:49:55 to 16:10 and B 15:59:55 to 16:10 with a 3 s gap at 16:02 that A covers. Now is 16:30.
        var snapshot = await _factory.Services.GetRequiredService<StatusService>().ReadAsync(CancellationToken.None);

        var combined = Assert.IsType<web::WebAudioArchive.Web.Clips.CombinedCoverage>(snapshot.Combined);
        Assert.Equal(20 * 60 + 5, combined.CoveredSeconds, 1);
        Assert.Equal(2, combined.Missing.Count);
        Assert.Equal(T0.AddHours(-23).AddMinutes(30), combined.Missing[0].Start);
        Assert.InRange((combined.Missing[0].End - T0.AddMinutes(49).AddSeconds(55)).TotalSeconds, -0.05, 0.05); // within a frame of the first audio
        Assert.Equal((T0.AddHours(1).AddMinutes(10), T0.AddHours(1).AddMinutes(30)), (combined.Missing[1].Start, combined.Missing[1].End));

        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
        Assert.Contains("id=\"station-coverage\"", html);
        Assert.Contains("A and B together", html);
        Assert.Contains("Missing from both recorders", html);
        Assert.Contains("2026-10-15 16:10:00 UTC to 2026-10-15 16:30:00 UTC", html);
        Assert.DoesNotContain("16:02:00 UTC to", html); // B's gap is covered by A
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

    [Theory]
    [InlineData("/")]
    [InlineData("/recordings")]
    [InlineData("/shows")]
    public async Task Every_page_sets_the_theme_before_first_paint_and_has_the_theme_toggle(string url)
    {
        var html = await _client.GetStringAsync(url);

        // The theme script sits in <head>, before any stylesheet, so the right theme is applied before first paint.
        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        var script = head.IndexOf("id=\"theme-boot\"", StringComparison.Ordinal);
        Assert.True(script > 0);
        Assert.True(script < head.IndexOf("stylesheet", StringComparison.Ordinal));
        Assert.Contains("prefers-color-scheme: dark", head);
        Assert.Contains("data-bs-theme", head);
        Assert.Contains("localStorage", head);
        foreach (var choice in new[] { "auto", "light", "dark" })
            Assert.Contains($"data-theme-choice=\"{choice}\"", html);

        // Library assets: Font Awesome for its icons, then its own and its scoped styles.
        var fa = head.IndexOf("lib/font-awesome/css/all.min.css", StringComparison.Ordinal);
        var lib = head.IndexOf("_content/PanoramicData.Blazor/css/main.min.css", StringComparison.Ordinal);
        Assert.True(fa > 0 && fa < lib);
        Assert.Contains("WebAudioArchive.Web.styles.css", head);
    }

    [Theory]
    [InlineData("/lib/font-awesome/css/all.min.css", "text/css")]
    [InlineData("/lib/font-awesome/webfonts/fa-solid-900.woff2", "font/woff2")]
    [InlineData("/lib/bootstrap/bootstrap.min.css", "text/css")]
    [InlineData("/css/app.css", "text/css")]
    public async Task Vendored_assets_are_served_locally(string url, string type)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task App_css_uses_bootstrap_variables_and_hides_the_programmatic_heading_focus_ring()
    {
        var css = await _client.GetStringAsync("/css/app.css");

        Assert.Contains("h1:focus", css);
        Assert.Contains("outline: none", css);
        Assert.Contains("var(--bs-", css);
        // Parts of the clip the browser has not loaded yet are striped on the seek bar.
        Assert.Contains("repeating-linear-gradient", css);
        Assert.Contains(".pdmp-buffered", css);
        Assert.DoesNotMatch("#[0-9a-fA-F]{3,6}\\b", css);
    }

    [Fact]
    public async Task Pages_use_the_library_components()
    {
        var status = await _client.GetStringAsync("/");
        Assert.Contains("pd-progressbar", status);
        Assert.Contains("pdtoolbar", status);

        var recordings = await _client.GetStringAsync("/recordings");
        Assert.Contains("pdtoggleswitch", recordings);
        Assert.Contains("pddatetime", recordings);

    }

    [Fact]
    public async Task There_is_no_separate_player_page()
    {
        // The show page plays everything; /at finds the show (or the off-air hour) for a UTC time.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/player")).StatusCode);
        Assert.DoesNotContain("href=\"player\"", await _client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Clip_api_returns_gaps_and_restarts_as_data_with_their_place_in_the_clip()
    {
        // Recorder B has a 3 s gap at 16:02:00; recorder A's 16:00 segment ended by shutdown at 16:10.
        var b = await ClipJson("from=2026-10-15T16:01:00Z&to=2026-10-15T16:03:00Z&recorder=b");
        var gap = Assert.Single(b.GetProperty("gaps").EnumerateArray());
        Assert.Equal("2026-10-15T16:02:00Z", gap.GetProperty("fromUtc").GetString());
        Assert.Equal("2026-10-15T16:02:03Z", gap.GetProperty("toUtc").GetString());
        Assert.Equal(3, gap.GetProperty("seconds").GetDouble(), 3);
        Assert.Equal("end-of-stream", gap.GetProperty("reason").GetString());
        Assert.InRange(gap.GetProperty("clipSeconds").GetDouble(), 59.9, 60.1);
        Assert.Empty(b.GetProperty("restarts").EnumerateArray());

        var a = await ClipJson("from=2026-10-15T16:08:00Z&to=2026-10-15T16:12:00Z&recorder=a");
        var restart = Assert.Single(a.GetProperty("restarts").EnumerateArray());
        Assert.Equal("2026-10-15T16:10:00Z", restart.GetProperty("atUtc").GetString());
        Assert.Equal("shutdown", restart.GetProperty("endedBy").GetString());
        Assert.InRange(restart.GetProperty("clipSeconds").GetDouble(), 119.9, 120.1);
        var tail = Assert.Single(a.GetProperty("gaps").EnumerateArray());
        Assert.Equal("not recorded", tail.GetProperty("reason").GetString());
        Assert.Equal("2026-10-15T16:12:00Z", tail.GetProperty("toUtc").GetString());
    }

    [Fact]
    public async Task Every_page_has_the_shows_nav_item()
    {
        var html = await _client.GetStringAsync("/recordings");
        Assert.Matches("href=\"shows\"[^>]*>.*fa-calendar-alt.*Shows</a>", html);
    }

    [Fact]
    public async Task Recordings_page_has_a_week_calendar_of_segments_and_gaps_that_opens_the_show()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/recordings"));

        Assert.Contains("id=\"recordings-calendar\"", html);
        Assert.Contains("pdcal-view-week", html);
        Assert.Contains("A: segment", html);
        Assert.Contains("B: gap 3.0 s", html);
        Assert.Contains("rec-item rec-gap rec-b", html);
        Assert.Contains("Recorder B has no audio from 2026-10-15 16:02:00 UTC to 2026-10-15 16:02:03 UTC", html);
        Assert.Contains("Segments", html);
        Assert.Contains("recordings-table", html); // the table is kept

        var items = new web::WebAudioArchive.Web.RecordingsQuery(_factory.Services.GetRequiredService<web::WebAudioArchive.Web.Clips.SegmentCatalog>(), _time)
            .CalendarItems(T0, T0.AddHours(2));
        var segment = items.First(i => i.Kind == "segment" && i.Recorder == "A");
        Assert.Equal("/at?utc=2026-10-15T15:50:00Z&recorder=a", segment.PlayUrl);
        var g = Assert.Single(items, i => i.Kind == "gap");
        Assert.Equal("/at?utc=2026-10-15T16:02:00Z&recorder=b", g.PlayUrl);
    }

    [Fact]
    public async Task Recordings_calendar_marks_what_neither_recorder_has_between_the_first_audio_and_now()
    {
        var items = new web::WebAudioArchive.Web.RecordingsQuery(_factory.Services.GetRequiredService<web::WebAudioArchive.Web.Clips.SegmentCatalog>(), _time)
            .CalendarItems(T0, T0.AddHours(2));

        // Not before the first audio (15:49:55) or after now (16:30); B's 16:02 gap is covered by A.
        var m = Assert.Single(items, i => i.Kind == "missing");
        Assert.Equal((T0.AddHours(1).AddMinutes(10), T0.AddHours(1).AddMinutes(30)), (m.StartUtc, m.EndUtc));
        Assert.Equal("A+B", m.Recorder);
        Assert.Equal("rec-item rec-missing", m.CssClass);
        Assert.Equal("A+B: missing 1200.0 s", m.Title);
        Assert.Contains("Neither recorder has audio from 2026-10-15 16:10:00 UTC to 2026-10-15 16:30:00 UTC", m.Tooltip);
        Assert.Equal("/at?utc=2026-10-15T16:10:00Z&recorder=auto", m.PlayUrl);

        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/recordings"));
        Assert.Contains("rec-legend rec-missing", html);
    }

    [Fact]
    public async Task Shows_page_without_a_schedule_says_so_and_points_to_the_recordings()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/shows"));

        Assert.Contains("<h1 class=\"h3\">Shows</h1>", html);
        Assert.Contains("Schedule unavailable", html);
        Assert.Contains("WAA_STATIONS_CONFIG", html);
        Assert.Contains("href=\"recordings\" id=\"schedule-fallback\"", html);
        Assert.DoesNotContain("pdcal", html);
    }

    // Flicker: the page's first interactive render must match the prerendered one, so the circuit start shows no change.

    [Fact]
    public async Task Status_page_reuses_a_fresh_reading_instead_of_reading_again()
    {
        await _client.GetStringAsync("/");
        var reads = _health.Requests.Count;
        Assert.Equal(2, reads); // both recorders once

        // Within one refresh period the reading is reused, as the circuit's first render does after the prerender.
        _time.Advance(TimeSpan.FromSeconds(web::WebAudioArchive.Web.Components.Pages.Status.RefreshSeconds - 1));
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
        Assert.Equal(reads, _health.Requests.Count);
        Assert.Contains("data-level=\"Green\"", html);
        Assert.DoesNotContain("Reading the recorders", html);
        Assert.Contains("Checked at <span class=\"utc\">2026-10-15 16:30:00 UTC</span>", html); // when it was read, not now

        // A stale reading is read again before it is prerendered.
        _time.Advance(TimeSpan.FromSeconds(1));
        await _client.GetStringAsync("/");
        Assert.Equal(reads * 2, _health.Requests.Count);
    }

    [Fact]
    public async Task Status_service_keeps_its_last_reading()
    {
        var service = _factory.Services.GetRequiredService<StatusService>();
        Assert.Null(service.Last);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.Same(snapshot, service.Last);
        Assert.Equal(_time.GetUtcNow(), snapshot.CheckedUtc);
        Assert.Equal(["a", "b"], snapshot.Statuses.Select(s => s.Recorder.Id));
    }

    [Theory]
    [InlineData(0, false, 15)]
    [InlineData(10, false, 5)]
    [InlineData(15, true, 0)]
    [InlineData(600, true, 0)]
    public void A_status_reading_is_due_for_refresh_after_one_period(int ageSeconds, bool stale, int dueSeconds)
    {
        var now = T0;
        var reading = new StatusSnapshot([], now.AddSeconds(-ageSeconds));

        Assert.Equal(stale, web::WebAudioArchive.Web.Components.Pages.Status.IsStale(reading, now));
        Assert.Equal(TimeSpan.FromSeconds(dueSeconds), web::WebAudioArchive.Web.Components.Pages.Status.DueIn(reading.CheckedUtc, now));
    }

    [Fact]
    public void Preparing_a_clip_copies_no_audio_and_the_same_request_gives_the_same_clip()
    {
        var clips = _factory.Services.GetRequiredService<web::WebAudioArchive.Web.Clips.ClipService>();
        var request = web::WebAudioArchive.Web.Clips.ClipService.ParseQuery("2026-10-15T15:55:00Z", "2026-10-15T15:55:20Z", null, null, "auto");

        var first = clips.Prepare(null, request);
        var again = clips.Prepare(null, request);

        // The clip is streamed from the segments: the cache holds a small manifest, never an MP3.
        Assert.Empty(Directory.EnumerateFiles(Cache, "*.mp3"));
        Assert.InRange(new FileInfo(Path.Combine(Cache, first.File.Id + ".json")).Length, 1, 8192);
        Assert.Equal(first.File.Id, again.File.Id);
        Assert.Equal(first.Url, again.Url);
        Assert.Equal(first.File.Bytes, again.File.Bytes);
        Assert.Throws<web::WebAudioArchive.Web.Clips.ClipException>(() => clips.Prepare("other", request));
    }

    [Fact]
    public async Task Recordings_page_says_loading_until_the_table_has_its_rows_and_starts_the_grid_at_an_hour()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/recordings"));

        // PDTable reads its rows only after its first render; the prerendered table must not claim there are none.
        Assert.Contains("Loading recordings...", html);
        Assert.DoesNotContain("No recordings found.", html);
        // 16:30 UTC: the week grid starts at 13:00, placed by js/calendar-state.js in the frame it is drawn.
        Assert.Contains("data-scroll-hour=\"13\"", html);
    }

    [Fact]
    public async Task Every_page_loads_the_calendar_script_and_preloads_the_icon_fonts_before_the_styles()
    {
        var html = await _client.GetStringAsync("/");

        var script = html.IndexOf("<script src=\"js/calendar-state.js\"></script>", StringComparison.Ordinal);
        var solid = html.IndexOf("<link rel=\"preload\" href=\"lib/font-awesome/webfonts/fa-solid-900.woff2\" as=\"font\" type=\"font/woff2\" crossorigin", StringComparison.Ordinal);
        var regular = html.IndexOf("<link rel=\"preload\" href=\"lib/font-awesome/webfonts/fa-regular-400.woff2\" as=\"font\" type=\"font/woff2\" crossorigin", StringComparison.Ordinal);
        var styles = html.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal);
        Assert.True(script > 0 && solid > 0 && regular > 0, "the calendar script and both font preloads are in the head");
        Assert.True(script < styles && solid < styles && regular < styles, "they come before the first stylesheet");
        Assert.True(html.IndexOf("theme-boot", StringComparison.Ordinal) < script, "the theme script still runs first");

        foreach (var url in new[] { "/js/calendar-state.js", "/lib/font-awesome/webfonts/fa-solid-900.woff2", "/lib/font-awesome/webfonts/fa-regular-400.woff2" })
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(url)).StatusCode);
        var js = await _client.GetStringAsync("/js/calendar-state.js");
        Assert.Contains("data-keep-scroll", js);
        Assert.Contains("replaceUrl", js);
    }
}