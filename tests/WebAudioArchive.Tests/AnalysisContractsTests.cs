using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class AnalysisContractsTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static SegmentMeta Meta(double wallSeconds, double audioSeconds, params SegmentGap[] gaps) => new(
        1, "home-a", "burst", "080000.mp3", T, T.AddSeconds(wallSeconds), 1000, 100, audioSeconds,
        new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], gaps, "hour-boundary");

    [Fact]
    public void Timeline_without_gaps_puts_the_connect_burst_before_the_start()
    {
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3605));

        var only = Assert.Single(anchors);
        Assert.Equal(0, only.AudioOffset);
        Assert.Equal(T.AddSeconds(-5), only.Utc);
        Assert.Equal(T, SegmentTimeline.ToUtc(anchors, 5));
        Assert.Equal(105, SegmentTimeline.ToOffset(anchors, T.AddSeconds(100)), 6);
    }

    [Fact]
    public void Timeline_with_a_gap_jumps_over_it()
    {
        // 10 s gap at 1000 s; 4 s of connect burst shared by the two connections (2 s each).
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3594, new SegmentGap(T.AddSeconds(1000), T.AddSeconds(1010), 10, "stream ended by server")));

        Assert.Equal(2, anchors.Count);
        Assert.Equal(new TimelineAnchor(0, T.AddSeconds(-2)), anchors[0]);
        Assert.Equal(new TimelineAnchor(1002, T.AddSeconds(1008)), anchors[1]);
        Assert.Equal(T.AddSeconds(999), SegmentTimeline.ToUtc(anchors, 1001));
        Assert.Equal(T.AddSeconds(1009), SegmentTimeline.ToUtc(anchors, 1003));
        Assert.Equal(1002, SegmentTimeline.ToOffset(anchors, T.AddSeconds(1005)), 6);

        var pieces = SegmentTimeline.Pieces(anchors, 3594);
        Assert.Equal((T.AddSeconds(-2), T.AddSeconds(1000)), pieces[0]);
        Assert.Equal((T.AddSeconds(1008), T.AddSeconds(3600)), pieces[1]);
    }

    [Fact]
    public void Audio_a_stall_lost_before_a_gap_is_placed_at_the_gap_not_at_the_end_of_the_hour()
    {
        // Issue #106: 30 s gap at 1800 s, but 36 s less audio than wall time minus the gap, because the stream stalled
        // before the connection dropped. The first connection's audio ends 36 s before the gap; the hour still ends at 3600.
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3534, new SegmentGap(T.AddSeconds(1800), T.AddSeconds(1830), 30, "connection error")));

        Assert.Equal([new TimelineAnchor(0, T), new TimelineAnchor(1764, T.AddSeconds(1830))], anchors);
        Assert.Equal(T.AddSeconds(1763), SegmentTimeline.ToUtc(anchors, 1763));
        Assert.Equal(T.AddSeconds(1831), SegmentTimeline.ToUtc(anchors, 1765));
        Assert.Equal(T.AddSeconds(3600), SegmentTimeline.ToUtc(anchors, 3534));
        Assert.Equal(1764, SegmentTimeline.ToOffset(anchors, T.AddSeconds(1790)), 6); // inside the stall: where the audio jumps
    }

    [Fact]
    public void A_shortfall_is_shared_between_the_gaps_and_never_takes_more_than_a_connection_holds()
    {
        // 40 s short with two gaps: 20 s before each, except the first connection is only 10 s long, so it loses 10 s and
        // the second gap takes the other 30 s.
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3520,
            new SegmentGap(T.AddSeconds(10), T.AddSeconds(30), 20, "x"), new SegmentGap(T.AddSeconds(2000), T.AddSeconds(2020), 20, "y")));

        Assert.Equal([new TimelineAnchor(0, T), new TimelineAnchor(0, T.AddSeconds(30)), new TimelineAnchor(1940, T.AddSeconds(2020))], anchors);
        Assert.Equal(T.AddSeconds(3600), SegmentTimeline.ToUtc(anchors, 3520));
    }

    [Fact]
    public void Gaps_outside_the_span_are_ignored_and_short_audio_has_no_burst()
    {
        var anchors = SegmentTimeline.Anchors(Meta(600, 590, new SegmentGap(T.AddSeconds(-30), T.AddSeconds(-20), 10, "before")));
        Assert.Equal(new TimelineAnchor(0, T), Assert.Single(anchors));
    }

    [Fact]
    public void Json_is_written_atomically_in_utc_and_reads_back()
    {
        var path = Path.Combine(_dir, "x", AnalysisPaths.TranscriptFile);
        var doc = new TranscriptDocument(TranscriptDocument.CurrentSchema, "burst", "a", "home-a", "a/2026/10-16/080000", "080000.mp3",
            T, T.AddHours(1), 3600, [new TimelineAnchor(0, T)], "whisper.net 1.9.1 (cuda)", "ggml-large-v3-turbo-q8_0", "en", "tv", "NLayer 3.0.0 frame decoder, NAudio.Core 3.1.0 WDL sinc resampler",
            T.AddHours(1), 120,
            [new TranscriptSegment(1, 2, "Hello there.", 0.01, -0.2, null, [new TranscriptWord("Hello", 1, 1.4, 0.9), new TranscriptWord("there.", 1.5, 1.9, 0.8)])]);

        AnalysisJson.Write(path, doc);
        var text = File.ReadAllText(path);
        var back = AnalysisJson.Read<TranscriptDocument>(path)!;

        Assert.Contains("\"segmentStartUtc\": \"2026-10-16T08:00:00+00:00\"", text);
        Assert.DoesNotContain("suppressed", text);
        Assert.DoesNotContain("audioFromUtc", text);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal("there.", back.Segments[0].Words[1].Text);
        Assert.Equal(T.AddSeconds(1.5), back.ToUtc(1.5));
    }

    [Fact]
    public void Unreadable_json_is_reported_not_thrown()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(AnalysisJson.TryRead<TranscriptDocument>(path, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Event_ids_are_stable_and_links_open_the_show_at_the_event()
    {
        var a = AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.3456, "shit");
        Assert.Equal(a, AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.3456, "shit"));
        Assert.NotEqual(a, AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.5, "shit"));
        Assert.Equal(16, a.Length);

        var e = new AnalysisEvent(a, EventTypes.Swear, T.AddSeconds(10), T.AddSeconds(10.4), Severities.High, 0.9, "swear", "1",
            EventSources.Transcript, "a", "a/2026/10-16/080000", 15, 0.4, "shit", null);
        Assert.Equal("/at?utc=2026-10-16T08:00:10Z&recorder=a", e.Link);
    }

    [Fact]
    public void Paths_follow_the_documented_layout()
    {
        var p = new AnalysisPaths("/analysis", "burst");
        Assert.Equal(Path.Combine("/analysis", "burst", "segments", "a", "2026", "10-16", "080000"), p.SegmentDir("a/2026/10-16/080000"));
        Assert.Equal(Path.Combine("/analysis", "burst", "events", "2026", "10-16.json"), p.DayEvents(new DateOnly(2026, 10, 16)));
        Assert.Equal(Path.Combine("/analysis", "burst", "shows", "2026", "burst-20261016T1900Z"), p.ShowDir("burst-20261016T1900Z", T.AddHours(11)));
        Assert.Throws<ArgumentException>(() => p.SegmentDir("a/../../etc"));
        Assert.Throws<ArgumentException>(() => p.SegmentDir("a\\2026"));
    }

    [Fact]
    public void A_preferred_recorder_hides_the_other_where_it_has_audio()
    {
        string[] pref = ["a", "b"];
        SourceSpan[] spans = [new("a", T, T.AddHours(1)), new("b", T.AddMinutes(30), T.AddMinutes(90))];

        Assert.False(RecorderPreference.Keep("b", T.AddMinutes(45), spans, pref));
        Assert.True(RecorderPreference.Keep("b", T.AddMinutes(60), spans, pref));
        Assert.True(RecorderPreference.Keep("a", T.AddMinutes(45), spans, pref));
        Assert.Equal(2, RecorderPreference.Rank("c", pref));
    }

    [Fact]
    public void Slices_take_a_where_it_has_audio_b_elsewhere_and_skip_suppressed_lines()
    {
        TranscriptDocument Doc(string recorder, DateTimeOffset start, params (double At, string Text, string? Suppressed)[] lines) => new(
            1, "burst", recorder, "home-" + recorder, $"{recorder}/2026/10-16/{start:HHmmss}", $"{start:HHmmss}.mp3", start, start.AddHours(1), 3600,
            [new TimelineAnchor(0, start)], "fake", "fake", "en", "tv", "decoder", start, 1,
            lines.Select(l => new TranscriptSegment(l.At, l.At + 2, l.Text, 0, 0, l.Suppressed, [new TranscriptWord(l.Text, l.At, l.At + 1, 1)])).ToList());

        var a = Doc("a", T, (60, "from a", null), (120, "music noise", "repetition"), (3500, "late a", null));
        var b = Doc("b", T.AddMinutes(30), (60, "b inside a", null), (1900, "b after a", null));

        var lines = TranscriptSlicer.Slice([a, b], T, T.AddHours(2), ["a", "b"]);

        Assert.Equal(new[] { "from a", "late a", "b after a" }, lines.Select(l => l.Text));
        Assert.Equal(T.AddSeconds(60), lines[0].StartUtc);
        Assert.Equal("b", lines[2].Recorder);
        Assert.Empty(TranscriptSlicer.Slice([a], T.AddHours(3), T.AddHours(4), ["a"]));
    }
}
