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
    }

    async Task RunPassAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        metrics.SetDisk(disk.FreeBytes(), scratch.UsedBytes(), scratch.MaxBytes, disk.MinFreeBytes);
        if (disk.IsLow())
        {
            metrics.SetPaused(LowDisk());
            log.LogWarning("Analysis paused: {Reason}", LowDisk());
            metrics.PassCompleted(now);
            return;
        }

        var ready = await ReadyAsync(ct);
        var plan = Decide();
        metrics.SetPaused(!ready && plan.Any(p => p.Decision.Action == JobAction.Run) ? "whisper is not ready (starting, loading the model, or not running)" : null);

        var touched = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var (job, decision) in plan)
        {
            var parkable = decision.Action == JobAction.Skip && NeedsParking(job);
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

            if (outcome.Status == JobStatus.Done) touched.Add((job.Source.Meta.StartUtc, job.Source.Meta.EndUtc));
            if (outcome.Status == JobStatus.Waiting) ready = await ReadyAsync(ct);
        }

        metrics.SetCurrent(null, clock.UtcNow);
        var after = Decide();
        var pending = Pending(after);
        metrics.SetBacklog(pending.Count, pending.Count == 0 ? null : pending.Min(p => p.ToUtc),
            after.Count(p => p.Decision.Action == JobAction.Skip && p.Decision.Reason.StartsWith("failed", StringComparison.Ordinal)));

        if (!_caughtUp)
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

        foreach (var old in _shows.Values.Where(s => s.StartUtc < now - TimeSpan.FromDays(14)).Select(s => s.ShowId).ToList()) _shows.Remove(old);

        if (enricher is not null)
        {
            var segmentWorkReady = ready && after.Any(p => p.Decision.Action is JobAction.Run or JobAction.Redetect);
            if (!segmentWorkReady)
            {
                foreach (var show in _shows.Values.Where(enricher.IsDue).OrderByDescending(s => s.StartUtc).Take(MaxEnrichmentsPerPass).ToList())
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

    /// <summary>--rebuild: events.json of every finished segment from its transcript and features, then the day files and shows. No whisper, no audio decode.</summary>
    public int RebuildAll()
    {
        var jobs = WorkPlanner.Plan(discovery.Scan(), preference, since);
        var rebuilt = jobs.Count(j => Guarded(j, "rebuild", pipeline.RebuildEvents));
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

    bool NeedsParking(SegmentJob job)
    {
        try
        {
            return pipeline.NeedsParking(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
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
            log.LogWarning("Segment {Job} cannot be analysed: {Error}", job.Source.JobId, ex.Message);
            return new JobDecision(JobAction.Skip, $"failed: cannot be analysed ({ex.GetType().Name}: {ex.Message})");
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
