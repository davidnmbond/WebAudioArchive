using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>I3 with the position-model fix (#106): a Redetect re-times stored events from the current sidecar, and the detector code version changed so one is triggered.</summary>
public sealed class AnalysisRedetectStallTests : IDisposable
{
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config", "analysis");
    readonly string _root = Directory.CreateTempSubdirectory("waa-stall-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_redetect_after_a_position_model_fix_moves_an_event_after_a_stall_gap_by_the_shortfall_and_the_detector_version_changed()
    {
        // A 5 s gap 10 s in; the sidecar first says the audio fills the wall span exactly (35 s of audio).
        var gap = new SegmentGap(Show.AddSeconds(10), Show.AddSeconds(15), 5, "connection error");
        var a = Path.Combine(_root, "a");
        await AnalysisFixtures.WriteDecodableSegmentAsync(a, "burst", Show, Signals.Concat(Signals.SpeechLike(30, 4), Signals.Sine(5)), [gap]);
        var whisper = new FakeTranscriber
        {
            Respond = (_, _) => [AnalysisFixtures.Said(1, "Um, hello there."), AnalysisFixtures.Said(20, "Um, once more.")],
        };
        var out1 = Path.Combine(_root, "analysis");
        var paths = new AnalysisPaths(out1, "burst");
        var config1 = AnalysisConfigLoader.Load(Path.Combine(ConfigDir, "analysis.json"));

        AnalysisRunner Runner(LoadedConfig config)
        {
            var scratch = new WebAudioArchive.Analysis.Decode.ScratchSpace(Path.Combine(_root, "scratch"), 512L * 1024 * 1024);
            var disk = new WebAudioArchive.Analysis.Work.DiskGuard(out1, 1L << 30, _ => long.MaxValue);
            var schedule = new WebAudioArchive.Analysis.Shows.ScheduleProvider(null, null, "burst", NullLogger.Instance);
            var pipeline = new WebAudioArchive.Analysis.Work.SegmentPipeline(paths, config, scratch, new WebAudioArchive.Analysis.Decode.SegmentDecoder(), whisper, disk,
                WebAudioArchive.Analysis.Detectors.Watershed.From(config.Config.Swearing, schedule.Zone), schedule.TitleAt, _clock, NullLogger.Instance);
            return new AnalysisRunner(new WebAudioArchive.Analysis.Work.SegmentDiscovery([new RecorderRoot("a", a)], "burst", NullLogger.Instance), pipeline,
                new WebAudioArchive.Analysis.Shows.ShowProcessor(paths, schedule, ["a"], AnalysisComposition.ShowMargin, _clock, NullLogger.Instance), null, whisper, disk, scratch,
                new AnalysisMetrics(_clock.UtcNow), ["a"], null, _clock, NullLogger.Instance);
        }

        await Runner(config1).PassAsync(CancellationToken.None);
        var eventsFile = Path.Combine(paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.EventsFile);
        var old = AnalysisJson.Read<EventsDocument>(eventsFile)!.Events.Where(e => e.Type == EventTypes.Filler).OrderBy(e => e.StartUtc).ToList();
        Assert.Equal(2, old.Count);
        Assert.Equal(Show.AddSeconds(1), old[0].StartUtc);
        Assert.Equal(Show.AddSeconds(25), old[1].StartUtc); // audio 20 s: 10 s of the first connection, then 10 s after the gap that ends at 15 s

        // The position model now sees a 3 s stall: the audio is 3 s shorter than the wall span less the gap; the shortfall is taken from the end of the connection before the gap.
        var sidecar = Path.Combine(a, "burst", "master", "2026", "10-15", "190000.json");
        var meta = SegmentMetaStore.Read(sidecar)!;
        SegmentMetaStore.Write(sidecar, meta with { AudioSeconds = meta.AudioSeconds - 3 });

        // A detector code or config version change makes the planner choose Redetect, which must use the current sidecar.
        var changed = config1 with { DetectorVersion = config1.DetectorVersion + "|redetect" };
        Assert.NotEqual(config1.DetectorVersion, changed.DetectorVersion);
        await Runner(changed).PassAsync(CancellationToken.None);

        var moved = AnalysisJson.Read<EventsDocument>(eventsFile)!.Events.Where(e => e.Type == EventTypes.Filler).OrderBy(e => e.StartUtc).ToList();
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(old[0].StartUtc, moved[0].StartUtc);
        Assert.Equal(old[1].StartUtc.AddSeconds(3), moved[1].StartUtc);
        var expected = SegmentTimeline.ToUtc(SegmentTimeline.Anchors(SegmentMetaStore.Read(sidecar)!), 20);
        Assert.Equal(expected, moved[1].StartUtc);
    }

    [Fact]
    public void The_detector_code_version_was_bumped_so_stored_events_are_retimed()
    {
        Assert.NotEqual("1", AnalysisConfigLoader.DetectorCodeVersion);
        var loaded = AnalysisConfigLoader.Load(Path.Combine(ConfigDir, "analysis.json"));
        Assert.EndsWith("|code" + AnalysisConfigLoader.DetectorCodeVersion, loaded.DetectorVersion);
        Assert.DoesNotContain("|code1", loaded.DetectorVersion);
    }
}
