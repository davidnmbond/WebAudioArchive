extern alias web;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Tests.Support;
using WebAudioArchive.Core.Analysis;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Schedule;
using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests;

/// <summary>
/// The web app with a schedule: the saved Burst page is served by a stubbed HttpClient in place of the live site (the
/// fixture is a test input only, never a runtime fallback). It is Thursday 15 October 2026, 21:30 UTC; recorder A has
/// audio from 18:55 to 20:05 UTC, so Backseat Aux (20:00 to 21:00 BST, 19:00 to 20:00 UTC) is recorded in full.
/// </summary>
public sealed class ShowsAppFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);
    public readonly string Root = Path.Combine(Path.GetTempPath(), "waa-showsapp-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<WebEntryPoint> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public volatile bool ScheduleFails;
    public int ScheduleRequests;

    public Task InitializeAsync()
    {
        var a = Path.Combine(Root, "data", "a");
        var b = Path.Combine(Root, "data", "b");
        Directory.CreateDirectory(b);
        var t = new DateTimeOffset(2026, 10, 15, 18, 55, 0, TimeSpan.Zero);
        var s1 = SegmentFiles.Closed(a, "burst", t, 300, firstSeq: 0, burstSeconds: 5);
        var s2 = SegmentFiles.Closed(a, "burst", t.AddMinutes(5), 3600, firstSeq: s1.Frames);
        SegmentFiles.Closed(a, "burst", t.AddMinutes(65), 300, firstSeq: s1.Frames + s2.Frames, endedBy: "shutdown");
        WriteAnalysis();

        var config = Path.Combine(Root, "stations.json");
        File.WriteAllText(config, """
            { "stations": [ { "id": "burst", "name": "Burst Radio", "listenUrl": "https://burst.test/listen",
              "scheduleUrl": "https://burst.test/schedule", "timezone": "Europe/London" } ] }
            """);

        var schedule = new StubHandler(req =>
        {
            Interlocked.Increment(ref ScheduleRequests);
            return req.RequestUri!.Host != "burst.test" || ScheduleFails
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : StubHandler.Html(ScheduleParserTests.Fixture);
        });
        Factory = new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WAA_STATION", "burst");
            builder.UseSetting("WAA_RECORDERS", $"a={a},b={b}");
            builder.UseSetting("WAA_CACHE", Path.Combine(Root, "cache"));
            builder.UseSetting("WAA_STATIONS_CONFIG", config);
            builder.UseSetting("WAA_ANALYSIS", Path.Combine(Root, "analysis"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(new ManualTimeProvider(Now));
                services.AddHttpClient(ScheduleService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => schedule);
                services.AddHttpClient(StatusService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => StubHandler.NotFound()));
            });
        });
        Client = Factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    public async Task<string> Page(string url) => WebUtility.HtmlDecode(await Client.GetStringAsync(url));

    /// <summary>
    /// Backseat Aux's analysis: two transcript lines from 19:00:00 (one talk section), a swear at 19:10, a review-only
    /// swear at 19:20, dead air at 19:30 and a song from 19:40 to 19:43 with its artist and title.
    /// </summary>
    void WriteAnalysis()
    {
        var show = new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
        const string job = "a/2026/10-15/190000";
        var paths = new AnalysisPaths(Path.Combine(Root, "analysis"), "burst");
        AnalysisEvent Event(string type, DateTimeOffset at, double seconds, string label, Dictionary<string, string>? evidence = null) => new(
            AnalysisEvent.MakeId(type, job, (at - show).TotalSeconds), type, at, at.AddSeconds(seconds), Severities.Medium, 0.9, type, "1",
            EventSources.Transcript, "a", job, (at - show).TotalSeconds, seconds, label, evidence);
        AnalysisJson.Write(paths.DayEvents(new DateOnly(2026, 10, 15)), new EventsDocument(1, "burst", "day:2026-10-15",
            show.AddHours(-19), show.AddHours(5), show, new Dictionary<string, string>(), [], null,
            [
                Event(EventTypes.Swear, show.AddMinutes(10), 0.4, "shit", new() { ["suppressed"] = "no" }),
                Event(EventTypes.Swear, show.AddMinutes(20), 0.4, "invented", new() { ["suppressed"] = "repetition" }),
                Event(EventTypes.DeadAir, show.AddMinutes(30), 8, "Dead air 8 s"),
                Event(EventTypes.Song, show.AddMinutes(40), 180, "Kate Bush - Running Up That Hill",
                    new() { ["artist"] = "Kate Bush", ["title"] = "Running Up That Hill", ["source"] = "now-playing" }),
            ]));
        AnalysisJson.Write(Path.Combine(paths.SegmentDir(job), AnalysisPaths.TranscriptFile),
            TranscriptFixtures.Of(show, "Good evening, this is Backseat Aux.", "Next up, a song.") with { JobId = job });
    }
}

