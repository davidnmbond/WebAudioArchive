using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

// Final fix wave: sharing the GPU with Ollama (I1), no transcript reads when nothing changed (I4), a model switch never
// re-transcribes by itself (I5), backlog audio and catch-up figures (I6).
public sealed partial class AnalysisServiceTests
{
    // ---- I1: the GPU is shared with Ollama ----

    [Fact]
    public async Task The_runner_frees_the_whisper_model_before_the_enrichment_asks_ollama_for_the_gpu()
    {
        var events = new List<string>();
        _whisper.Events = events;
        _llm.Respond = (_, _) =>
        {
            events.Add("chat");
            return "{}";
        };
        var (runner, _) = Runner();

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(1, _whisper.Resets);
        var firstChat = events.IndexOf("chat");
        Assert.True(firstChat >= 0, "the enrichment should have asked the model: " + string.Join(",", events));
        Assert.True(events.IndexOf("reset") < firstChat, "reset must come before the first chat: " + string.Join(",", events));
        Assert.True(events.LastIndexOf("transcribe") < events.IndexOf("reset"), "no transcription between the reset and the enrichment");
    }

    [Fact]
    public async Task A_pass_with_no_enrichment_due_does_not_reset_the_model()
    {
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        var resets = _whisper.Resets;

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(resets, _whisper.Resets);
    }

    [Fact]
    public async Task A_transcriber_that_cannot_get_gpu_memory_never_burns_an_attempt_or_parks_the_segment()
    {
        _whisper.Respond = (_, _) => throw new TransientTranscriptionException("whisper ran out of memory or hit a GPU error: failed to allocate");
        var (runner, metrics) = Runner();
        var dir = Paths.SegmentDir("a/2026/10-15/190000");

        for (var i = 0; i < JobRules.MaxAttempts + 2; i++)
        {
            _clock.Advance(JobRules.UnavailableRetry + TimeSpan.FromSeconds(1));
            await runner.PassAsync(CancellationToken.None);
        }

        var state = JobStore.Read(dir)!;
        Assert.Equal(JobStatus.Waiting, state.Status);
        Assert.Equal(0, state.Attempts);
        Assert.Equal(0, metrics.Snapshot().FailedSegments);
        Assert.True(_whisper.Calls >= JobRules.MaxAttempts + 2);
    }

    // ---- I4: nothing changed means no transcript is read ----

