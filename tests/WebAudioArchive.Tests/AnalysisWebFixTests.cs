extern alias web;

using System.Net;
using System.Text.Json;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Analysis;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

// Fix round 1 (reviewer findings): transcript reads bounded, review-only flag, date bounds, error mapping, markers inside the clip.
public sealed partial class AnalysisWebTests
{
    void WriteHourlyTranscripts(params int[] hours)
    {
        foreach (var h in hours)
        {
            var start = new DateTimeOffset(2026, 10, 15, h, 0, 0, TimeSpan.Zero);
            var job = $"a/2026/10-15/{h:00}0000";
            AnalysisJson.Write(Path.Combine(Paths.SegmentDir(job), AnalysisPaths.TranscriptFile),
                TranscriptFixtures.Of(start, $"Line from hour {h}.") with { JobId = job });
        }
    }

    [Fact]
    public void A_one_hour_request_opens_only_transcripts_that_can_overlap_it()
    {
        WriteHourlyTranscripts(14, 15, 16, 17, 18, 19, 20, 21, 22);
        var opened = new List<string>();

        var docs = AnalysisStore.Transcripts(Paths, T, T.AddHours(1), p =>
        {
            opened.Add(Path.GetFileName(Path.GetDirectoryName(p)!));
            return AnalysisStore.ReadTranscript(p);
        });

        Assert.Contains("190000", opened);
        Assert.DoesNotContain("140000", opened);
        Assert.DoesNotContain("150000", opened);
        Assert.DoesNotContain("160000", opened);
        Assert.DoesNotContain("200000", opened);
        Assert.DoesNotContain("210000", opened);
        Assert.DoesNotContain("220000", opened);
        Assert.True(opened.Count <= 3, string.Join(",", opened));
        Assert.Single(docs);
    }

    [Fact]
    public void An_oversized_transcript_is_refused_with_413_not_loaded()
    {
        var file = Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.TranscriptFile);
        File.WriteAllBytes(file, new byte[AnalysisStore.MaxTranscriptBytes + 1]);

        Assert.Throws<TranscriptTooLargeException>(() => AnalysisStore.ReadTranscript(file));
        var ex = Assert.Throws<AnalysisQueryException>(() => Reader().Transcript(T, T.AddMinutes(30)));
        Assert.Equal(413, ex.StatusCode);
        Assert.DoesNotContain(Path.GetTempPath(), ex.Message);
    }

    [Fact]
    public void Transcript_files_are_cached_by_path_time_and_length()
    {
        var r = Reader();
        Assert.Equal(2, r.Transcript(T, T.AddMinutes(30)).Count);
        Assert.Equal(2, r.Transcript(T, T.AddMinutes(30)).Count);
        Assert.Equal(1, r.TranscriptFileReads);

        var file = Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.TranscriptFile);
        AnalysisJson.Write(file, TranscriptFixtures.Of(T, "Only one line now, a different length.") with { JobId = "a/2026/10-15/190000" });
        Assert.Single(r.Transcript(T, T.AddMinutes(30)));
        Assert.Equal(2, r.TranscriptFileReads);
    }

    [Fact]
    public async Task Review_only_events_are_flagged_in_the_json_and_counted_only_when_asked_for()
    {
        await using var factory = Host(analysis: true);
        var client = factory.CreateClient();
        const string range = "from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z&types=swear";

        var normal = JsonDocument.Parse(await client.GetStringAsync("/api/analysis/events?" + range)).RootElement;
        Assert.Equal(1, normal.GetProperty("count").GetInt32());
        Assert.All(normal.GetProperty("events").EnumerateArray(), e => Assert.False(e.GetProperty("reviewOnly").GetBoolean()));

        var all = JsonDocument.Parse(await client.GetStringAsync("/api/analysis/events?" + range + "&includeReview=true")).RootElement;
        Assert.Equal(2, all.GetProperty("count").GetInt32());
        Assert.True(all.GetProperty("includeReview").GetBoolean());
        var flags = all.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("reviewOnly").GetBoolean()).Order().ToArray();
        Assert.Equal(new[] { false, true }, flags);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", "0001-01-01T06:00:00Z")]
    [InlineData("9999-12-31T00:00:00Z", "9999-12-31T12:00:00Z")]
    [InlineData("1999-12-31T23:00:00Z", "2000-01-01T01:00:00Z")]
    [InlineData("2100-12-31T23:00:00Z", "2101-01-01T01:00:00Z")]
    public async Task Extreme_dates_are_400_not_500(string from, string to)
    {
        Assert.Equal(400, Assert.Throws<AnalysisQueryException>(() => AnalysisReader.Range(from, to, AnalysisReader.MaxEventsRange)).StatusCode);

        await using var factory = Host(analysis: true);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/analysis/events?from={from}&to={to}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/analysis/transcript?from={from}&to={to}")).StatusCode);
    }

    [Fact]
    public void Io_errors_are_500_with_a_generic_message_and_query_errors_keep_their_status()
    {
        var io = AnalysisReader.Run(() => throw new IOException(@"Could not read C:\secret\data\analysis\x.json"));
        Assert.Equal(500, io.Status);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(io.Body));
        Assert.Equal(500, AnalysisReader.Run(() => throw new UnauthorizedAccessException("denied")).Status);
        Assert.Equal(400, AnalysisReader.Run(() => throw new AnalysisQueryException(400, "bad")).Status);
        Assert.Equal(200, AnalysisReader.Run(() => new { ok = true }).Status);
    }

    [Fact]
    public void Markers_and_segments_outside_the_clip_are_dropped_not_clamped_to_its_edges()
    {
        var clock = new ClipClock([(0, T)], 600);
        var inside = Event(EventTypes.Swear, T.AddMinutes(5));
        var before = Event(EventTypes.Swear, T.AddMinutes(-1));
        var after = Event(EventTypes.Swear, T.AddMinutes(20));
        var deadEdge = Event(EventTypes.DeadAir, T.AddSeconds(590), 30);
        var deadOut = Event(EventTypes.DeadAir, T.AddMinutes(30), 30);
        TimedLine Line(DateTimeOffset at) => new(at, at.AddSeconds(5), "hello", "a", "a/2026/10-15/190000", []);

        var markers = AnalysisMarkers.Markers([before, inside, after], clock);
        var segments = AnalysisMarkers.Segments([deadEdge, deadOut], [Line(T.AddMinutes(-10)), Line(T.AddMinutes(2)), Line(T.AddMinutes(40))], clock);

        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(markers).Position);
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, s => s.Kind == EventTypes.DeadAir && s.End == TimeSpan.FromSeconds(600));
        Assert.Single(segments, s => s.Kind == AnalysisMarkers.Talk);
    }

    [Fact]
    public void The_web_project_file_has_no_reference_to_the_analysis_project()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebAudioArchive.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var csproj = File.ReadAllText(Path.Combine(dir!.FullName, "src", "WebAudioArchive.Web", "WebAudioArchive.Web.csproj"));
        Assert.DoesNotContain("WebAudioArchive.Analysis", csproj);
        Assert.Contains("WebAudioArchive.Core.csproj", csproj);
    }
}