public sealed class ShowsPageTests(ShowsAppFixture app) : IClassFixture<ShowsAppFixture>
{
    [Fact]
    public async Task The_shows_week_lists_the_fetched_schedule_in_utc_with_coverage()
    {
        var html = await app.Page("/shows");

        Assert.Contains("<h1 class=\"h3\">Shows</h1>", html);
        Assert.Contains("All times are UTC", html);
        Assert.Contains("Times UTC", html);
        Assert.Contains("90 weekly slots; local times read as Europe/London", html);
        Assert.Contains("https://burst.test/schedule", html);
        Assert.Contains("pdcal-view-week", html);
        Assert.Contains("Showing 90 shows from <span class=\"utc\">2026-10-12 00:00:00 UTC</span> to <span class=\"utc\">2026-10-19 00:00:00 UTC</span>", html);

        // Thursday 20:00 BST is 19:00 UTC; recorded in full by A.
        Assert.Contains("Backseat Aux\n2026-10-15 19:00:00 UTC to 2026-10-15 20:00:00 UTC\nRecorded in full by A (A 100%, B 0%)", html);
        // A covers only the last 5 minutes of the show before and the first 5 of the show after.
        Assert.Contains("Lo-Fidelity\n2026-10-15 18:00:00 UTC to 2026-10-15 19:00:00 UTC\nPartly recorded", html);
        Assert.Contains("The Noise Exchange\n2026-10-15 20:00:00 UTC to 2026-10-15 21:00:00 UTC\nPartly recorded", html);
        Assert.Contains("Reset Cafe\n2026-10-15 13:00:00 UTC to 2026-10-15 14:00:00 UTC\nNot recorded by any recorder", html);
        Assert.Contains("Breakfast with Alice\n2026-10-16 08:00:00 UTC to 2026-10-16 09:00:00 UTC\nIn the future", html);
        foreach (var css in new[] { "show-full", "show-partial", "show-missing", "show-future" })
            Assert.Contains("show-item " + css, html);
        Assert.DoesNotContain("show-item show-live", html);
    }

    [Fact]
    public async Task The_month_and_agenda_views_are_available()
    {
        Assert.Contains("pdcal-view-month", await app.Page("/shows?view=month"));
        var agenda = await app.Page("/shows?view=agenda&date=2026-10-26");
        Assert.Contains("pdcal-view-agenda", agenda);
        // After the clocks go back, Monday's breakfast at 09:00 GMT is 09:00 UTC.
        Assert.Contains("Breakfast with Mackenzie\n2026-10-26 09:00:00 UTC to 2026-10-26 10:00:00 UTC", agenda);
    }

