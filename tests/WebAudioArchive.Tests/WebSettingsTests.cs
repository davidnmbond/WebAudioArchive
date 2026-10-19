extern alias web;

using Microsoft.AspNetCore.Hosting;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Clips;
using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests;

public sealed class WebSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-set-" + Guid.NewGuid().ToString("N"));

    public WebSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Reads_recorders_in_order_with_health_urls_and_defaults()
    {
        var s = WebSettings.Load(Env(
            ("WAA_RECORDERS", $"a={_root}/a, b={_root}/b"),
            ("WAA_RECORDER_HEALTH", "a=http://recorder-a:8080/healthz,b=http://recorder-b:8080/healthz")));

        Assert.Equal("burst", s.Station);
        Assert.Equal("/cache", s.CacheDir);
        Assert.Equal(["a", "b"], s.Recorders.Select(r => r.Id));
        Assert.Equal($"{_root}/b", s.Recorders[1].Root);
        Assert.Equal(new Uri("http://recorder-b:8080/healthz"), s.Recorders[1].HealthUrl);
        Assert.Equal("B", s.Recorders[1].Label);
    }

    [Fact]
    public void A_recorder_named_merged_is_refused_because_that_id_is_the_merged_copy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "merged"));
        var ex = Assert.Throws<SettingsException>(() => WebSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a,merged={_root}/merged"))));
        Assert.Contains("WAA_RECORDERS", ex.Message);
        Assert.Contains("'merged'", ex.Message);
        Assert.Contains("reserved", ex.Message);
    }

    [Theory]
    [InlineData(null, "WAA_RECORDERS is not set")]
    [InlineData("", "WAA_RECORDERS is not set")]
    [InlineData("a", "WAA_RECORDERS: 'a' is not of the form id=value")]
    [InlineData("a=/no/such/folder/for/waa", "WAA_RECORDERS: the data root '/no/such/folder/for/waa' for recorder 'a' does not exist")]
    [InlineData("A B=/x", "not a valid recorder id")]
    public void Missing_or_wrong_recorders_fail_naming_the_setting(string? value, string message)
    {
        var ex = Assert.Throws<SettingsException>(() => WebSettings.Load(Env(("WAA_RECORDERS", value!))));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Duplicate_recorders_unknown_health_ids_and_bad_stations_fail()
    {
        Assert.Contains("listed twice", Assert.Throws<SettingsException>(() =>
            WebSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a,a={_root}/b")))).Message);
        Assert.Contains("WAA_RECORDER_HEALTH: recorder 'c'", Assert.Throws<SettingsException>(() =>
            WebSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a"), ("WAA_RECORDER_HEALTH", "c=http://x/healthz")))).Message);
        Assert.Contains("WAA_STATION", Assert.Throws<SettingsException>(() =>
            WebSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a"), ("WAA_STATION", "../etc")))).Message);
    }

    [Fact]
    public void The_app_refuses_to_start_without_recorders()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WAA_RECORDERS", "a=/no/such/folder/for/waa");
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(true, 0.4, null, "Green")]
    [InlineData(true, 30.0, null, "Amber")]
    [InlineData(false, 3.0, null, "Amber")]
    [InlineData(true, 1.0, "disk full", "Amber")]
    [InlineData(true, 121.0, null, "Red")]
    [InlineData(false, null, null, "Red")]
    public void Health_levels(bool connected, double? seconds, string? lastError, string level)
    {
        var h = new RecorderHealth(true, null, "x", 10, connected, seconds, lastError, null, null, 0);
        Assert.Equal(level, StatusRules.Classify(h).Level.ToString());
    }

    [Fact]
    public void Unreachable_and_unconfigured_health()
    {
        Assert.Equal(HealthLevel.Red, StatusRules.Classify(RecorderHealth.Unreachable("refused")).Level);
        Assert.Contains("unreachable", StatusRules.Classify(RecorderHealth.Unreachable("refused")).Summary);
        Assert.Equal(HealthLevel.Amber, StatusRules.Classify(null).Level);
        Assert.False(StatusRules.Parse("<html>", "burst").Reachable);
        Assert.False(StatusRules.Parse("""{ "stations": [ { "id": "other" } ] }""", "burst").Reachable);
    }

    [Fact]
    public void Health_times_are_converted_to_utc()
    {
        var h = StatusRules.Parse("""
            { "instance": "home-a", "uptimeSeconds": 12, "stations": [ { "id": "burst", "connected": true,
              "secondsSinceLastFrameWritten": 0.2, "currentSegmentStartUtc": "2026-10-15T16:13:18+01:00", "reconnects": 2 } ] }
            """, "burst");

        Assert.True(h.Reachable);
        Assert.Equal(TimeSpan.Zero, h.CurrentSegmentStartUtc!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 15, 13, 18, TimeSpan.Zero), h.CurrentSegmentStartUtc);
        Assert.Equal(2, h.Reconnects);
        Assert.Equal(12, h.UptimeSeconds);
    }

    [Fact]
    public void Play_links_open_the_show_at_a_utc_time()
    {
        var at = new DateTimeOffset(2026, 10, 15, 15, 15, 0, TimeSpan.Zero);
        Assert.Equal("/at?utc=2026-10-15T15:15:00Z&recorder=a", PlayLink.For(at, "a"));
        Assert.Equal("/at?utc=2026-10-15T15:15:00Z&recorder=a%2Bb", PlayLink.For(at, "a+b"));
    }
}