    [Fact]
    public async Task A_pass_with_nothing_changed_reads_no_transcript_for_the_enrichment_checks()
    {
        var reads = 0;
        var (runner, _) = Runner(readTranscript: p =>
        {
            reads++;
            return AnalysisJson.TryRead<TranscriptDocument>(p, out _);
        });
        await runner.PassAsync(CancellationToken.None);
        Assert.True(reads > 0, "the first pass has to read the transcript");

        reads = 0;
        await runner.PassAsync(CancellationToken.None);
        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(0, reads);

        // A changed transcript file (here: a longer file with the same content) is noticed and read again.
        File.AppendAllText(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.TranscriptFile), "\n");
        await runner.PassAsync(CancellationToken.None);
        Assert.True(reads > 0);
        Assert.Single(_llm.Requests);
    }

    // ---- I5: a model switch never re-transcribes the archive by itself ----

    [Fact]
    public async Task Switching_the_model_keeps_done_jobs_done()
    {
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);
        Assert.Equal(1, _whisper.Calls);

        _whisper.Model = "ggml-base.en";
        _whisper.ModelBytes = 147_000_000;
        _whisper.Device = "cpu";
        var (second, metrics) = Runner();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);

        Assert.Equal(1, _whisper.Calls);
        Assert.Equal(JobStatus.Done, JobStore.Read(Paths.SegmentDir("a/2026/10-15/190000"))!.Status);
        Assert.Equal(0, metrics.Snapshot().Backlog);
    }

    [Fact]
    public async Task A_done_job_made_by_the_in_process_engine_is_skipped_by_the_whisper_server_engine()
    {
        _whisper.Identity = "whisper.net 1";
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);
        Assert.Equal(1, _whisper.Calls);
        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        Assert.StartsWith("whisper.net 1|", JobStore.Read(dir)!.TranscriptVersion);

        _whisper.Identity = "whisper.cpp";
        _whisper.Engine = "whisper.cpp server at pdl-rune-02.panoramicdata.com:8080";
        _whisper.Device = "remote";
        var (second, metrics) = Runner();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);

        Assert.Equal(1, _whisper.Calls);
        Assert.Equal(JobStatus.Done, JobStore.Read(dir)!.Status);
        Assert.Equal(0, metrics.Snapshot().Backlog);
    }

    [Fact]
    public async Task Retranscribe_runs_a_segment_made_with_another_model_once_and_records_the_model_size_and_device()
    {
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);
        _whisper.Model = "ggml-base.en";
        _whisper.ModelBytes = 147_000_000;
        _whisper.Device = "cpu";

        var (second, _) = Runner(retranscribe: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);
        Assert.Equal(2, _whisper.Calls);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);
        Assert.Equal(2, _whisper.Calls);

        var dir = Paths.SegmentDir("a/2026/10-15/190000");
        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(dir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal("ggml-base.en", transcript.Model);
        Assert.Equal(147_000_000, transcript.ModelBytes);
        Assert.Equal("cpu", transcript.Device);
        Assert.Equal("ggml-base.en|147000000", JobStore.Read(dir)!.ModelIdentity);
    }

    [Fact]
    public async Task Retranscribe_does_nothing_when_the_model_is_the_one_that_made_the_transcript()
    {
        var (first, _) = Runner(retranscribe: true);
        await first.PassAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await first.PassAsync(CancellationToken.None);
        Assert.Equal(1, _whisper.Calls);
    }

    [Fact]
    public async Task A_transcript_option_change_or_a_package_major_change_re_runs_without_the_retranscribe_switch()
    {
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);

        var copy = Path.Combine(_root, "config-copy");
        CopyDirectory(Path.GetDirectoryName(ConfigPath)!, copy);
        var copied = Path.Combine(copy, "analysis.json");
        File.WriteAllText(copied, File.ReadAllText(copied).Replace("\"chunkSeconds\": 300", "\"chunkSeconds\": 200"));
        var (second, _) = Runner(configPath: copied);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await second.PassAsync(CancellationToken.None);
        Assert.Equal(2, _whisper.Calls);

        _whisper.Identity = "fake-next-major";
        var (third, _) = Runner(configPath: copied);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await third.PassAsync(CancellationToken.None);
        Assert.Equal(3, _whisper.Calls);
    }

    [Fact]
    public async Task Rebuild_with_retranscribe_marks_segments_made_with_another_model_for_the_service_to_run_again()
    {
        var (first, _) = Runner();
        await first.PassAsync(CancellationToken.None);
        _whisper.Model = "ggml-base.en";
        _whisper.ModelBytes = 147_000_000;

        var (plain, _) = Runner();
        Assert.Equal(1, plain.RebuildAll());
        Assert.Equal(0, plain.MarkedForRetranscription);
        Assert.Equal(JobStatus.Done, JobStore.Read(Paths.SegmentDir("a/2026/10-15/190000"))!.Status);

        var (marking, _) = Runner();
        marking.RebuildAll(retranscribe: true);
        Assert.Equal(1, marking.MarkedForRetranscription);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var (service, _) = Runner();
        await service.PassAsync(CancellationToken.None);
        Assert.Equal(2, _whisper.Calls);
        Assert.Equal("ggml-base.en|147000000", JobStore.Read(Paths.SegmentDir("a/2026/10-15/190000"))!.ModelIdentity);
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---- I6: backlog audio and the progress mark ----

    [Fact]
    public async Task The_backlog_is_measured_in_audio_seconds_for_the_health_report()
    {
        _whisper.Ready = false;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(35, metrics.Snapshot().BacklogAudioSeconds, 3);
    }

    [Fact]
    public async Task A_finished_segment_job_is_progress_and_an_enrichment_is_not()
    {
        var (runner, metrics) = Runner();
        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(_clock.UtcNow, m.LastJobDoneUtc);
        Assert.Equal(1, m.EnrichmentDone);

        var fresh = new AnalysisMetrics(_clock.UtcNow);
        fresh.RecordSuccess(AnalysisMetrics.EnrichmentClass, _clock.UtcNow);
        Assert.Null(fresh.Snapshot().LastJobDoneUtc);
    }
}

