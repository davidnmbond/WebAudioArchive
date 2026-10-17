extern alias web;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Analysis;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class AnalysisWebTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-aweb-").FullName;

    public AnalysisWebTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        var paths = Paths;
        AnalysisJson.Write(paths.DayEvents(new DateOnly(2026, 10, 15)), Day("day:2026-10-15",
            Event(EventTypes.Swear, T.AddMinutes(10)), Event(EventTypes.Filler, T.AddMinutes(11)), Event(EventTypes.DeadAir, T.AddMinutes(30), 8),
            Event(EventTypes.Clipping, T.AddHours(2)),
            Event(EventTypes.Swear, T.AddMinutes(20), suppressed: "repetition")));
        AnalysisJson.Write(Path.Combine(paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.TranscriptFile),
            TranscriptFixtures.Of(T, "Good evening, this is Backseat Aux.", "Next up, a song.") with { JobId = "a/2026/10-15/190000" });
        AnalysisJson.Write(Path.Combine(paths.ShowDir("burst-20261015T1900Z", T), AnalysisPaths.ShowAnalysisFile),
            new ShowAnalysis(1, "burst-20261015T1900Z", "burst", "Backseat Aux", T, T.AddHours(1), T.AddMinutes(-5), T.AddMinutes(65), T.AddHours(2), true,
                [], new ShowCounts(0, 1, 0, 1, 0, 0, 1, 8, 1, 0.1, null, 3600, 1), []));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string AnalysisDir => Path.Combine(_root, "analysis");
    AnalysisPaths Paths => new(AnalysisDir, "burst");

    static AnalysisEvent Event(string type, DateTimeOffset at, double seconds = 0.4, string? suppressed = null) => new(
        AnalysisEvent.MakeId(type, "a/2026/10-15/190000", (at - T).TotalSeconds, suppressed), type, at, at.AddSeconds(seconds), Severities.Medium, 0.9, type, "1",
        EventSources.Transcript, "a", "a/2026/10-15/190000", (at - T).TotalSeconds, seconds, type + " label",
        type == EventTypes.Swear ? new Dictionary<string, string> { ["suppressed"] = suppressed ?? "no" } : null);

    static EventsDocument Day(string scope, params AnalysisEvent[] events) => new(1, "burst", scope, new DateTimeOffset(T.UtcDateTime.Date, TimeSpan.Zero),
        new DateTimeOffset(T.UtcDateTime.Date.AddDays(1), TimeSpan.Zero), T, new Dictionary<string, string>(), [], null, events);

    AnalysisReader Reader(bool configured = true) =>
        new(TestSettings.Create(("a", Path.Combine(_root, "a"))) with { AnalysisDir = configured ? AnalysisDir : null });

    [Fact]
    public void Events_come_from_the_day_files_in_range_and_by_type()
    {
        var r = Reader();
        Assert.Equal(3, r.Events(T, T.AddHours(1), null).Count);
        Assert.Equal(EventTypes.Swear, Assert.Single(r.Events(T, T.AddHours(1), "swear")).Type);
        Assert.Equal(2, r.Events(T, T.AddHours(1), "swear, filler").Count);
        Assert.Empty(r.Events(T.AddHours(5), T.AddHours(6), null));
    }

    [Fact]
    public void Suppressed_swear_events_are_review_only_and_left_out_unless_asked_for()
    {
        var r = Reader();

        var normal = r.Events(T, T.AddHours(1), "swear");
        Assert.All(normal, e => Assert.Equal("no", e.Evidence!["suppressed"]));
        Assert.Single(normal);

        var review = r.Events(T, T.AddHours(1), "swear", includeReview: true);
        Assert.Equal(2, review.Count);
        Assert.Single(review, e => e.IsReviewOnly);
        Assert.Equal(4, r.Events(T, T.AddHours(1), null, includeReview: true).Count);
    }

    [Fact]
    public void Transcript_lines_and_show_files_are_read()
    {
        var r = Reader();
        var lines = r.Transcript(T, T.AddMinutes(30));
        Assert.Equal(new[] { "Good evening, this is Backseat Aux.", "Next up, a song." }, lines.Select(l => l.Text));

        var (analysis, enrichment) = r.Show("burst-20261015T1900Z");
        Assert.Equal("Backseat Aux", analysis!.Title);
        Assert.Null(enrichment);
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => r.Show("burst-20261015T2300Z")).StatusCode);
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => r.Show("../../etc")).StatusCode);
    }

    [Theory]
    [InlineData("burst-20261015T1900Z/..")]
    [InlineData("burst-20261015T1900Z%2f..")]
    [InlineData("..\\..\\x")]
    [InlineData("other-20261015T1900Z")]
    [InlineData("burst-20261015T1900")]
    [InlineData("BURST-20261015T1900Z")]
    [InlineData("burst-20261015T1900Z ")]
    [InlineData("burst-20261015T1900Z.json")]
    [InlineData("")]
    public void Only_ids_of_discovered_shows_reach_the_file_system(string id)
    {
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => Reader().Show(id)).StatusCode);
    }

    [Theory]
    [InlineData(null, "2026-10-15T20:00:00Z")]
    [InlineData("2026-10-15T19:00:00", "2026-10-15T20:00:00Z")]
    [InlineData("2026-10-15T20:00:00Z", "2026-10-15T19:00:00Z")]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-15T00:00:00Z")]
    public void Bad_ranges_are_400(string? from, string to)
    {
        Assert.Equal(400, Assert.Throws<AnalysisQueryException>(() => AnalysisReader.Range(from, to, AnalysisReader.MaxEventsRange)).StatusCode);
    }

    [Fact]
    public void Without_analysis_the_reader_says_404()
    {
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => Reader(configured: false).Events(T, T.AddHours(1), null)).StatusCode);
    }

    [Fact]
    public void Events_become_player_markers_and_dead_air_and_talk_become_segments()
    {
        var clock = new ClipClock([(0, T.AddMinutes(-5))], 4200);
        var events = Reader().Events(T, T.AddHours(1), null);
        var lines = Reader().Transcript(T, T.AddHours(1));

        var markers = AnalysisMarkers.Markers(events, clock);
        var segments = AnalysisMarkers.Segments(events, lines, clock);

        var swear = Assert.Single(markers, m => m.Kind == EventTypes.Swear);
        Assert.Equal(TimeSpan.FromMinutes(15), swear.Position);
        Assert.Contains("19:10:00 UTC", swear.Label);
        Assert.Contains("confidence 0.90", swear.Detail);
        var dead = Assert.Single(segments, s => s.Kind == EventTypes.DeadAir);
        Assert.Equal(TimeSpan.FromMinutes(35), dead.Start);
        Assert.Equal(TimeSpan.FromMinutes(35).Add(TimeSpan.FromSeconds(8)), dead.End);
        Assert.Equal(2, segments.Count(s => s.Kind == AnalysisMarkers.Talk));
        foreach (var kind in new[] { EventTypes.Swear, EventTypes.Clipping, EventTypes.Filler, EventTypes.DeadAir, EventTypes.Song, AnalysisMarkers.Talk, AnalysisMarkers.SwearReview })
        {
            Assert.True(AnalysisMarkers.KindLabels.ContainsKey(kind));
            Assert.True(AnalysisMarkers.KindColors.ContainsKey(kind));
        }
    }

    [Fact]
    public void A_suppressed_swear_is_never_drawn_as_a_swear_marker()
    {
        var clock = new ClipClock([(0, T.AddMinutes(-5))], 4200);
        var events = Reader().Events(T, T.AddHours(1), null, includeReview: true);

        var markers = AnalysisMarkers.Markers(events, clock);

        Assert.Single(markers, m => m.Kind == EventTypes.Swear);
        var review = Assert.Single(markers, m => m.Kind == AnalysisMarkers.SwearReview);
        Assert.Contains("review only", review.Detail);
        Assert.Contains("probably", AnalysisMarkers.KindLabels[AnalysisMarkers.SwearReview]);
    }

    [Fact]
    public void The_web_project_does_not_reference_the_analysis_project()
    {
        var referenced = typeof(AnalysisReader).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("WebAudioArchive.Analysis", referenced);
        Assert.Contains("WebAudioArchive.Core", referenced);
    }

    WebApplicationFactory<WebEntryPoint> Host(bool analysis) => new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(b =>
    {
        b.UseSetting("WAA_RECORDERS", $"a={Path.Combine(_root, "a")}");
        b.UseSetting("WAA_CACHE", Path.Combine(_root, "cache"));
        if (analysis) b.UseSetting("WAA_ANALYSIS", AnalysisDir);
    });

    [Fact]
    public async Task The_endpoints_serve_json_and_errors()
    {
        await using var factory = Host(analysis: true);
        var client = factory.CreateClient();

        var events = await client.GetAsync("/api/analysis/events?from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z&types=swear");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var body = await events.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"swear\"", body);
        Assert.Contains("\"link\":\"/player?from=2026-10-15T19:09:55Z", body);
        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/analysis/transcript?from=2026-10-15T19:00:00Z&to=2026-10-15T19:30:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/analysis/shows/burst-20261015T1900Z")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/shows/burst-20261016T1900Z")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/shows/..%2f..%2fetc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analysis/events?from=yesterday&to=today")).StatusCode);
    }

    [Fact]
    public async Task The_events_endpoint_hides_review_only_swears_unless_asked()
    {
        await using var factory = Host(analysis: true);
        var client = factory.CreateClient();
        const string range = "from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z&types=swear";

        var normal = JsonDocument.Parse(await client.GetStringAsync("/api/analysis/events?" + range));
        Assert.Equal(1, normal.RootElement.GetProperty("count").GetInt32());
        Assert.DoesNotContain("repetition", normal.RootElement.ToString());

        var review = JsonDocument.Parse(await client.GetStringAsync("/api/analysis/events?" + range + "&includeReview=true"));
        Assert.Equal(2, review.RootElement.GetProperty("count").GetInt32());
        Assert.Contains("repetition", review.RootElement.ToString());
    }

    [Fact]
    public async Task Without_waa_analysis_the_endpoints_are_404()
    {
        await using var factory = Host(analysis: false);
        var response = await factory.CreateClient().GetAsync("/api/analysis/events?from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("WAA_ANALYSIS", await response.Content.ReadAsStringAsync());
    }
}
