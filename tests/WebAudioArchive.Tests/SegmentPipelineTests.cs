using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Audio;
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
        // The process is killed during the second chunk: whatever was on disk at that moment is what a restart finds.
        byte[]? stateAtKill = null, partialAtKill = null;
        var killed = new FakeTranscriber
        {
            Respond = (_, call) =>
            {
                if (call != 1) return [AnalysisFixtures.Said(1, "one")];
                stateAtKill = File.ReadAllBytes(Path.Combine(SegmentDir, AnalysisPaths.StateFile));
                partialAtKill = File.ReadAllBytes(Path.Combine(SegmentDir, AnalysisPaths.PartialFile));
                throw new InvalidOperationException("simulated crash");
            },
        };

        await Pipeline(killed).ProcessAsync(Job, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(SegmentDir, AnalysisPaths.StateFile), stateAtKill!);
        File.WriteAllBytes(Path.Combine(SegmentDir, AnalysisPaths.PartialFile), partialAtKill!);
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
    public async Task A_failed_chunk_keeps_finished_chunks_and_the_next_attempt_resumes_after_them()
    {
        var failing = new FakeTranscriber
        {
            Respond = (_, call) => call == 1 ? throw new InvalidOperationException("odd") : [AnalysisFixtures.Said(1, "one")],
        };

        Assert.Equal(JobStatus.Failed, (await Pipeline(failing).ProcessAsync(Job, CancellationToken.None)).Status);
        Assert.Equal(new[] { 0 }, JobStore.Read(SegmentDir)!.ChunksDone);

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(JobStatus.Done, (await Pipeline().ProcessAsync(Job, CancellationToken.None)).Status);
        Assert.Equal(2, _whisper.Calls);
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

    SegmentJob[] AllJobs => [.. WorkPlanner.Plan(new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan(), ["a"], null)];

    [Fact]
    public async Task An_unknown_exception_is_a_counted_failure_with_backoff()
    {
        var odd = new FakeTranscriber { Respond = (_, _) => throw new InvalidOperationException("something odd") };

        var outcome = await Pipeline(odd).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(1, state.Attempts);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), state.NextAttemptUtc);
        Assert.Contains("something odd", state.LastError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }

    [Fact]
    public async Task An_access_denied_error_is_a_counted_failure_not_a_forever_retry()
    {
        var denied = new FakeTranscriber { Respond = (_, _) => throw new UnauthorizedAccessException("denied") };

        var outcome = await Pipeline(denied).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, outcome.Status);
        Assert.Equal(1, JobStore.Read(SegmentDir)!.Attempts);
        Assert.Equal(JobAction.Wait, (await Pipeline(denied).ProcessAsync(Job, CancellationToken.None)).Action);
        Assert.Equal(1, denied.Calls);
    }

    [Fact]
    public async Task A_transient_transcriber_error_waits_without_using_an_attempt()
    {
        var oom = new FakeTranscriber { Respond = (_, _) => throw new TransientTranscriptionException("CUDA out of memory") };

        var outcome = await Pipeline(oom).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Waiting, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(0, state.Attempts);
        Assert.Equal(_clock.UtcNow + JobRules.UnavailableRetry, state.NextAttemptUtc);
    }

    [Fact]
    public async Task An_attempt_is_counted_when_running_is_written_and_reset_when_done()
    {
        var job = Job;
        var pipeline = Pipeline();
        JobStore.Write(SegmentDir, new JobState(1, job.Source.JobId, job.Source.Fingerprint, pipeline.TranscriptVersion, _config.DetectorVersion,
            JobStatus.Running, 2, null, null, [], 0, T, 70, 0, 0));
        JobState? seen = null;
        _whisper.Respond = (_, _) =>
        {
            seen ??= JobStore.Read(SegmentDir);
            return [AnalysisFixtures.Said(1, "hello")];
        };

        var outcome = await pipeline.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Running, seen!.Status);
        Assert.Equal(3, seen.Attempts);
        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(0, JobStore.Read(SegmentDir)!.Attempts);
    }

    [Fact]
    public async Task A_job_killed_again_and_again_is_parked_as_failed_and_skipped()
    {
        byte[]? running = null;
        var dying = new FakeTranscriber
        {
            Respond = (_, _) =>
            {
                running = File.ReadAllBytes(Path.Combine(SegmentDir, AnalysisPaths.StateFile));
                throw new InvalidOperationException("killed");
            },
        };

        for (var i = 0; i < JobRules.MaxAttempts; i++)
        {
            await Pipeline(dying).ProcessAsync(Job, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(SegmentDir, AnalysisPaths.StateFile), running!);
        }

        Assert.Equal(JobStatus.Running, JobStore.Read(SegmentDir)!.Status);
        Assert.Equal(JobRules.MaxAttempts, JobStore.Read(SegmentDir)!.Attempts);
        var parked = await Pipeline(dying).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobAction.Skip, parked.Action);
        Assert.Equal(JobStatus.Failed, parked.Status);
        Assert.Equal(JobRules.MaxAttempts, dying.Calls);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(JobStatus.Failed, state.Status);
        Assert.Contains("interrupted", state.LastError);
        Assert.Equal(JobAction.Skip, (await Pipeline(dying).ProcessAsync(Job, CancellationToken.None)).Action);
        Assert.Equal(JobRules.MaxAttempts, dying.Calls);
    }

    [Fact]
    public async Task A_failed_segment_does_not_block_older_ones()
    {
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", T.AddHours(-1), Signals.SpeechLike(10, 3));
        var whisper = new FakeTranscriber { Respond = (r, _) => r.Wav.Length > 500_000 ? throw new InvalidOperationException("bad chunk") : [AnalysisFixtures.Said(1, "older")] };
        var jobs = AllJobs;
        Assert.Equal(["a/2026/10-16/190000", "a/2026/10-16/180000"], jobs.Select(j => j.Source.JobId));

        for (var pass = 0; pass < JobRules.MaxAttempts + 1; pass++)
        {
            _clock.Advance(TimeSpan.FromHours(7));
            foreach (var j in jobs) await Pipeline(whisper).ProcessAsync(j, CancellationToken.None);
        }

        var newest = JobStore.Read(SegmentDir)!;
        Assert.Equal(JobStatus.Failed, newest.Status);
        Assert.Equal(JobRules.MaxAttempts, newest.Attempts);
        Assert.Equal(JobStatus.Done, JobStore.Read(Paths.SegmentDir("a/2026/10-16/180000"))!.Status);
        Assert.Equal(JobRules.MaxAttempts + 1, whisper.Calls);
        Assert.Equal(JobAction.Skip, (await Pipeline(whisper).ProcessAsync(jobs[0], CancellationToken.None)).Action);
    }

    [Fact]
    public void Transcript_version_ignores_the_device_and_the_model_and_tracks_the_package_major_and_options()
    {
        var version = Pipeline().TranscriptVersion;

        _whisper.Engine = "fake (cuda)";
        Assert.Equal(version, Pipeline().TranscriptVersion);
        _whisper.Engine = "fake (cpu)";
        Assert.Equal(version, Pipeline().TranscriptVersion);

        var chunked = _config with { Config = _config.Config with { Transcription = _config.Config.Transcription with { ChunkSeconds = 60 } } };
        Assert.NotEqual(version, Pipeline(config: chunked).TranscriptVersion);
        var filtered = _config with { Config = _config.Config with { Transcription = _config.Config.Transcription with { MaxRepeats = 9 } } };
        Assert.Equal(version, Pipeline(config: filtered).TranscriptVersion);
        _whisper.Model = "other-model";
        _whisper.ModelBytes = 12345;
        Assert.Equal(version, Pipeline().TranscriptVersion);
        _whisper.Identity = "fake-next-major";
        Assert.NotEqual(version, Pipeline().TranscriptVersion);
    }

    [Fact]
    public async Task A_cpu_fallback_does_not_make_done_segments_stale_and_the_device_is_recorded()
    {
        _whisper.Engine = "fake (cuda)";
        await Pipeline().ProcessAsync(Job, CancellationToken.None);
        _whisper.Engine = "fake (cpu)";

        var again = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobAction.Skip, again.Action);
        Assert.Equal(3, _whisper.Calls);
        Assert.Equal("fake (cuda)", AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!.Engine);
    }

    async Task<DecodedSegment> ReferenceDecodeAsync()
    {
        var scratch = new ScratchSpace(Path.Combine(_root, "ref"), 512L * 1024 * 1024);
        using var lease = scratch.Acquire("ref", 100_000_000);
        return await new SegmentDecoder().DecodeAsync(_mp3, new FileInfo(_mp3).Length, 44100, 2, lease, CancellationToken.None);
    }

    [Fact]
    public async Task The_decoded_frame_count_equals_the_sidecar_and_silenced_frames_are_recorded()
    {
        await Pipeline().ProcessAsync(Job, CancellationToken.None);

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(_mp3, ".json"))!;
        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        var features = AnalysisJson.Read<AudioFeatures>(Path.Combine(SegmentDir, AnalysisPaths.FeaturesFile))!;
        Assert.Equal(AnalysisFixtures.CountFrames(_mp3), meta.Frames);
        Assert.Equal(meta.Frames, transcript.DecodedFrames);
        Assert.Equal((long)meta.Frames * 1152, features.Frames);
        var reference = await ReferenceDecodeAsync();
        Assert.Equal(reference.SilencedFrames, transcript.SilencedFrames);
        Assert.Equal(reference.RecoveredFrames, transcript.RecoveredFrames);
    }

    [Fact]
    public async Task A_sidecar_frame_count_that_the_audio_does_not_match_fails_and_parks()
    {
        var json = Path.ChangeExtension(_mp3, ".json");
        var meta = SegmentMetaStore.Read(json)!;
        SegmentMetaStore.Write(json, meta with { Frames = meta.Frames + 7 });

        for (var i = 0; i < JobRules.MaxAttempts; i++)
        {
            _clock.Advance(TimeSpan.FromHours(7));
            var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);
            Assert.Equal(JobStatus.Failed, outcome.Status);
        }

        var state = JobStore.Read(SegmentDir)!;
        Assert.Contains($"{meta.Frames + 7} frames", state.LastError);
        Assert.Contains($"{meta.Frames} frames", state.LastError);
        Assert.Equal(0, _whisper.Calls);
        Assert.Equal(JobAction.Skip, (await Pipeline().ProcessAsync(Job, CancellationToken.None)).Action);
        Assert.False(File.Exists(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile)));
    }

    [Fact]
    public async Task Too_many_silenced_frames_are_flagged_in_the_transcript_and_the_events()
    {
        var strict = _config with { Config = _config.Config with { Transcription = _config.Config.Transcription with { MaxSilencedFrameFraction = -1 } } };
        var lax = _config with { Config = _config.Config with { Transcription = _config.Config.Transcription with { MaxSilencedFrameFraction = 1 } } };
        // Start the file in the middle of the stream, as a recorder segment does: the first frames need bit reservoir data
        // that was in the previous file, so the decoder can only write them as silence.
        var all = await File.ReadAllBytesAsync(_mp3);
        var skip = 0;
        var lengths = new List<int>();
        new WebAudioArchive.Core.Mp3.Mp3FrameSplitter().Push(all, (ReadOnlySpan<byte> f, in WebAudioArchive.Core.Mp3.Mp3FrameHeader _) => lengths.Add(f.Length));
        for (var i = 0; i < 12; i++) skip += lengths[i];
        await File.WriteAllBytesAsync(_mp3, all[skip..]);
        var json = Path.ChangeExtension(_mp3, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { Bytes = all.Length - skip, Frames = AnalysisFixtures.CountFrames(_mp3) });
        var reference = await ReferenceDecodeAsync();
        Assert.True(reference.SilencedFrames > 0, "the synthetic segment must start with a silenced frame (its reservoir is in the previous file) for this test to mean anything");

        await Pipeline(config: strict).ProcessAsync(Job, CancellationToken.None);

        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        Assert.Contains("silenced-frames", transcript.Flags!);
        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!;
        Assert.Equal(1, events.Stats!["decodeSuspect"]);
        Assert.Equal(reference.SilencedFrames, events.Stats["silencedFrames"]);

        Directory.Delete(Out, recursive: true);
        await Pipeline(config: lax).ProcessAsync(Job, CancellationToken.None);
        Assert.Null(AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!.Flags);
        Assert.Equal(0, AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!.Stats!["decodeSuspect"]);
    }

    [Fact]
    public async Task Cancellation_stops_the_job_with_state_stopped_and_empty_scratch()
    {
        using var cts = new CancellationTokenSource();
        var whisper = new FakeTranscriber
        {
            Respond = (_, call) =>
            {
                if (call == 1)
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }

                return [AnalysisFixtures.Said(1, "one")];
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(whisper).ProcessAsync(Job, cts.Token));

        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(JobStatus.Waiting, state.Status);
        Assert.Equal("stopped", state.LastError);
        Assert.Equal(0, state.Attempts);
        Assert.Equal(new[] { 0 }, state.ChunksDone);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
        Assert.Equal(JobStatus.Done, (await Pipeline().ProcessAsync(Job, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Events_of_a_segment_with_a_start_burst_are_placed_before_the_wall_start()
    {
        // 20 s of audio but only 15 s of wall time: the first 5 s were broadcast before the recorder connected.
        var start = T.AddHours(1);
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", start, Signals.Concat(Signals.SpeechLike(2, 1), Signals.Sine(1), Signals.Silence(8), Signals.SpeechLike(9, 2)), burstSeconds: 5);
        _whisper.Respond = (_, _) => [AnalysisFixtures.Said(1, "Hello there")];
        var job = AllJobs.Single(j => j.Source.Meta.StartUtc == start);

        await Pipeline().ProcessAsync(job, CancellationToken.None);

        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(Paths.SegmentDir(job.Source.JobId), AnalysisPaths.EventsFile))!;
        var dead = Assert.Single(events.Events, e => e.Type == EventTypes.DeadAir);
        var audioStart = start - TimeSpan.FromSeconds(5);
        Assert.InRange((dead.StartUtc - audioStart).TotalSeconds, 2.9, 3.3);
        Assert.InRange((dead.EndUtc - audioStart).TotalSeconds, 10.8, 11.3);
    }

    [Fact]
    public async Task Events_after_a_gap_are_placed_after_the_gap()
    {
        // 20 s of audio over 30 s of wall time with a 10 s gap at 10 to 20 s: audio from 10 s on was broadcast from start + 20 s.
        var start = T.AddHours(1);
        var gap = new SegmentGap(start.AddSeconds(10), start.AddSeconds(20), 10, "stream ended by server");
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", start, Signals.Concat(Signals.SpeechLike(11, 1), Signals.Sine(1), Signals.Silence(6), Signals.SpeechLike(2, 2)), [gap]);
        _whisper.Respond = (_, _) => [AnalysisFixtures.Said(1, "Hello there")];
        var job = AllJobs.Single(j => j.Source.Meta.StartUtc == start);

        await Pipeline().ProcessAsync(job, CancellationToken.None);

        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(Paths.SegmentDir(job.Source.JobId), AnalysisPaths.EventsFile))!;
        var dead = Assert.Single(events.Events, e => e.Type == EventTypes.DeadAir);
        Assert.InRange((dead.StartUtc - start).TotalSeconds, 21.9, 22.3);
        Assert.InRange((dead.EndUtc - start).TotalSeconds, 27.8, 28.3);
    }
}
