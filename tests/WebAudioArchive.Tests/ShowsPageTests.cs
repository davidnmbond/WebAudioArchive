extern alias web;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Tests.Support;
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

    public async Task<string> Page(string url)
    {
        var h = WebUtility.HtmlDecode(await Client.GetStringAsync(url));
        File.WriteAllText(@"C:\Users\david\AppData\Local\Temp\claude\c--Users-david-source-repos-davidnmbond-WebAudioArchive\724cebe8-e5c2-4d21-ba37-30fa75ec8f78\scratchpad\dump-" + url.Replace('/', '_').Replace('?', '_') + ".html", h); // DEBUG
        return h;
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
        Assert.Contains("Showing 90 shows from 2026-10-12 00:00:00 UTC to 2026-10-19 00:00:00 UTC", html);

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
        Assert.Contains("The clip runs from 2026-10-15 18:55:00 UTC to 2026-10-15 20:05:00 UTC", html);
        Assert.Contains("download=\"burst_20261015T185500Z_20261015T200500Z_A.mp3\"", html);

        // Marker and segment kinds as the player shows them (labels from ClipMarkers.KindLabels).
        Assert.Contains(">Show", html);
        Assert.Contains("Margin", html);
        Assert.Contains("Previous marker", html);
        Assert.Contains("Next marker", html);
        Assert.Contains("Lead-in", html);
        Assert.Contains("Tail", html);

        // A deep link is worked out again from the id on every load, so it survives a reload.
        Assert.Contains("id=\"show-title\">Backseat Aux</h1>", await app.Page("/shows/burst-20261015T1900Z"));
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
        Assert.Contains("The clip runs from 2026-10-15 19:58:00 UTC to 2026-10-15 20:05:00 UTC", html);
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
