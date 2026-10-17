using WebAudioArchive.Analysis.Work;

namespace WebAudioArchive.Analysis;

/// <summary>
/// Figures for one segment class (the recorder the segment came from: "a" is the preferred copy, "b" fills its holes), or
/// "enrichment" / "show" for the other work the lane does. LastError is the most recent failure, kept after later successes.
/// </summary>
public sealed record SegmentClassStatus(int Done, int Failed, DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastErrorUtc, string? LastError, string? LastErrorJob);

/// <summary>
/// What /healthz reports. OldestPendingEndUtc is the end of the oldest closed segment not yet analysed: lag is now
/// minus that. Backlog is the queue length. LastActivityUtc is the last sign of life inside a long pass (a job started or
/// finished); CurrentJobStartedUtc is when CurrentJob began.
/// </summary>
public sealed record MetricsSnapshot(
    DateTimeOffset StartedUtc,
    DateTimeOffset? LastPassUtc,
    int Backlog,
    int FailedSegments,
    DateTimeOffset? OldestPendingEndUtc,
    int JobsDone,
    int JobsFailed,
    double? LastRealtimeFactor,
    double? AverageRealtimeFactor,
    double AudioHoursLast24h,
    string? CurrentJob,
    string? Paused,
    bool? WhisperReady,
    int EnrichmentPending,
    int EnrichmentDone,
    int EnrichmentFailed,
    long FreeBytes,
    long ScratchBytes,
    string? Device = null,
    IReadOnlyDictionary<string, SegmentClassStatus>? Classes = null,
    DateTimeOffset? LastSuccessUtc = null,
    DateTimeOffset? LastActivityUtc = null,
    long ScratchMaxBytes = 0,
    long MinFreeBytes = 0,
    string? LastPassError = null);

public sealed class AnalysisMetrics(DateTimeOffset startedUtc)
{
    public const string EnrichmentClass = "enrichment";
    public const string ShowClass = "show";

    readonly object _gate = new();
    readonly Queue<(DateTimeOffset At, double Seconds)> _done = new();
    MetricsSnapshot _s = new(startedUtc, null, 0, 0, null, 0, 0, null, null, 0, null, null, null, 0, 0, 0, 0, 0);

    public MetricsSnapshot Snapshot()
    {
        lock (_gate) return _s;
    }

    public void SetDisk(long free, long scratch, long scratchMax, long minFree) =>
        Set(s => s with { FreeBytes = free, ScratchBytes = scratch, ScratchMaxBytes = scratchMax, MinFreeBytes = minFree });

    public void SetWhisper(bool ready, string? device) => Set(s => s with { WhisperReady = ready, Device = device });

    public void SetPaused(string? reason) => Set(s => s with { Paused = reason });

    /// <summary>The job in progress (null when idle). Starting one counts as activity, so a long backlog pass is not mistaken for a stall.</summary>
    public void SetCurrent(string? job, DateTimeOffset now) =>
        Set(s => s with { CurrentJob = job, LastActivityUtc = now });

    public void SetBacklog(int backlog, DateTimeOffset? oldestPendingEnd, int failed) =>
        Set(s => s with { Backlog = backlog, OldestPendingEndUtc = oldestPendingEnd, FailedSegments = failed });

    public void SetEnrichment(int pending, int done, int failed) =>
        Set(s => s with { EnrichmentPending = pending, EnrichmentDone = done, EnrichmentFailed = failed });

    public void PassCompleted(DateTimeOffset now) => Set(s => s with { LastPassUtc = now, LastActivityUtc = now, LastPassError = null });

    /// <summary>A whole pass failed (for example the folder could not be listed): it is retried, and healthz says why.</summary>
    public void PassFailed(string error, DateTimeOffset now) => Set(s => s with { LastPassError = error, LastActivityUtc = now });

    /// <summary>A failure outside a segment job (enrichment, a show update): kept under its own class beside the segment classes.</summary>
    public void RecordFailure(string segmentClass, string? job, string error, DateTimeOffset now) =>
        Set(s => With(s, segmentClass, c => c with { Failed = c.Failed + 1, LastErrorUtc = now, LastError = error, LastErrorJob = job }));

    public void RecordSuccess(string segmentClass, DateTimeOffset now) =>
        Set(s => With(s, segmentClass, c => c with { Done = c.Done + 1, LastSuccessUtc = now }) with { LastSuccessUtc = now });

    /// <summary>
    /// Counts the job under its class; for a full run, updates the realtime factor (last and a moving average) and the audio
    /// hours analysed in the last 24 hours. A failed job records its reason as the class's last error.
    /// </summary>
    public void JobFinished(JobOutcome o, DateTimeOffset now, string segmentClass)
    {
        lock (_gate)
        {
            _s = _s with { LastActivityUtc = now };
            if (o.Status == JobStatus.Failed)
            {
                _s = _s with { JobsFailed = _s.JobsFailed + 1 };
                _s = With(_s, segmentClass, c => c with { Failed = c.Failed + 1, LastErrorUtc = now, LastError = o.Reason, LastErrorJob = o.JobId });
            }

            if (o.Status != JobStatus.Done) return;
            _s = _s with { JobsDone = _s.JobsDone + 1, LastSuccessUtc = now };
            _s = With(_s, segmentClass, c => c with { Done = c.Done + 1, LastSuccessUtc = now });
            if (o.RealtimeFactor is not { } rtf) return;
            _done.Enqueue((now, o.AudioSeconds));
            while (_done.Count > 0 && _done.Peek().At < now.AddHours(-24)) _done.Dequeue();
            _s = _s with
            {
                LastRealtimeFactor = Math.Round(rtf, 4),
                AverageRealtimeFactor = Math.Round(_s.AverageRealtimeFactor is { } avg ? 0.8 * avg + 0.2 * rtf : rtf, 4),
                AudioHoursLast24h = _done.Sum(d => d.Seconds) / 3600,
            };
        }
    }

    static MetricsSnapshot With(MetricsSnapshot s, string segmentClass, Func<SegmentClassStatus, SegmentClassStatus> change)
    {
        var classes = s.Classes is null ? new Dictionary<string, SegmentClassStatus>() : new Dictionary<string, SegmentClassStatus>(s.Classes);
        classes[segmentClass] = change(classes.GetValueOrDefault(segmentClass) ?? new SegmentClassStatus(0, 0, null, null, null, null));
        return s with { Classes = classes };
    }

    void Set(Func<MetricsSnapshot, MetricsSnapshot> change)
    {
        lock (_gate) _s = change(_s);
    }
}
