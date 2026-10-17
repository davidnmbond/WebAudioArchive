using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis;

/// <summary>
/// One pass of the single work lane (D17): stop at once if the disk is low; ask the transcriber whether it is ready
/// (before anything is planned); plan the jobs (A first, newest first); run segment jobs (only redetects when whisper is
/// not ready); rebuild the day timelines and shows they touched (and the last 7 days once after start-up); then, only if no
/// segment job is ready, enrich up to 2 finished shows. An exception in one job, one show or one enrichment is recorded
/// against its class in the metrics and the lane carries on with the next. A graceful stop (cancellation) ends the pass
/// quietly: the job in progress is left waiting without using an attempt. The analysis container must be given a stop grace
/// period of at least 60 s so that a stop abandons the chunk in hand cleanly before Docker kills the process.
/// </summary>
public sealed class AnalysisRunner(
    SegmentDiscovery discovery,
    SegmentPipeline pipeline,
    ShowProcessor shows,
    Enricher? enricher,
    ITranscriber transcriber,
    DiskGuard disk,
    ScratchSpace scratch,
    AnalysisMetrics metrics,
    IReadOnlyList<string> preference,
    DateTimeOffset? since,
    IClock clock,
    ILogger log)
{
    public const int MaxEnrichmentsPerPass = 2;
    public static readonly TimeSpan CatchUp = TimeSpan.FromDays(7);
    readonly Dictionary<string, ShowAnalysis> _shows = new(StringComparer.Ordinal);
    readonly HashSet<string> _reportedBad = new(StringComparer.Ordinal);
    bool _caughtUp;

    public async Task PassAsync(CancellationToken ct)
    {
        try
        {
            await RunPassAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.SetCurrent(null, clock.UtcNow);
            log.LogInformation("Analysis stopped: the work in hand is left to resume");
        }
        catch (Exception ex)
        {
            // A whole pass failed (a folder cannot be listed, the disk check threw): recorded, shown in /healthz (503 once no pass has finished for 15 minutes), retried next time.
            log.LogError(ex, "Analysis pass failed; trying again next time");
            metrics.ClearCurrent();
            metrics.PassFailed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task RunPassAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        metrics.SetDisk(disk.FreeBytes(), scratch.UsedBytes(), scratch.MaxBytes, disk.MinFreeBytes);
        var ready = await ReadyAsync(ct);
        var plan = Decide();
        if (disk.IsLow())
        {
            // Nothing is analysed, but the backlog and its lag are still measured (they only read files), so a long low-disk pause raises the alarm.
            PublishBacklog(plan);
            metrics.SetPaused(LowDisk());
            log.LogWarning("Analysis paused: {Reason}", LowDisk());
            metrics.PassCompleted(now);
            return;
        }

        metrics.SetPaused(!ready && plan.Any(p => p.Decision.Action == JobAction.Run) ? "whisper is not ready (starting, loading the model, or not running)" : null);

        var touched = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var (job, decision) in plan)
        {
            var parkable = decision.Parkable;
            if (decision.Action is JobAction.Skip && !parkable) continue;
            if (decision.Action == JobAction.Wait) continue;
            if (decision.Action == JobAction.Run && !ready) continue;
            ct.ThrowIfCancellationRequested();
            if (!parkable && disk.IsLow())
            {
                metrics.SetPaused(LowDisk());
                break;
            }

            metrics.SetCurrent(job.Source.JobId, clock.UtcNow);
            JobOutcome outcome;
            try
            {
                outcome = await pipeline.ProcessAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analysis of {Job} threw; carrying on with the next segment", job.Source.JobId);
                outcome = new JobOutcome(job.Source.JobId, decision.Action, JobStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0, 0);
            }

            metrics.JobFinished(outcome, clock.UtcNow, job.Source.Recorder);
            if (outcome.Status == JobOutcome.PausedDisk)
            {
                metrics.SetPaused(LowDisk());
                break;
            }

            // A job that finished or failed for good changes which shows are complete (a failed job no longer counts as pending).
            if (outcome.Status is JobStatus.Done or JobStatus.Failed) touched.Add((job.Source.Meta.StartUtc, job.Source.Meta.EndUtc));
            if (outcome.Status == JobStatus.Waiting) ready = await ReadyAsync(ct);
        }

        metrics.SetCurrent(null, clock.UtcNow);
        var after = Decide();
        var pending = PublishBacklog(after);

        // A show left incomplete is re-checked every pass once nothing overlapping it is pending any more.
        foreach (var open in _shows.Values.Where(s => !s.Complete && !pending.Any(p => p.FromUtc < s.WindowToUtc && p.ToUtc > s.WindowFromUtc)))
            touched.Add((open.WindowFromUtc, open.WindowToUtc));

        // The 7 day catch-up waits for whisper: with the model missing every finished segment looks pending, and shows would be rewritten as incomplete.
        if (!_caughtUp && ready)
        {
            touched.Add((now - CatchUp, now));
            _caughtUp = true;
        }

        foreach (var (from, to) in MergeRanges(touched))
        {
            try
            {
                foreach (var doc in shows.Update(from, to, pending))
                    _shows[doc.ShowId] = doc;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Updating the day timelines and shows from {From:u} to {To:u} failed", from, to);
                metrics.RecordFailure(AnalysisMetrics.ShowClass, null, $"{ex.GetType().Name}: {ex.Message}", clock.UtcNow);
            }
        }

        foreach (var old in _shows.Values.Where(s => s.StartUtc < now - TimeSpan.FromDays(14)).Select(s => s.ShowId).ToList())
        {
            _shows.Remove(old);
            enricher?.Forget(old);
        }

        if (enricher is not null)
        {
            var segmentWorkReady = ready && after.Any(p => p.Decision.Action is JobAction.Run or JobAction.Redetect);
            if (!segmentWorkReady)
            {
                var due = _shows.Values.Where(enricher.IsDue).OrderByDescending(s => s.StartUtc).Take(MaxEnrichmentsPerPass).ToList();
                // The GPU is shared with Ollama: drop whisper's model first so the LLM can have the memory (the next readiness check, a
                // minute later, loads it again, and Ollama unloads its own model straight after each request, keep_alive 0).
                if (due.Count > 0) transcriber.ResetModel();
                foreach (var show in due)
                {
                    ct.ThrowIfCancellationRequested();
                    metrics.SetCurrent("enrich:" + show.ShowId, clock.UtcNow);
                    try
                    {
                        await enricher.EnrichAsync(show, ct);
                        metrics.RecordSuccess(AnalysisMetrics.EnrichmentClass, clock.UtcNow);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Enrichment of {Show} threw; carrying on", show.ShowId);
                        metrics.RecordFailure(AnalysisMetrics.EnrichmentClass, show.ShowId, $"{ex.GetType().Name}: {ex.Message}", clock.UtcNow);
                    }
                }

                metrics.SetCurrent(null, clock.UtcNow);
            }

            var statuses = _shows.Values.Select(enricher.StatusOf).ToList();
            metrics.SetEnrichment(_shows.Values.Count(enricher.IsDue), statuses.Count(s => s == EnrichmentStatus.Done), statuses.Count(s => s == EnrichmentStatus.Failed));
        }

        metrics.PassCompleted(clock.UtcNow);
    }

    /// <summary>How many segments the last <see cref="RebuildAll"/> marked for re-transcription (only with retranscribe on).</summary>
    public int MarkedForRetranscription { get; private set; }

    /// <summary>--rebuild: events.json of every finished segment from its transcript and features, then the day files and shows. No whisper, no audio decode.</summary>
    public int RebuildAll(bool retranscribe = false)
    {
        var jobs = WorkPlanner.Plan(discovery.Scan(), preference, since);
        // --retranscribe: segments made by another model are marked for the service to transcribe again (nothing is rebuilt for those).
        var marked = retranscribe ? jobs.Where(j => Guarded(j, "mark for re-transcription", pipeline.MarkForRetranscription)).ToList() : [];
        MarkedForRetranscription = marked.Count;
        var rebuilt = jobs.Except(marked).Count(j => Guarded(j, "rebuild", pipeline.RebuildEvents));
        if (jobs.Count > 0)
        {
            var pending = Pending(Decide());
            foreach (var doc in shows.Update(jobs.Min(j => j.Source.Meta.StartUtc), jobs.Max(j => j.Source.Meta.EndUtc), pending))
                _shows[doc.ShowId] = doc;
        }

        log.LogInformation("Rebuilt events for {Count} segments", rebuilt);
        return rebuilt;
    }

    /// <summary>--unpark: every failed or parked segment goes back in the queue with its attempts reset. Returns how many.</summary>
    public int UnparkAll()
    {
        var n = WorkPlanner.Plan(discovery.Scan(), preference, since).Count(j => Guarded(j, "un-park", pipeline.Unpark));
        log.LogInformation("Un-parked {Count} segments", n);
        return n;
    }

    bool Guarded(SegmentJob job, string what, Func<SegmentJob, bool> act)
    {
        try
        {
            return act(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not {What} {Job}: {Error}", what, job.Source.JobId, ex.Message);
            return false;
        }
    }

    async Task<bool> ReadyAsync(CancellationToken ct)
    {
        bool ready;
        try
        {
            ready = await transcriber.IsReadyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Whisper readiness check failed: {Error}", ex.Message);
            ready = false;
        }

        metrics.SetWhisper(ready, transcriber.Device);
        return ready;
    }

    List<SourceSpan> PublishBacklog(List<(SegmentJob Job, JobDecision Decision)> decided)
    {
        var pending = Pending(decided);
        metrics.SetBacklog(pending.Count, pending.Count == 0 ? null : pending.Min(p => p.ToUtc),
            decided.Count(p => p.Decision.Action == JobAction.Skip && p.Decision.Reason.StartsWith("failed", StringComparison.Ordinal)),
            decided.Where(p => p.Decision.Action is JobAction.Run or JobAction.Wait).Sum(p => p.Job.Source.Meta.AudioSeconds));
        return pending;
    }

    /// <summary>A segment whose decision cannot be worked out (for example a file name that is not a safe job id) is skipped as failed, so it cannot stop the rest of the plan.</summary>
    List<(SegmentJob Job, JobDecision Decision)> Decide() =>
        WorkPlanner.Plan(discovery.Scan(), preference, since).Select(j => (j, SafeDecide(j))).ToList();

    JobDecision SafeDecide(SegmentJob job)
    {
        try
        {
            return pipeline.Decide(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = $"cannot be analysed ({ex.GetType().Name}: {ex.Message})";
            if (_reportedBad.Add(job.Source.JobId))
            {
                log.LogWarning("Segment {Job} {Reason}", job.Source.JobId, reason);
                metrics.RecordFailure(job.Source.Recorder, job.Source.JobId, reason, clock.UtcNow);
            }

            return new JobDecision(JobAction.Skip, "failed: " + reason);
        }
    }

    static List<SourceSpan> Pending(List<(SegmentJob Job, JobDecision Decision)> decided) => decided
        .Where(p => p.Decision.Action is not JobAction.Skip)
        .Select(p => new SourceSpan(p.Job.Source.Recorder, p.Job.Source.Meta.StartUtc, p.Job.Source.Meta.EndUtc))
        .ToList();

    string LowDisk() => $"free disk below {disk.MinFreeBytes / 1073741824.0:0.#} GB: analysis stopped so the recorders keep the space";

    static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> MergeRanges(List<(DateTimeOffset From, DateTimeOffset To)> ranges)
    {
        (DateTimeOffset From, DateTimeOffset To)? run = null;
        foreach (var r in ranges.OrderBy(r => r.From))
        {
            if (run is { } current && r.From <= current.To)
            {
                run = (current.From, r.To > current.To ? r.To : current.To);
                continue;
            }

            if (run is { } done) yield return done;
            run = r;
        }

        if (run is { } last) yield return last;
    }
}
