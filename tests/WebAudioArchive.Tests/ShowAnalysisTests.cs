using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class ShowAnalysisTests : IDisposable
{
    // Thursday 15 October 2026, British Summer Time: Backseat Aux at 20:00 local is 19:00 to 20:00 UTC.
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-shows-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    const string SavedSchedule = """
        { "station": "burst", "url": "https://www.burstradio.org/schedule", "timezone": "Europe/London",
          "fetchedUtc": "2026-10-15T12:00:00+00:00",
          "slots": [ { "day": "Thursday", "localStart": "20:00", "minutes": 60, "title": "Backseat Aux" },
                     { "day": "Thursday", "localStart": "21:00", "minutes": 60, "title": "Late Show" } ] }
        """;

    string WriteSchedule()
    {
        var path = Path.Combine(_root, "burst.json");
        File.WriteAllText(path, SavedSchedule);
        return path;
    }

    static AnalysisEvent Event(string type, DateTimeOffset at, string recorder = "a", string? tier = null, bool before = false, double seconds = 0.4) => new(
        AnalysisEvent.MakeId(type, recorder + "/job", (at - Show).TotalSeconds), type, at, at.AddSeconds(seconds), Severities.Medium, 0.9, type, "1",
        EventSources.Transcript, recorder, recorder + "/job", 0, seconds, type,
        tier is null ? null : new Dictionary<string, string> { ["tier"] = tier, ["beforeWatershed"] = before ? "true" : "false" });

    static EventsDocument Segment(string recorder, DateTimeOffset from, DateTimeOffset to, params AnalysisEvent[] events) => new(
        1, "burst", $"segment:{recorder}/{from:yyyy}/{from:MM-dd}/{from:HHmmss}", from, to, from, new Dictionary<string, string> { ["detectors"] = "dv" },
        [new SourceSpan(recorder, from, to)], null, events);

    [Fact]
    public void The_saved_schedule_written_by_the_web_app_maps_to_utc_shows()
    {
        var provider = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance);

        var shows = provider.Occurrences(Show.AddHours(-1), Show.AddHours(3));

        Assert.Equal(new[] { "Backseat Aux", "Late Show" }, shows.Select(s => s.Title));
        Assert.Equal(Show, shows[0].StartUtc);
        Assert.Equal("burst-20261015T1900Z", shows[0].Id);
        Assert.Equal("Backseat Aux", provider.TitleAt(Show.AddMinutes(30)));
        Assert.Null(provider.TitleAt(Show.AddHours(-3)));
        Assert.Equal("Europe/London", provider.Zone.Id);
    }

    [Fact]
    public void No_schedule_file_means_no_shows_and_no_titles()
    {
        var provider = new ScheduleProvider(Path.Combine(_root, "missing.json"), null, "burst", NullLogger.Instance);
        Assert.Empty(provider.Occurrences(Show, Show.AddDays(1)));
        Assert.Null(provider.TitleAt(Show));
    }

    [Fact]
    public void The_day_timeline_prefers_a_and_files_each_event_by_its_start()
    {
        var day = new DateOnly(2026, 10, 15);
        var a = Segment("a", new(2026, 10, 15, 23, 0, 0, TimeSpan.Zero), new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero),
            Event(EventTypes.Swear, new(2026, 10, 15, 23, 59, 59, TimeSpan.Zero)));
        var b = Segment("b", new(2026, 10, 15, 23, 30, 0, TimeSpan.Zero), new(2026, 10, 16, 0, 30, 0, TimeSpan.Zero),
            Event(EventTypes.Swear, new(2026, 10, 15, 23, 45, 0, TimeSpan.Zero), "b"),
            Event(EventTypes.Filler, new(2026, 10, 16, 0, 10, 0, TimeSpan.Zero), "b"));

        var d15 = DayTimeline.BuildDay("burst", day, [a, b], ["a", "b"], _clock.UtcNow);
        var d16 = DayTimeline.BuildDay("burst", day.AddDays(1), [a, b], ["a", "b"], _clock.UtcNow);

        Assert.Equal("day:2026-10-15", d15.Scope);
        Assert.Equal("a", Assert.Single(d15.Events).Recorder);
        Assert.Equal(EventTypes.Filler, Assert.Single(d16.Events).Type);
        Assert.Single(DayTimeline.Merge([a, a], ["a", "b"]));
    }

    [Fact]
    public void Show_counts_cover_the_show_proper_and_the_filler_rate_is_per_minute_of_speech()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent[] events =
        [
            Event(EventTypes.Swear, Show.AddMinutes(10), tier: "strong", before: true),
            Event(EventTypes.Swear, Show.AddMinutes(-2), tier: "mild"),
            Event(EventTypes.Clipping, Show.AddMinutes(30), seconds: 0.12),
            Event(EventTypes.DeadAir, Show.AddMinutes(40), seconds: 6),
            Event(EventTypes.Filler, Show.AddMinutes(5)),
            Event(EventTypes.Filler, Show.AddMinutes(6)),
            Event(EventTypes.Filler, Show.AddMinutes(7)),
        ];
        var lines = Enumerable.Range(0, 10)
            .Select(i => new TimedLine(Show.AddMinutes(i * 5), Show.AddMinutes(i * 5).AddSeconds(30), "words", "a", "a/job", []))
            .ToList();

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, lines,
            [new ShowSource("a", "a/2026/10-15/190000", Show, Show.AddHours(1))], complete: true, _clock.UtcNow);

        Assert.Equal(Show.AddMinutes(-5), doc.WindowFromUtc);
        Assert.Equal(1, doc.Counts.SwearsStrong);
        Assert.Equal(0, doc.Counts.SwearsMild);
        Assert.Equal(1, doc.Counts.SwearsBeforeWatershed);
        Assert.Equal(1, doc.Counts.ClippingEvents);
        Assert.Equal(120, doc.Counts.ClippedMs, 3);
        Assert.Equal(6, doc.Counts.DeadAirSeconds, 3);
        Assert.Equal(3, doc.Counts.Fillers);
        Assert.Equal(5, doc.Counts.SpeechMinutes, 6);
        Assert.Equal(0.6, doc.Counts.FillersPerMinute!.Value, 6);
        Assert.Equal(3600, doc.Counts.AnalysedSeconds, 3);
        Assert.Contains(doc.Notes, n => n.Contains("estimate"));
    }

    [Fact]
    public void Less_than_a_minute_of_speech_gives_no_filler_rate()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), [Event(EventTypes.Filler, Show.AddMinutes(1))],
            [new TimedLine(Show, Show.AddSeconds(20), "um", "a", "a/job", [])], [], complete: true, _clock.UtcNow);
        Assert.Null(doc.Counts.FillersPerMinute);
        Assert.Contains(doc.Notes, n => n.Contains("not analysed"));
    }

    [Fact]
    public void The_processor_writes_day_files_and_finished_shows_from_segment_results()
    {
        var job = "a/2026/10-15/190000";
        var dir = Paths.SegmentDir(job);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), Segment("a", Show, Show.AddHours(1), Event(EventTypes.Filler, Show.AddMinutes(3))) with { Scope = "segment:" + job });
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), TranscriptFixtures.Of(Show, "um hello there", "more words here") with { JobId = job });
        var processor = new ShowProcessor(Paths, new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance), ["a", "b"], TimeSpan.FromMinutes(5), _clock, NullLogger.Instance);

        var docs = processor.Update(Show, Show.AddHours(3), pending: []);

        var backseat = Assert.Single(docs, d => d.Title == "Backseat Aux");
        Assert.True(backseat.Complete);
        Assert.Equal(1, backseat.Counts.Fillers);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir("burst-20261015T1900Z", Show), AnalysisPaths.ShowAnalysisFile)));
        Assert.True(File.Exists(Paths.DayEvents(new DateOnly(2026, 10, 15))));

        var pending = processor.Update(Show, Show.AddHours(3), pending: [new SourceSpan("a", Show.AddMinutes(50), Show.AddHours(2))]);
        Assert.False(Assert.Single(pending, d => d.Title == "Backseat Aux").Complete);

        _clock.Set(Show.AddMinutes(30));
        Assert.Empty(processor.Update(Show, Show.AddHours(3), pending: []));
    }

    [Fact]
    public void Suppressed_swear_runs_are_review_only_and_never_counted()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        static AnalysisEvent Suppressed(DateTimeOffset at, string tier) => Event(EventTypes.Swear, at, tier: tier, before: true) with
        {
            Id = "suppressed-" + at.ToUnixTimeSeconds() + tier,
            Evidence = new Dictionary<string, string> { ["tier"] = tier, ["beforeWatershed"] = "true", ["suppressed"] = "repetition", ["runLength"] = "5" },
        };
        static AnalysisEvent Normal(DateTimeOffset at, string tier, bool before) => Event(EventTypes.Swear, at, tier: tier, before: before) with
        {
            Evidence = new Dictionary<string, string> { ["tier"] = tier, ["beforeWatershed"] = before ? "true" : "false", ["suppressed"] = "no" },
        };
        AnalysisEvent[] events =
        [
            Normal(Show.AddMinutes(10), "strong", before: true),
            Normal(Show.AddMinutes(11), "mild", before: false),
            Suppressed(Show.AddMinutes(20), "severe"),
            Suppressed(Show.AddMinutes(21), "strong"),
            Suppressed(Show.AddMinutes(22), "mild"),
        ];

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, [], [], complete: true, _clock.UtcNow);

        Assert.Equal(1, doc.Counts.SwearsStrong);
        Assert.Equal(1, doc.Counts.SwearsMild);
        Assert.Equal(0, doc.Counts.SwearsSevere);
        Assert.Equal(1, doc.Counts.SwearsBeforeWatershed);
        Assert.Equal(3, doc.Counts.SuppressedSwearRuns);
    }

    [Fact]
    public void Dead_air_and_clipping_are_clipped_to_the_show_proper_before_their_duration_is_counted()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent[] events =
        [
            Event(EventTypes.DeadAir, Show.AddMinutes(58), seconds: 600),
            Event(EventTypes.DeadAir, Show.AddMinutes(-1), seconds: 90),
            Event(EventTypes.Clipping, Show.AddMinutes(-2), seconds: 10),
        ];

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, [], [], complete: true, _clock.UtcNow);

        Assert.Equal(2, doc.Counts.DeadAirEvents);
        Assert.Equal(120 + 30, doc.Counts.DeadAirSeconds, 3);
        Assert.Equal(0, doc.Counts.ClippingEvents);
    }

    [Fact]
    public void Partial_coverage_reports_only_the_analysed_minutes()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), [], [], [new ShowSource("a", "a/job", Show.AddMinutes(-30), Show.AddMinutes(20))],
            complete: true, _clock.UtcNow);

        Assert.Equal(1200, doc.Counts.AnalysedSeconds, 3);
        Assert.Contains(doc.Notes, n => n.Contains("Only 20 of 60 minutes"));
    }

    const string SundaySchedule = """
        { "station": "burst", "timezone": "Europe/London", "fetchedUtc": "2026-10-15T12:00:00+00:00",
          "slots": [ { "day": "Sunday", "localStart": "00:00", "minutes": 180, "title": "Night Owls" },
                     { "day": "Sunday", "localStart": "20:00", "minutes": 60, "title": "Sunday Session" } ] }
        """;

    [Theory]
    [InlineData(2026, 10, 25, "2026-10-24T23:00:00+00:00", "2026-10-25T20:00:00+00:00")]
    [InlineData(2027, 3, 28, "2027-03-28T00:00:00+00:00", "2027-03-28T19:00:00+00:00")]
    public void Show_analysis_on_the_clock_change_days_uses_real_utc_durations(int year, int month, int day, string nightStart, string sessionStart)
    {
        var file = Path.Combine(_root, "sunday.json");
        File.WriteAllText(file, SundaySchedule);
        var provider = new ScheduleProvider(file, null, "burst", NullLogger.Instance);
        var from = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).AddHours(-2);
        var shows = provider.Occurrences(from, from.AddHours(30));
        var night = Assert.Single(shows, s => s.Title == "Night Owls");
        var session = Assert.Single(shows, s => s.Title == "Sunday Session");
        Assert.Equal(DateTimeOffset.Parse(nightStart), night.StartUtc);
        Assert.Equal(DateTimeOffset.Parse(sessionStart), session.StartUtc);

        var inRepeatedHour = Event(EventTypes.Filler, night.StartUtc.AddMinutes(100));
        var doc = ShowAnalysisBuilder.Build(night, TimeSpan.FromMinutes(5), [inRepeatedHour], [], [new ShowSource("a", "a/job", night.StartUtc, night.EndUtc)], true, night.EndUtc.AddHours(1));

        Assert.Equal(night.Duration.TotalSeconds, doc.Counts.AnalysedSeconds, 3);
        Assert.Equal(night.EndUtc, doc.EndUtc);
        Assert.Equal(1, doc.Counts.Fillers);
        Assert.DoesNotContain(doc.Notes, n => n.StartsWith("Only", StringComparison.Ordinal));
    }

    [Fact]
    public void A_and_b_events_for_the_same_time_count_once_and_b_fills_a_hole_through_the_processor()
    {
        var jobA = "a/2026/10-15/190000";
        var jobB = "b/2026/10-15/190000";
        var segA = Segment("a", Show, Show.AddMinutes(20), Event(EventTypes.Filler, Show.AddMinutes(10))) with { Scope = "segment:" + jobA };
        var segB = Segment("b", Show, Show.AddHours(1),
            Event(EventTypes.Filler, Show.AddMinutes(10).AddSeconds(0.3), "b"),
            Event(EventTypes.Filler, Show.AddMinutes(40), "b")) with { Scope = "segment:" + jobB };
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(jobA), AnalysisPaths.EventsFile), segA);
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(jobB), AnalysisPaths.EventsFile), segB);
        var processor = new ShowProcessor(Paths, new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance), ["a", "b"], TimeSpan.FromMinutes(5), _clock, NullLogger.Instance);

        var doc = Assert.Single(processor.Update(Show, Show.AddHours(3), []), d => d.Title == "Backseat Aux");

        Assert.Equal(2, doc.Counts.Fillers);
        Assert.Equal(3600, doc.Counts.AnalysedSeconds, 3);
        Assert.Equal(2, doc.Sources.Count);
    }

    [Fact]
    public void A_dropout_inside_one_recording_is_not_counted_as_analysed()
    {
        var job = "a/2026/10-15/190000";
        var segment = Segment("a", Show, Show.AddHours(1)) with
        {
            Scope = "segment:" + job,
            Coverage = [new SourceSpan("a", Show, Show.AddMinutes(20)), new SourceSpan("a", Show.AddMinutes(30), Show.AddHours(1))],
        };
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(job), AnalysisPaths.EventsFile), segment);
        var processor = new ShowProcessor(Paths, new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance), ["a", "b"], TimeSpan.FromMinutes(5), _clock, NullLogger.Instance);

        var doc = Assert.Single(processor.Update(Show, Show.AddHours(3), []), d => d.Title == "Backseat Aux");

        Assert.Equal(2, doc.Sources.Count);
        Assert.Equal(3000, doc.Counts.AnalysedSeconds, 3);
        Assert.Contains(doc.Notes, n => n.Contains("Only 50 of 60 minutes"));
    }

    [Fact]
    public void A_schedule_file_that_cannot_be_read_yet_is_retried_on_the_next_call()
    {
        var file = WriteSchedule();
        var provider = new ScheduleProvider(file, null, "burst", NullLogger.Instance);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Empty(provider.Occurrences(Show.AddHours(-1), Show.AddHours(3)));

        Assert.Equal(2, provider.Occurrences(Show.AddHours(-1), Show.AddHours(3)).Count);
    }

    [Fact]
    public void Dead_air_that_starts_before_the_window_and_runs_into_the_show_is_counted_by_the_processor()
    {
        var job = "a/2026/10-15/180000";
        var segment = Segment("a", Show.AddHours(-1), Show, Event(EventTypes.DeadAir, Show.AddMinutes(-2), seconds: 180)) with { Scope = "segment:" + job };
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(job), AnalysisPaths.EventsFile), segment);
        var margin = TimeSpan.FromMinutes(1);
        var processor = new ShowProcessor(Paths, new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance), ["a", "b"], margin, _clock, NullLogger.Instance);

        // The event starts 2 minutes before the show, so before the 1 minute margin window as well.
        var doc = Assert.Single(processor.Update(Show.AddMinutes(-10), Show.AddHours(3), []), d => d.Title == "Backseat Aux");

        Assert.Equal(1, doc.Counts.DeadAirEvents);
        Assert.Equal(60, doc.Counts.DeadAirSeconds, 3);
    }

    static AnalysisEvent Swear(DateTimeOffset at, string tier, string severityTier, bool before) => Event(EventTypes.Swear, at, tier: tier, before: before) with
    {
        Id = $"swear-{at.ToUnixTimeSeconds()}-{tier}-{severityTier}",
        Evidence = new Dictionary<string, string>
        {
            ["tier"] = tier, ["severityTier"] = severityTier, ["beforeWatershed"] = before ? "true" : "false", ["suppressed"] = "no",
        },
    };

    [Fact]
    public void Tiers_are_counted_by_the_severity_tier_so_a_masked_word_counts_at_its_lower_tier()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent[] events =
        [
            Swear(Show.AddMinutes(10), tier: "severe", severityTier: "strong", before: false),
            Swear(Show.AddMinutes(11), tier: "strong", severityTier: "mild", before: false),
            Swear(Show.AddMinutes(12), tier: "mild", severityTier: "mild", before: false),
        ];

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, [], [], complete: true, _clock.UtcNow);

        Assert.Equal(2, doc.Counts.SwearsMild);
        Assert.Equal(1, doc.Counts.SwearsStrong);
        Assert.Equal(0, doc.Counts.SwearsSevere);
    }

    [Fact]
    public void Each_tier_is_split_before_and_after_the_watershed_and_suppressed_runs_stay_out()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent Suppressed(DateTimeOffset at) => Swear(at, "severe", "severe", before: true) with
        {
            Id = "sup-" + at.ToUnixTimeSeconds(),
            Evidence = new Dictionary<string, string> { ["tier"] = "severe", ["severityTier"] = "severe", ["beforeWatershed"] = "true", ["suppressed"] = "repetition" },
        };
        AnalysisEvent[] events =
        [
            Swear(Show.AddMinutes(1), "mild", "mild", before: true),
            Swear(Show.AddMinutes(2), "mild", "mild", before: false),
            Swear(Show.AddMinutes(3), "mild", "mild", before: false),
            Swear(Show.AddMinutes(4), "strong", "strong", before: true),
            Swear(Show.AddMinutes(5), "strong", "strong", before: true),
            Swear(Show.AddMinutes(6), "severe", "severe", before: false),
            Swear(Show.AddMinutes(7), "strong", "mild", before: true),
            Suppressed(Show.AddMinutes(8)),
        ];

        var c = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, [], [], complete: true, _clock.UtcNow).Counts;

        Assert.Equal((2, 2, 2, 0, 0, 1), (c.SwearsMildBeforeWatershed, c.SwearsMildAfterWatershed, c.SwearsStrongBeforeWatershed, c.SwearsStrongAfterWatershed, c.SwearsSevereBeforeWatershed, c.SwearsSevereAfterWatershed));
        Assert.Equal(c.SwearsBeforeWatershed, c.SwearsMildBeforeWatershed + c.SwearsStrongBeforeWatershed + c.SwearsSevereBeforeWatershed);
        Assert.Equal(1, c.SuppressedSwearRuns);
    }

    [Fact]
    public void The_worst_clipping_peak_is_the_highest_peak_of_clipping_inside_the_show()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent Clip(DateTimeOffset at, string peak) => Event(EventTypes.Clipping, at, seconds: 0.1) with
        {
            Id = "clip-" + at.ToUnixTimeSeconds(),
            Evidence = new Dictionary<string, string> { ["peak"] = peak },
        };

        var two = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5),
            [Clip(Show.AddMinutes(5), "0.995"), Clip(Show.AddMinutes(9), "1.000"), Clip(Show.AddMinutes(-30), "1.000")], [], [], complete: true, _clock.UtcNow);
        Assert.Equal(2, two.Counts.ClippingEvents);
        Assert.Equal(1.0, two.Counts.WorstClippingPeak!.Value, 3);

        var one = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), [Clip(Show.AddMinutes(5), "0.995")], [], [], complete: true, _clock.UtcNow);
        Assert.Equal(0.995, one.Counts.WorstClippingPeak!.Value, 3);

        var none = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), [], [], [], complete: true, _clock.UtcNow);
        Assert.Null(none.Counts.WorstClippingPeak);
        Assert.Equal(ShowAnalysis.CurrentSchema, none.Schema);
        Assert.True(ShowAnalysis.CurrentSchema >= 2, "the new count fields are a schema change");
    }
}
