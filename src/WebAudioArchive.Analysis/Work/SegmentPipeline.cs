using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Work;

public sealed class DiskLowException(string message) : Exception(message);

/// <summary>What one call did. Status is a JobStatus value, or "paused-disk" when the disk guard stopped it.</summary>
public sealed record JobOutcome(string JobId, JobAction Action, string Status, string Reason, double AudioSeconds, double TranscribeSeconds, double WallSeconds)
{
    public const string PausedDisk = "paused-disk";

    /// <summary>Transcription seconds per audio second (0.05 is 20 times faster than real time), for finished runs.</summary>
    public double? RealtimeFactor => Action == JobAction.Run && Status == JobStatus.Done && AudioSeconds > 0 ? TranscribeSeconds / AudioSeconds : null;
}

/// <summary>
/// One segment, idempotent and resumable: decide from state.json; copy and decode into scratch; save features; send
/// each chunk not yet done to the transcriber, saving progress after each; assemble transcript.json; run the
/// detectors into events.json; mark done. Scratch is released whatever happens. The archive is only read.
/// </summary>
public sealed class SegmentPipeline(
    AnalysisPaths paths,
    LoadedConfig config,
    ScratchSpace scratch,
    SegmentDecoder decoder,
    ITranscriber transcriber,
    DiskGuard disk,
    Watershed watershed,
    Func<DateTimeOffset, string?> showTitleAt,
    IClock clock,
    ILogger log)
{
    /// <summary>What decides whether a stored transcript is current: the engine package version, the model and its file size, and the transcript options. The device (cuda or cpu) is recorded in the transcript but is not part of this.</summary>
    public string TranscriptVersion => $"{transcriber.Identity}|{config.Config.Transcription.TranscriptKey()}";

    public JobDecision Decide(SegmentJob job) =>
        JobRules.Decide(JobStore.Read(paths.SegmentDir(job.Source.JobId)), job.Source.Fingerprint, TranscriptVersion, config.DetectorVersion, clock.UtcNow);

    /// <summary>Rebuilds a finished job's events.json (and its transcript's suppression flags) from the stored transcript and features: no whisper, no audio.</summary>
    public bool RebuildEvents(SegmentJob job)
    {
        var dir = paths.SegmentDir(job.Source.JobId);
        var state = JobStore.Read(dir);
        if (state is null || state.Status != JobStatus.Done) return false;
        return Redetect(job.Source, dir, state, new JobDecision(JobAction.Redetect, "rebuild")).Status == JobStatus.Done;
    }

    public async Task<JobOutcome> ProcessAsync(SegmentJob job, CancellationToken ct)
    {
        var s = job.Source;
        var dir = paths.SegmentDir(s.JobId);
        var state = JobStore.Read(dir);
        var decision = JobRules.Decide(state, s.Fingerprint, TranscriptVersion, config.DetectorVersion, clock.UtcNow);
        if (decision.Action == JobAction.Skip && state is not null && state.Status is not (JobStatus.Done or JobStatus.Failed) && state.Attempts >= JobRules.MaxAttempts)
            return Park(s, dir, state, decision.Reason);
        if (decision.Action is JobAction.Skip or JobAction.Wait)
            return new JobOutcome(s.JobId, decision.Action, state?.Status ?? "none", decision.Reason, 0, 0, 0);
        if (decision.Action == JobAction.Redetect)
            return Redetect(s, dir, state!, decision);
        if (disk.IsLow())
            return new JobOutcome(s.JobId, decision.Action, JobOutcome.PausedDisk, "free disk is below the threshold", 0, 0, 0);

        var wall = Stopwatch.StartNew();
        var resumable = state is not null && state.Fingerprint == s.Fingerprint && state.TranscriptVersion == TranscriptVersion;
        var partial = resumable ? LoadPartial(dir, s.Fingerprint) : null;
        var previousAttempts = resumable ? state!.Attempts : 0;
        var attempts = previousAttempts + 1; // counted now, so a process that is killed again and again reaches MaxAttempts
        var current = new JobState(JobState.CurrentSchema, s.JobId, s.Fingerprint, TranscriptVersion, config.DetectorVersion, JobStatus.Running,
            attempts, null, null, partial?.Chunks.Select(c => c.Index).Order().ToList() ?? [], partial?.ChunkCount ?? 0, clock.UtcNow,
            s.Meta.AudioSeconds, partial?.TranscribeSeconds ?? 0, 0);
        JobStore.Write(dir, current);
        log.LogInformation("Analysing {Job} ({Reason})", s.JobId, decision.Reason);

        try
        {
            using var lease = scratch.Acquire(s.JobId, SegmentDecoder.ScratchBytesFor(s.Meta.Bytes, s.Meta.AudioSeconds));
            var decoded = await decoder.DecodeAsync(s.AudioPath, s.Meta.Bytes, s.SampleRate, s.Channels, lease, ct);
            if (decoded.Frames != s.Meta.Frames)
                throw new DecodeException($"decoded {decoded.Frames} frames but the sidecar says {s.Meta.Frames} frames: the audio and its sidecar disagree");
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.FeaturesFile), decoded.Features);

            var anchors = SegmentTimeline.Anchors(s.Meta);
            var o = config.Config.Transcription;
            var chunks = ChunkPlanner.Plan(decoded.Features, o.ChunkSeconds, o.BoundarySearchSeconds);
            if (partial is not null && partial.ChunkCount != chunks.Count) partial = null;
            partial ??= new TranscriptPartial(TranscriptPartial.CurrentSchema, s.Fingerprint, TranscriptVersion, chunks.Count, 0, []);
            var wav = WavFile.ReadInfo(decoded.SpeechWavPath);

            foreach (var chunk in chunks)
            {
                if (partial.Chunks.Any(c => c.Index == chunk.Index)) continue;
                ct.ThrowIfCancellationRequested();
                if (disk.IsLow()) throw new DiskLowException("free disk fell below the threshold");
                var title = showTitleAt(SegmentTimeline.ToUtc(anchors, (chunk.Start + chunk.End) / 2));
                var audio = WavFile.Slice(decoded.SpeechWavPath, wav, chunk.Start, chunk.End);
                var started = Stopwatch.GetTimestamp();
                var raw = await transcriber.TranscribeAsync(new TranscriptionRequest(audio, PromptBuilder.Build(o, title), o.Language), ct);
                var took = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var shifted = raw.Select(r => r.Shift(chunk.Start)).Where(r => r.Start < chunk.End + 1).ToList();
                partial = partial with
                {
                    TranscribeSeconds = partial.TranscribeSeconds + took,
                    Chunks = [.. partial.Chunks, new PartialChunk(chunk.Index, chunk.Start, chunk.End, shifted)],
                };
                AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.PartialFile), partial);
                current = current with
                {
                    ChunksDone = partial.Chunks.Select(c => c.Index).Order().ToList(),
                    ChunkCount = chunks.Count,
                    TranscribeSeconds = partial.TranscribeSeconds,
                    UpdatedUtc = clock.UtcNow,
                };
                JobStore.Write(dir, current);
            }

            var transcript = TranscriptAssembler.Build(s, paths.Station, anchors, partial.Chunks.OrderBy(c => c.Index).SelectMany(c => c.Segments),
                o, transcriber.Engine, transcriber.Model, TranscriptVersion, decoded.Decoder, clock.UtcNow, partial.TranscribeSeconds,
                new DecodeStats(decoded.Frames, decoded.SilencedFrames, decoded.RecoveredFrames));
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), transcript);
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), DetectorRunner.Run(transcript, decoded.Features, config, watershed, clock.UtcNow));

            current = current with { Status = JobStatus.Done, Attempts = 0, LastError = null, NextAttemptUtc = null, WallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 3), UpdatedUtc = clock.UtcNow };
            JobStore.Write(dir, current);
            File.Delete(Path.Combine(dir, AnalysisPaths.PartialFile)); // after Done is written, so a crash between the two leaves a finished job and a stray partial, never the reverse
            return new JobOutcome(s.JobId, JobAction.Run, JobStatus.Done, decision.Reason, s.Meta.AudioSeconds, partial.TranscribeSeconds, current.WallSeconds);
        }
        catch (Exception ex) when (ex is TranscriberUnavailableException or TransientTranscriptionException)
        {
            return Pause(s, dir, current with { Attempts = previousAttempts }, JobStatus.Waiting, ex.Message, clock.UtcNow + JobRules.UnavailableRetry);
        }
        catch (ScratchFullException ex)
        {
            return Pause(s, dir, current with { Attempts = previousAttempts }, JobStatus.Waiting, ex.Message, clock.UtcNow + JobRules.UnavailableRetry);
        }
        catch (DiskLowException ex)
        {
            Pause(s, dir, current with { Attempts = previousAttempts }, JobStatus.Waiting, ex.Message, null);
            return new JobOutcome(s.JobId, JobAction.Run, JobOutcome.PausedDisk, ex.Message, 0, 0, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            JobStore.Write(dir, current with { Status = JobStatus.Waiting, Attempts = previousAttempts, LastError = "stopped", UpdatedUtc = clock.UtcNow });
            throw;
        }
        catch (Exception ex)
        {
            var failed = current with
            {
                Status = JobStatus.Failed,

                LastError = ex is DecodeException or TranscriptionFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}",
                NextAttemptUtc = JobRules.NextAttempt(current.Attempts, clock.UtcNow),
                UpdatedUtc = clock.UtcNow,
            };
            JobStore.Write(dir, failed);
            log.LogWarning("Analysis of {Job} failed (attempt {Attempt} of {Max}): {Error}", s.JobId, failed.Attempts, JobRules.MaxAttempts, failed.LastError);
            return new JobOutcome(s.JobId, JobAction.Run, JobStatus.Failed, failed.LastError!, 0, 0, 0);
        }
    }

    /// <summary>A job left "running" or "waiting" that has used all its attempts (killed again and again, or retried to the limit): marked failed with the reason, so it is skipped and cannot block older segments.</summary>
    JobOutcome Park(SourceSegment s, string dir, JobState state, string reason)
    {
        JobStore.Write(dir, state with { Status = JobStatus.Failed, LastError = reason, NextAttemptUtc = null, UpdatedUtc = clock.UtcNow });
        log.LogWarning("Analysis of {Job} parked after {Attempts} attempts: {Reason}", s.JobId, state.Attempts, reason);
        return new JobOutcome(s.JobId, JobAction.Skip, JobStatus.Failed, reason, 0, 0, 0);
    }

    JobOutcome Pause(SourceSegment s, string dir, JobState current, string status, string reason, DateTimeOffset? next)
    {
        JobStore.Write(dir, current with { Status = status, LastError = reason, NextAttemptUtc = next, UpdatedUtc = clock.UtcNow });
        log.LogInformation("Analysis of {Job} paused: {Reason}", s.JobId, reason);
        return new JobOutcome(s.JobId, JobAction.Run, status, reason, 0, 0, 0);
    }

    JobOutcome Redetect(SourceSegment s, string dir, JobState state, JobDecision decision)
    {
        var transcript = AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(dir, AnalysisPaths.TranscriptFile), out _);
        var features = AnalysisJson.TryRead<AudioFeatures>(Path.Combine(dir, AnalysisPaths.FeaturesFile), out _);
        if (transcript is null || features is null)
        {
            // Results are missing or damaged: forget the state so the next pass runs the job in full.
            File.Delete(Path.Combine(dir, AnalysisPaths.StateFile));
            return new JobOutcome(s.JobId, JobAction.Redetect, "none", "results missing, the job runs again", 0, 0, 0);
        }

        var refiltered = TranscriptAssembler.Refilter(transcript, config.Config.Transcription);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), refiltered);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), DetectorRunner.Run(refiltered, features, config, watershed, clock.UtcNow));
        JobStore.Write(dir, state with { DetectorVersion = config.DetectorVersion, UpdatedUtc = clock.UtcNow });
        return new JobOutcome(s.JobId, JobAction.Redetect, JobStatus.Done, decision.Reason, 0, 0, 0);
    }

    static TranscriptPartial? LoadPartial(string dir, string fingerprint)
    {
        var p = AnalysisJson.TryRead<TranscriptPartial>(Path.Combine(dir, AnalysisPaths.PartialFile), out _);
        return p is not null && p.Fingerprint == fingerprint ? p : null;
    }
}
