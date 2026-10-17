extern alias web;

using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

/// <summary>The cross-container contract: what the web app's ScheduleService saves is what the analysis ScheduleProvider reads.</summary>
public sealed class ScheduleFileContractTests : IDisposable
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    readonly WebSettings _settings = TestSettings.Create();
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_settings.CacheDir)) Directory.Delete(_settings.CacheDir, recursive: true);
    }

    sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task The_schedule_the_web_app_saves_is_read_back_by_the_analysis_provider_with_the_same_shows()
    {
        var service = new ScheduleService(new ScheduleSource("burst", new Uri("https://burst.test/schedule"), London, null), _settings,
            new Factory(new StubHandler(_ => StubHandler.Html(ScheduleParserTests.Fixture))), _time, NullLogger<ScheduleService>.Instance);
        await service.RefreshAsync(CancellationToken.None);
        Assert.True(File.Exists(service.SavedPath));

        var provider = new ScheduleProvider(service.SavedPath, null, "burst", NullLogger.Instance);
        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var analysis = provider.Occurrences(from, from.AddDays(7));
        var web = service.Occurrences(from, from.AddDays(7));

        Assert.NotEmpty(analysis);
        Assert.Equal(web.Select(o => (o.Id, o.Title, o.StartUtc, o.EndUtc)), analysis.Select(o => (o.Id, o.Title, o.StartUtc, o.EndUtc)));
        Assert.Equal(service.Current.FetchedUtc, provider.FetchedUtc);
        Assert.Equal("Backseat Aux", provider.TitleAt(new DateTimeOffset(2026, 10, 15, 19, 30, 0, TimeSpan.Zero)));
    }
}
