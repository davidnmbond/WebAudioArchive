using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The whole segment job on a real (synthetic) MP3 with the managed decoder and a fake transcriber. 19:00 UTC on 16 October 2026 is 20:00 BST, before the watershed.</summary>
public sealed class SegmentPipelineTests : IAsyncLifetime
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    readonly string _root = Directory.CreateTempSubdirectory("waa-pipe-").FullName;
    readonly ManualClock _clock = new(T.AddHours(2));
    readonly FakeTranscriber _whisper = new();
    LoadedConfig _config = null!;
    string _mp3 = null!;
    long _free = long.MaxValue;

    string A => Path.Combine(_root, "a");
    string Out => Path.Combine(_root, "analysis");
    string Scratch => Path.Combine(_root, "scratch");
    AnalysisPaths Paths => new(Out, "burst");

    public async Task InitializeAsync()
    {
        var loaded = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json"));
        _config = loaded with
        {
            Config = loaded.Config with
            {
                Transcription = loaded.Config.Transcription with { ChunkSeconds = 30, BoundarySearchSeconds = 2 },
                Clipping = loaded.Config.Clipping with { Threshold = 0.98 },
            },
        };

        // 70 s: speech 0-9 and a tone to 10 (so the dead air starts exactly at 10), dead air 10-17, a hard-clipped tone
        // 17-20, speech 20-70. Three chunks: about 0-30, 30-60, 60-70.
        var clipped = Signals.Sine(3, amp: 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
        _mp3 = await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", T,
            Signals.Concat(Signals.SpeechLike(9, 1), Signals.Sine(1), Signals.Silence(7), clipped, Signals.SpeechLike(50, 2)));
        _whisper.Respond = (_, call) => call == 0 ? [AnalysisFixtures.Said(1.0, "Um, welcome to Burst, that was shit.")] : [AnalysisFixtures.Said(1.0, "Hello again.")];
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    SegmentPipeline Pipeline(ITranscriber? transcriber = null, LoadedConfig? config = null) => new(
        Paths, config ?? _config, new ScratchSpace(Scratch, 512L * 1024 * 1024), new SegmentDecoder(),
        transcriber ?? _whisper, new DiskGuard(Out, 1L << 30, _ => _free), Watershed.From(new SwearOptions(), London),
        _ => "Backseat Aux", _clock, NullLogger.Instance);

    SegmentJob Job => new(Assert.Single(new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan()), false);

    string SegmentDir => Paths.SegmentDir("a/2026/10-16/190000");

    [Fact]
    public async Task A_segment_becomes_a_transcript_and_events_and_scratch_is_cleaned()
    {
        var archived = await File.ReadAllBytesAsync(_mp3);

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(3, _whisper.Calls);
        Assert.Contains("Backseat Aux", _whisper.Requests[0].Prompt);
        Assert.NotNull(outcome.RealtimeFactor);

        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal(3, transcript.Segments.Count);
        Assert.Equal(1.0, transcript.Segments[0].Start, 3);
        Assert.InRange(transcript.Segments[1].Start, 29, 33);
        Assert.Equal(T.AddSeconds(1.0), transcript.ToUtc(transcript.Segments[0].Start));
        Assert.StartsWith("NLayer 3.0.0", transcript.Decoder);

        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!;
        var swear = Assert.Single(events.Events, e => e.Type == EventTypes.Swear);
        Assert.Equal(Severities.High, swear.Severity);
        Assert.Equal("true", swear.Evidence!["beforeWatershed"]);
        Assert.Equal("no", swear.Evidence["suppressed"]);
        Assert.Equal(T.AddMilliseconds(2800), swear.StartUtc);
        Assert.Equal(EventSources.Transcript, swear.Source);
        Assert.Single(events.Events, e => e.Type == EventTypes.Filler);
        var dead = Assert.Single(events.Events, e => e.Type == EventTypes.DeadAir);
        Assert.InRange((dead.StartUtc - T).TotalSeconds, 9.8, 10.3);
        Assert.InRange((dead.EndUtc - T).TotalSeconds, 16.8, 17.2);
        Assert.Contains(events.Events, e => e.Type == EventTypes.Clipping && (e.StartUtc - T).TotalSeconds is > 16.9 and < 20.1);
        Assert.Equal(1, events.Stats!["swearsBeforeWatershed"]);
        Assert.Equal(_config.DetectorVersion, events.Versions["detectors"]);

        Assert.Equal(archived, await File.ReadAllBytesAsync(_mp3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
        Assert.False(File.Exists(Path.Combine(SegmentDir, AnalysisPaths.PartialFile)));
        Assert.Equal(JobStatus.Done, JobStore.Read(SegmentDir)!.Status);
    }

    [Fact]
    public async Task A_suppressed_swear_run_is_one_flagged_low_confidence_event_and_does_not_inflate_counts()
    {
        var whisper = new FakeTranscriber
        {
            Respond = (_, call) => call == 0
                ? [AnalysisFixtures.Said(1, "shit shit shit"), AnalysisFixtures.Said(3, "shit shit shit"), AnalysisFixtures.Said(5, "shit shit shit")]
                : [AnalysisFixtures.Said(1, "Hello again.")],
        };

        await Pipeline(whisper).ProcessAsync(Job, CancellationToken.None);

        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!;
        var swear = Assert.Single(events.Events, e => e.Type == EventTypes.Swear);
        Assert.Equal("repetition", swear.Evidence!["suppressed"]);
        Assert.Equal("9", swear.Evidence["runLength"]);
        Assert.Contains("suppressed: repetition", swear.Label);
        Assert.InRange(swear.Confidence, 0, 0.3);
        Assert.Equal(EventSources.Transcript, swear.Source);
        Assert.Equal(0, events.Stats!["swears"]);
        Assert.Equal(0, events.Stats["swearsBeforeWatershed"]);
        Assert.Equal(1, events.Stats["suppressedSwearRuns"]);
    }

    [Fact]
    public async Task Running_again_is_a_no_op()
    {
        await Pipeline().ProcessAsync(Job, CancellationToken.None);
        var again = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobAction.Skip, again.Action);
        Assert.Equal(3, _whisper.Calls);
    }

    [Fact]
    public async Task Resume_after_a_crash_skips_finished_chunks()
    {
        var crashing = new FakeTranscriber
        {
            Respond = (_, call) => call == 1 ? throw new InvalidOperationException("simulated crash") : [AnalysisFixtures.Said(1, "one")],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(crashing).ProcessAsync(Job, CancellationToken.None));
        var interrupted = JobStore.Read(SegmentDir)!;
        Assert.Equal(JobStatus.Running, interrupted.Status);
        Assert.Equal(new[] { 0 }, interrupted.ChunksDone);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(2, _whisper.Calls);
        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal("one", transcript.Segments[0].Text);
    }

    [Fact]
    public async Task Whisper_down_waits_without_using_an_attempt()
    {
        var down = new FakeTranscriber { Respond = (_, _) => throw new TranscriberUnavailableException("refused") };

        var outcome = await Pipeline(down).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Waiting, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(0, state.Attempts);
        Assert.Equal(_clock.UtcNow + JobRules.UnavailableRetry, state.NextAttemptUtc);
        Assert.Equal(JobAction.Wait, (await Pipeline(down).ProcessAsync(Job, CancellationToken.None)).Action);
        Assert.Equal(1, down.Calls);

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(JobStatus.Done, (await Pipeline().ProcessAsync(Job, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Low_disk_stops_between_chunks_and_keeps_progress()
    {
        var calls = 0;
        var whisper = new FakeTranscriber
        {
            Respond = (_, _) =>
            {
                if (++calls == 1) _free = 1;
                return [AnalysisFixtures.Said(1, "one")];
            },
        };

        var outcome = await Pipeline(whisper).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal("paused-disk", outcome.Status);
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(new[] { 0 }, JobStore.Read(SegmentDir)!.ChunksDone);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));

        Assert.Equal("paused-disk", (await Pipeline(whisper).ProcessAsync(Job, CancellationToken.None)).Status);
        Assert.Equal(1, whisper.Calls);
    }

    [Fact]
    public async Task A_detector_change_rebuilds_events_without_whisper_or_audio()
    {
        var job = Job;
        await Pipeline().ProcessAsync(job, CancellationToken.None);
        var changed = _config with { DetectorVersion = "changed" };
        File.Move(_mp3, _mp3 + ".away");

        var outcome = await Pipeline(config: changed).ProcessAsync(job, CancellationToken.None);

        File.Move(_mp3 + ".away", _mp3);
        Assert.Equal(JobAction.Redetect, outcome.Action);
        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(3, _whisper.Calls);
        Assert.Equal("changed", AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!.Versions["detectors"]);
        Assert.Equal("changed", JobStore.Read(SegmentDir)!.DetectorVersion);
    }

    [Fact]
    public async Task A_rewritten_segment_is_analysed_again()
    {
        await Pipeline().ProcessAsync(Job, CancellationToken.None);
        var json = Path.ChangeExtension(_mp3, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { EndedBy = "crash" });

        await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(6, _whisper.Calls);
    }

    [Fact]
    public async Task Undecodable_audio_fails_with_backoff()
    {
        await File.WriteAllTextAsync(_mp3, string.Concat(Enumerable.Repeat("not audio ", 1000)));
        var json = Path.ChangeExtension(_mp3, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { Bytes = new FileInfo(_mp3).Length });

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(1, state.Attempts);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), state.NextAttemptUtc);
        Assert.Contains("no MPEG audio frames", state.LastError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }
}