    [Theory]
    [InlineData(PanoramicData.Blazor.Enums.CalendarView.Week, "2026-10-21", "shows?view=week&date=2026-10-21", "19 to 25 Oct 2026")]
    [InlineData(PanoramicData.Blazor.Enums.CalendarView.Month, "2026-11-03", "shows?view=month&date=2026-11-03", "November 2026")]
    [InlineData(PanoramicData.Blazor.Enums.CalendarView.Agenda, "2026-10-26", "shows?view=agenda&date=2026-10-26", "pdcal-view-agenda")]
    public async Task The_address_kept_for_back_opens_the_same_view_and_date(PanoramicData.Blazor.Enums.CalendarView view, string date, string address, string expected)
    {
        // The calendar's week and view are written to the address (history.replaceState) so Back from a show returns to them.
        Assert.Equal(address, web::WebAudioArchive.Web.Components.Pages.Shows.AddressFor(view, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

        var html = await app.Page("/" + address);
        Assert.Contains("pdcal-view-" + view.ToString().ToLowerInvariant(), html);
        Assert.Contains(expected, html);
    }

    [Fact]
    public async Task The_shows_calendar_keeps_its_scroll_position_through_the_page_script()
    {
        var html = await app.Page("/shows");

        // js/calendar-state.js puts the week grid back where it was, before the browser paints, for this host.
        var host = html.IndexOf("<div data-keep-scroll=\"shows\">", StringComparison.Ordinal);
        Assert.True(host > 0, "the calendar is inside the data-keep-scroll host");
        Assert.True(host < html.IndexOf("pdcal-week-scroll", StringComparison.Ordinal));
        Assert.DoesNotContain("data-scroll-hour", html);
    }
    [Fact]
    public async Task A_recorded_show_opens_its_timeline_with_margins_markers_and_segments()
    {
        var html = await app.Page("/shows/burst-20261015T1900Z");

        Assert.Contains("<h1 class=\"h3\" id=\"show-title\">Backseat Aux</h1>", html);
        Assert.Contains("2026-10-15 19:00:00 UTC", html);
        Assert.Contains("2026-10-15 20:00:00 UTC", html);
        Assert.Contains("Recorded in full by A", html);
        Assert.Contains("id=\"show-recorders\"", html);
        Assert.Contains("value=\"5\"", html); // the default margin
        Assert.Contains("pd-media-player", html);
        Assert.Matches("<audio[^>]*src=\"/media/clip/[0-9a-f]{32}[.]mp3\"", html);
        Assert.Contains("Backseat Aux: recorder A, 5 minute margins", html);
        // The first whole frame is at, or a frame before, 18:55:00.
        Assert.Matches("The clip runs from <span class=\"utc\">2026-10-15 18:5(4:59|5:00) UTC</span> to <span class=\"utc\">2026-10-15 20:05:00 UTC</span>", html);
        Assert.Contains("download=\"burst_20261015T185500Z_20261015T200500Z_A.mp3\"", html);

        // PDMediaPlayer: the speeds, skip 15 and 30 s, marker navigation and a download of the named file.
        foreach (var speed in new[] { "0.75×", "1×", "1.25×", "1.5×", "2×" }) Assert.Contains($">{speed}</option>", html);
        Assert.DoesNotContain(">1.75×</option>", html);
        Assert.Contains("Back 15 seconds", html);
        Assert.Contains("Forward 30 seconds", html);
        Assert.Contains("Previous marker", html);
        Assert.Contains("Next marker", html);
        Assert.Matches("href=\"/media/clip/[0-9a-f]{32}[.]mp3[?]download=1\"[^>]*download=\"burst_20261015T185500Z_20261015T200500Z_A.mp3\"", html);
        // The UTC wall time beside the player's own media time; position 0 is the first whole frame.
        Assert.Matches("<strong id=\"clip-wall\" class=\"utc\">2026-10-15 18:5(4:59|5:00)</strong> UTC", html);

        // A deep link is worked out again from the id on every load, so it survives a reload.
        Assert.Contains("id=\"show-title\">Backseat Aux</h1>", await app.Page("/shows/burst-20261015T1900Z"));
    }

    [Fact]
    public async Task The_show_page_is_a_toolbar_then_the_player_then_events_beside_the_transcript()
    {
        var html = await app.Page("/shows/burst-20261015T1900Z");

        // Toolbar first (title, times, coverage, margin, recorder, update), then the player, then the splitter.
        var toolbar = html.IndexOf("id=\"show-toolbar\"", StringComparison.Ordinal);
        var player = html.IndexOf("id=\"clip-player\"", StringComparison.Ordinal);
        var splitter = html.IndexOf("id=\"show-split\"", StringComparison.Ordinal);
        Assert.True(toolbar > 0 && toolbar < player && player < splitter, "toolbar, then player, then splitter");
        Assert.Contains("pdsplitter horizontal", html);
        Assert.Contains("id=\"show-margin\"", html);
        Assert.Contains("id=\"show-recorder\"", html);
        Assert.Contains("id=\"show-prepare\"", html);
        Assert.Contains("lib/splitjs/split.min.js", html);

        // The coverage table is still there, folded away under the coverage summary.
        Assert.Contains("id=\"show-recorders\"", html);

        // The player's own marker and segment lists and now-playing strip give way to the events pane.
        Assert.DoesNotContain("title=\"Hide Show markers\"", html);
        Assert.DoesNotContain("title=\"Hide Margin segments\"", html);

        // Left: margins, the show, talk, detected events and the song, in time order; review-only swears are left out.
        var events = Between(html, "id=\"show-events\"", "id=\"show-detail\"");
        string[] order = ["Lead-in", "Backseat Aux", "Good evening, this is Backseat Aux.", "Swearing (detected)", "Dead air (detected)", "Kate Bush - Running Up That Hill", "Tail"];
        var at = 0;
        foreach (var label in order)
        {
            var next = events.IndexOf(label, at, StringComparison.Ordinal);
            Assert.True(next >= 0, $"'{label}' is in the events list after the one before it");
            at = next;
        }

        Assert.DoesNotContain("invented", events);
        Assert.Contains("19:10:00 UTC", events);

        // Right: the transcript of the clip, by default.
        var detail = html[html.IndexOf("id=\"show-detail\"", StringComparison.Ordinal)..];
        Assert.Contains("id=\"show-transcript\"", detail);
        Assert.Contains("Good evening, this is Backseat Aux.", detail);
        Assert.Contains("Next up, a song.", detail);
        Assert.DoesNotContain("id=\"show-song\"", detail);
    }

    [Fact]
    public async Task Selecting_a_time_in_talk_highlights_the_transcript_and_a_song_shows_its_metadata()
    {
        // A time in a talk section highlights its transcript lines.
        var talk = await app.Page("/shows/burst-20261015T1900Z?at=2026-10-15T19:00:10Z");
        Assert.Matches("class=\"[^\"]*show-event[^\"]*selected[^\"]*\"[^>]*data-kind=\"talk\"", talk);
        Assert.Equal(2, CountOf(talk, "class=\"show-line selected\""));
        Assert.Equal(0, CountOf(await app.Page("/shows/burst-20261015T1900Z"), "class=\"show-line selected\""));

        // A time in a song shows the song's metadata in place of the transcript.
        var song = await app.Page("/shows/burst-20261015T1900Z?at=2026-10-15T19:41:00Z");
        Assert.Matches("class=\"[^\"]*show-event[^\"]*selected[^\"]*\"[^>]*data-kind=\"song\"", song);
        var detail = song[song.IndexOf("id=\"show-detail\"", StringComparison.Ordinal)..];
        Assert.Contains("id=\"show-song\"", detail);
        Assert.Contains("Running Up That Hill", detail);
        Assert.Contains("Kate Bush", detail);
        Assert.Contains("now-playing", detail);
        Assert.Contains("2026-10-15 19:40:00 UTC", detail);
        Assert.Contains("2026-10-15 19:43:00 UTC", detail);
        Assert.DoesNotContain("id=\"show-transcript\"", detail);
    }

    [Fact]
    public async Task At_opens_the_show_on_air_at_a_utc_time_or_the_off_air_hour()
    {
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var show = await client.GetAsync("/at?utc=2026-10-15T19:10:00Z&recorder=a");
        Assert.Equal(HttpStatusCode.Redirect, show.StatusCode);
        Assert.Equal("/shows/burst-20261015T1900Z?at=2026-10-15T19:10:00Z&recorder=a", show.Headers.Location!.OriginalString);

        // Nothing is scheduled from 22:00 to 09:00 local; the off-air hour is its own page.
        var off = await client.GetAsync("/at?utc=2026-10-15T23:30:00Z");
        Assert.Equal("/shows/burst-20261015T2300Z?at=2026-10-15T23:30:00Z", off.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/at?utc=nonsense")).StatusCode);

        var html = await app.Page("/shows/burst-20261015T2300Z");
        Assert.Contains("id=\"show-title\">Off air (unscheduled)</h1>", html);
        Assert.Contains("2026-10-16 00:00:00 UTC", html);
        // Not an hour start, or an hour with a show in it, is still not a show.
        Assert.Contains("No scheduled show", await app.Page("/shows/burst-20261015T2330Z"));
    }

    [Fact]
    public async Task An_off_air_hour_with_audio_plays_it()
    {
        // The Noise Exchange ends at 21:00 UTC; recorder A's audio ends at 20:05, so the next hour has none.
        var html = await app.Page("/shows/burst-20261015T2100Z");
        Assert.Contains("Off air (unscheduled)", html);
        Assert.Contains("Nothing of this show was recorded", html);
    }

    static int CountOf(string html, string text)
    {
        var n = 0;
        for (var i = html.IndexOf(text, StringComparison.Ordinal); i >= 0; i = html.IndexOf(text, i + text.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    static string Between(string html, string from, string to)
    {
        var a = html.IndexOf(from, StringComparison.Ordinal);
        var b = html.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, $"'{from}' comes before '{to}'");
        return html[a..b];
    }

    [Fact]
    public async Task A_partly_recorded_show_warns_and_plays_what_exists()
    {
        var html = await app.Page("/shows/burst-20261015T2000Z?margin=2");

        Assert.Contains("The Noise Exchange", html);
        Assert.Contains("Partly recorded", html);
        Assert.Contains("id=\"show-warnings\"", html);
        Assert.Contains("Only part of this show was recorded", html);
        Assert.Contains("value=\"2\"", html);
        Assert.Contains("pd-media-player", html);
        Assert.Matches("The clip runs from <span class=\"utc\">2026-10-15 19:5(7:59|8:00) UTC</span> to <span class=\"utc\">2026-10-15 20:05:00 UTC</span>", html);
    }

    [Fact]
    public async Task A_future_show_is_not_yet_broadcast_and_an_unknown_id_is_explained()
    {
        var future = await app.Page("/shows/burst-20261016T0800Z");
        Assert.Contains("Breakfast with Alice", future);
        Assert.Contains("Not yet broadcast", future);
        Assert.DoesNotContain("pd-media-player", future);

        Assert.Contains("No scheduled show of station 'burst' starts at 2026-10-15 19:30:00 UTC", await app.Page("/shows/burst-20261015T1930Z"));
        Assert.Contains("is not a show id", await app.Page("/shows/not-a-show"));
        var missing = await app.Page("/shows/burst-20261015T1300Z");
        Assert.Contains("Nothing of this show was recorded", missing);
    }

    [Fact]
    public async Task A_failed_refresh_shows_a_warning_and_keeps_the_last_good_schedule()
    {
        var service = app.Factory.Services.GetRequiredService<ScheduleService>();
        await service.WaitForScheduleAsync(TimeSpan.FromSeconds(10));
        try
        {
            app.ScheduleFails = true;
            await service.RefreshAsync(CancellationToken.None);

            var html = await app.Page("/shows");
            Assert.Contains("Showing the last good schedule", html);
            Assert.Contains("HTTP 503", html);
            Assert.Contains("Backseat Aux", html);
        }
        finally
        {
            app.ScheduleFails = false;
            await service.RefreshAsync(CancellationToken.None);
        }

        Assert.DoesNotContain("Showing the last good schedule", await app.Page("/shows"));
        Assert.True(File.Exists(service.SavedPath));
    }
}
