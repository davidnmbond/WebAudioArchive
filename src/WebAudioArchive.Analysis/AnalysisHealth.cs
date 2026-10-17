namespace WebAudioArchive.Analysis;

public sealed record HealthResult(int StatusCode, object Body);

/// <summary>
/// /healthz for the analysis service (spec 5, D16): 503 when the lag passes the alarm or the worker shows no sign of life
/// for 15 minutes, so `docker ps` shows unhealthy. A pause (low disk, whisper not ready) is reported but is 200 until the
/// lag it causes reaches the alarm. Passes that keep throwing (last pass error set, no pass finished for 15 minutes) are 503 "failing". A job in progress counts as alive for up to <see cref="JobStallAfter"/> since the last
/// sign of life (a one hour segment on a CPU can take longer than 15 minutes). All times UTC.
/// </summary>
public static class AnalysisHealth
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long one job may run without finishing before the worker is called stalled.</summary>
    public static readonly TimeSpan JobStallAfter = TimeSpan.FromHours(3);

    public static HealthResult Build(MetricsSnapshot m, TimeSpan lagAlarm, DateTimeOffset now, TimeSpan? jobStallAfter = null)
    {
        var lag = m.OldestPendingEndUtc is { } oldest && now > oldest ? now - oldest : TimeSpan.Zero;
        // Between jobs the last sign of life is the latest of the pass end and any activity; while a job runs it is when that job started.
        var lastSign = m.CurrentJob is not null && m.LastActivityUtc is { } started ? started : new[] { m.LastPassUtc, m.LastActivityUtc, m.StartedUtc }.Max()!.Value;
        var stalled = now - lastSign > (m.CurrentJob is null ? StallAfter : jobStallAfter ?? JobStallAfter);
        // Passes that keep failing (the folder cannot be listed, the disk check throws) never refresh LastPassUtc, whatever else shows activity.
        var failing = m.CurrentJob is null && m.LastPassError is not null && now - (m.LastPassUtc ?? m.StartedUtc) > StallAfter;
        var lagging = lag > lagAlarm;
        var status = stalled ? "stalled" : failing ? "failing" : lagging ? "lagging" : m.Paused is not null ? "paused" : "ok";
        var classes = (m.Classes ?? new Dictionary<string, SegmentClassStatus>())
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(c => c.Key, c => (object)new
            {
                done = c.Value.Done,
                failed = c.Value.Failed,
                lastSuccessUtc = c.Value.LastSuccessUtc,
                lastErrorUtc = c.Value.LastErrorUtc,
                lastError = c.Value.LastError,
                lastErrorJob = c.Value.LastErrorJob,
            });
        var body = new
        {
            status,
            lagSeconds = Math.Round(lag.TotalSeconds),
            lagAlarmSeconds = Math.Round(lagAlarm.TotalSeconds),
            queueSegments = m.Backlog,
            backlogSegments = m.Backlog,
            failedSegments = m.FailedSegments,
            oldestPendingEndUtc = m.OldestPendingEndUtc,
            currentJob = m.CurrentJob,
            paused = m.Paused,
            device = m.Device,
            realtimeFactor = new { last = m.LastRealtimeFactor, average = m.AverageRealtimeFactor },
            audioHoursAnalysedLast24h = Math.Round(m.AudioHoursLast24h, 2),
            jobsDone = m.JobsDone,
            jobsFailed = m.JobsFailed,
            whisperReady = m.WhisperReady,
            enrichment = new { pending = m.EnrichmentPending, done = m.EnrichmentDone, failed = m.EnrichmentFailed },
            freeDiskBytes = m.FreeBytes,
            minFreeDiskBytes = m.MinFreeBytes,
            scratchBytes = m.ScratchBytes,
            scratchMaxBytes = m.ScratchMaxBytes,
            lastSuccessUtc = m.LastSuccessUtc,
            lastErrorBySegmentClass = classes,
            lastPassError = m.LastPassError,
            lastPassUtc = m.LastPassUtc,
            uptimeSeconds = Math.Round((now - m.StartedUtc).TotalSeconds),
        };
        return new HealthResult(stalled || failing || lagging ? 503 : 200, body);
    }
}