public sealed class JobRulesModelTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    static JobState Done(string? modelIdentity) => new(JobState.CurrentSchema, "a/2026/10-15/190000", "fp", "v1", "d1", JobStatus.Done, 0, null, null, [], 0, Now, 35, 1, 1)
    {
        ModelIdentity = modelIdentity,
    };

    [Fact]
    public void A_done_job_stays_done_when_only_the_model_differs_unless_retranscribe_is_on()
    {
        var state = Done("m1|1000");

        Assert.Equal(JobAction.Skip, JobRules.Decide(state, "fp", "v1", "d1", Now, "m2|2000", retranscribe: false).Action);
        Assert.Equal(JobAction.Run, JobRules.Decide(state, "fp", "v1", "d1", Now, "m2|2000", retranscribe: true).Action);
        Assert.Equal(JobAction.Skip, JobRules.Decide(state, "fp", "v1", "d1", Now, "m1|1000", retranscribe: true).Action);
        Assert.Equal(JobAction.Run, JobRules.Decide(Done(null), "fp", "v1", "d1", Now, "m1|1000", retranscribe: true).Action);
        Assert.Equal(JobAction.Run, JobRules.Decide(state, "fp", "v2", "d1", Now, "m1|1000", retranscribe: false).Action);
    }
}

public sealed class WhisperProcessorTests
{
    sealed class FakeProcessor : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    static async IAsyncEnumerable<RawSegment> Yield(params RawSegment[] segments)
    {
        foreach (var s in segments) yield return s;
        await Task.CompletedTask;
    }

    static async IAsyncEnumerable<RawSegment> Throw(Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task Any_failure_to_build_the_processor_is_transient_and_resets_the_model()
    {
        var resets = 0;

        var ex = await Assert.ThrowsAsync<TransientTranscriptionException>(() => WhisperNetTranscriber.RunAsync<FakeProcessor>(
            () => throw new InvalidOperationException("could not create the whisper state"), _ => Yield(), () => resets++, CancellationToken.None));

        Assert.Equal(1, resets);
        Assert.Contains("could not create the whisper state", ex.Message);
    }

    [Fact]
    public async Task A_memory_error_while_processing_is_transient_resets_the_model_and_disposes_the_processor()
    {
        var resets = 0;
        var processor = new FakeProcessor();

        await Assert.ThrowsAsync<TransientTranscriptionException>(() => WhisperNetTranscriber.RunAsync(
            () => processor, _ => Throw(new InvalidOperationException("CUDA error: out of memory")), () => resets++, CancellationToken.None));

        Assert.Equal(1, resets);
        Assert.True(processor.Disposed);
    }

    [Fact]
    public async Task A_bad_input_fails_the_chunk_without_resetting_the_model()
    {
        var resets = 0;

        await Assert.ThrowsAsync<TranscriptionFailedException>(() => WhisperNetTranscriber.RunAsync(
            () => new FakeProcessor(), _ => Throw(new InvalidOperationException("bad audio")), () => resets++, CancellationToken.None));

        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task Segments_are_returned_in_order_and_cancellation_passes_through()
    {
        var a = AnalysisFixtures.Said(0, "one");
        var b = AnalysisFixtures.Said(2, "two");
        var all = await WhisperNetTranscriber.RunAsync(() => new FakeProcessor(), _ => Yield(a, b), () => { }, CancellationToken.None);
        Assert.Equal([a, b], all);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WhisperNetTranscriber.RunAsync(
            () => new FakeProcessor(), _ => Throw(new OperationCanceledException(cts.Token)), () => { }, cts.Token));
    }

    [Fact]
    public void The_job_identity_is_the_package_major_only_and_the_model_identity_names_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "waa-id-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[1234]);
        try
        {
            using var t = new WhisperNetTranscriber(path, WhisperDevice.Cpu, 1, dtw: false);
            Assert.Matches(@"^whisper\.net \d+$", t.Identity);
            Assert.Equal(1234, t.ModelBytes);
            Assert.Equal(Path.GetFileNameWithoutExtension(path) + "|1234", t.ModelIdentity);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
