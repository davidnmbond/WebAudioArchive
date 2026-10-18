namespace WebAudioArchive.Analysis;

public sealed record HealthResult(int StatusCode, object Body);

/// <summary>
/// /healthz for the analysis service (spec 5, D16): 503 "lagging" when the lag passes the alarm and no segment job has finished within the stall window
/// (while jobs do finish the status is the 200 "catching-up", the whole first catch-up), or "stalled" when the worker shows no sign of life
/// for 15 minutes, so `docker ps` shows unhealthy. A pause (low disk, whisper not ready) is reported but is 200 until the
/// lag it causes reaches the alarm. Passes that keep throwing (last pass error set, no pass finished for 15 minutes) are 503 "failing". A job in progress counts as alive for up to <see cref="JobStallAfter"/> since the last
/// sign of life (a one hour segment on a CPU can take longer than 15 minutes). All times UTC.
/// </summary>
public static class AnalysisHealth
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long one job may run without finishing before the worker is called stalled.</summary>
    public static readonly TimeSpan JobStallAfter = TimeSpan.FromHours(3);

    public static HealthResult Build(MetricsSnapshot m, TimeSpan lagAlarm, DateTimeOffset now, TimeSpan? jobStallAfter = null, string? whisperUrl = null)
    {
        var lag = m.OldestPendingEndUtc is { } oldest && now > oldest ? now - oldest : TimeSpan.Zero;
        // Between jobs the last sign of life is the latest of the pass end and any activity; while a job runs it is when that job started.
        var lastSign = m.CurrentJob is not null && m.LastActivityUtc is { } started ? started : new[] { m.LastPassUtc, m.LastActivityUtc, m.StartedUtc }.Max()!.Value;
        var stalled = now - lastSign > (m.CurrentJob is null ? StallAfter : jobStallAfter ?? JobStallAfter);
        // Passes that keep failing (the folder cannot be listed, the disk check throws) never refresh LastPassUtc, whatever else shows activity.
        var failing = m.CurrentJob is null && m.LastPassError is not null && now - (m.LastPassUtc ?? m.StartedUtc) > StallAfter;
        var overAlarm = lag > lagAlarm;
        // Progress is a segment job finished within the stall window (inclusive). Over the alarm while jobs finish is the backlog being worked off
        // (the first catch-up runs newest first, so lag stays high for days); over the alarm with no progress is a real problem.
        var progressing = m.LastJobDoneUtc is { } done && now - done <= (jobStallAfter ?? JobStallAfter);
        var lagging = overAlarm && !progressing;
        var catchingUp = overAlarm && progressing && m.Paused is null;
        var status = stalled ? "stalled" : failing ? "failing" : lagging ? "lagging" : catchingUp ? "catching-up" : m.Paused is not null ? "paused" : "ok";
        var backlogHours = m.BacklogAudioSeconds / 3600;
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
            backlogAudioHours = Math.Round(backlogHours, 2),
            // Transcription seconds per audio second (the average realtime factor) times the audio still to do; unknown until a job has finished.
            catchUpEtaHours = m.AverageRealtimeFactor is { } rtf ? (double?)Math.Round(backlogHours * rtf, 2) : null,
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
            whisperUrl, // scheme, host and port only (see AnalysisSettings.WhisperUrlForHealth)
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
