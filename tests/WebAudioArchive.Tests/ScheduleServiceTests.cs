extern alias web;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

/// <summary>Fetching the live page, keeping the last good schedule, and never failing to a caller.</summary>
public sealed class ScheduleServiceTests : IDisposable
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly Uri PageUrl = new("https://burst.test/schedule");
    readonly WebSettings _settings = TestSettings.Create();
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero));
    Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => StubHandler.Html(ScheduleParserTests.Fixture);
    readonly List<HttpRequestMessage> _requests = [];

    public void Dispose()
    {
        if (Directory.Exists(_settings.CacheDir)) Directory.Delete(_settings.CacheDir, recursive: true);
    }

    sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    sealed class Handler(ScheduleServiceTests t) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            t._requests.Add(request);
            return Task.FromResult(t._respond(request));
        }
    }

    ScheduleService Service(ScheduleSource? source = null) =>
        new(source ?? new ScheduleSource("burst", PageUrl, London, null), _settings, new Factory(new Handler(this)), _time, NullLogger<ScheduleService>.Instance);

    [Fact]
    public async Task A_good_page_becomes_the_schedule_and_is_saved_in_the_cache()
    {
        var service = Service();
        await service.RefreshAsync(CancellationToken.None);

        var now = service.Current;
        Assert.Equal(90, now.Schedule!.Slots.Count);
        Assert.Null(now.LastError);
        Assert.False(now.FromSavedCopy);
        Assert.Equal(_time.GetUtcNow(), now.FetchedUtc);
        Assert.True(File.Exists(service.SavedPath));
        Assert.StartsWith(Path.Combine(_settings.CacheDir, "schedule"), service.SavedPath);

        var request = Assert.Single(_requests);
        Assert.Equal(PageUrl, request.RequestUri);
        Assert.Contains("WebAudioArchive", request.Headers.UserAgent.ToString());

        var show = service.Find("burst-20261015T1900Z");
        Assert.Equal("Backseat Aux", show!.Title);
        Assert.Null(service.Find("burst-20261015T1930Z"));
        Assert.Null(service.Find("other-20261015T1900Z"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("garbage")]
    [InlineData("throws")]
    [InlineData("timeout")]
    public async Task A_failed_refresh_keeps_the_last_good_schedule_and_reports_why(string failure)
    {
        var service = Service();
        await service.RefreshAsync(CancellationToken.None);
        var good = service.Current;
        _time.Advance(TimeSpan.FromMinutes(15));

        _respond = failure switch
        {
            "status" => _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "garbage" => _ => StubHandler.Html("<html><body><p>We are redesigning our site</p></body></html>"),
            "throws" => _ => throw new HttpRequestException("Name or service not known (burst.test:443)"),
            _ => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        };
        await service.RefreshAsync(CancellationToken.None);

        var now = service.Current;
        Assert.Same(good.Schedule, now.Schedule);
        Assert.Equal(good.FetchedUtc, now.FetchedUtc);
        Assert.Equal(_time.GetUtcNow(), now.LastAttemptUtc);
        Assert.Contains(failure switch
        {
            "status" => "HTTP 502",
            "garbage" => "not understood",
            "throws" => "could not be fetched",
            _ => "did not answer",
        }, now.LastError);
        Assert.Equal("Backseat Aux", service.Find("burst-20261015T1900Z")!.Title);

        // And it recovers on the next good fetch.
        _respond = _ => StubHandler.Html(ScheduleParserTests.Fixture);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Null(service.Current.LastError);
    }

    [Fact]
    public async Task A_restart_starts_from_the_saved_schedule_before_any_fetch()
    {
        await Service().RefreshAsync(CancellationToken.None);
        _respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var restarted = Service();
        var saved = restarted.Current;
        Assert.True(saved.FromSavedCopy);
        Assert.Equal(90, saved.Schedule!.Slots.Count);
        Assert.Equal("Mags & Lou: Live from Whipperton", saved.Schedule.Slots.Single(s => s.Day == DayOfWeek.Wednesday && s.LocalStart.Hour == 14).Title);

        await restarted.RefreshAsync(CancellationToken.None);
        Assert.NotNull(restarted.Current.Schedule);
        Assert.Contains("HTTP 503", restarted.Current.LastError);
    }

    [Fact]
    public async Task With_no_good_schedule_ever_there_is_no_schedule_and_the_reason_is_kept()
    {
        _respond = _ => StubHandler.Html("<table><tr><th>Mon</th></tr></table>");
        var service = Service();
        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.Current.Schedule);
        Assert.Contains("Monday to Sunday", service.Current.LastError);
        Assert.Empty(service.Occurrences(_time.GetUtcNow().AddDays(-7), _time.GetUtcNow()));
        Assert.False(File.Exists(service.SavedPath));
        Assert.Null(await Task.Run(() => service.Find("burst-20261015T1900Z")));
    }

    [Fact]
    public async Task A_corrupt_saved_copy_is_ignored()
    {
        var service = Service();
        Directory.CreateDirectory(Path.GetDirectoryName(service.SavedPath)!);
        await File.WriteAllTextAsync(service.SavedPath, "{ not json");

        Assert.Null(service.Current.Schedule);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(90, service.Current.Schedule!.Slots.Count);
    }

    [Fact]
    public async Task Without_a_schedule_url_nothing_is_fetched_and_the_reason_is_given()
    {
        var path = Path.Combine(_settings.CacheDir, "stations.json");
        Directory.CreateDirectory(_settings.CacheDir);
        await File.WriteAllTextAsync(path, """{ "stations": [ { "id": "burst", "name": "Burst", "listenUrl": "https://burst.test/listen", "timezone": "Europe/London" } ] }""");

        var source = ScheduleSource.Load("burst", path);
        Assert.False(source.IsConfigured);
        Assert.Contains("no scheduleUrl", source.Problem);
        Assert.Contains("WAA_STATIONS_CONFIG", ScheduleSource.Load("burst", null).Problem);
        Assert.Contains("not in", ScheduleSource.Load("other", path).Problem);

        var service = Service(source);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Empty(_requests);
        Assert.Null(service.Current.Schedule);
        Assert.Contains("no scheduleUrl", service.Current.LastError);
    }

    [Fact]
    public void The_repository_stations_config_gives_burst_its_schedule_url_and_london_time()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "config", "stations.json"))) root = Path.GetDirectoryName(root)!;

        var source = ScheduleSource.Load("burst", Path.Combine(root, "config", "stations.json"));

        Assert.True(source.IsConfigured, source.Problem);
        Assert.Equal("https://www.burstradio.org/schedule", source.Url!.ToString());
        Assert.Equal(TimeSpan.FromHours(1), source.Zone!.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0)));
    }
}
