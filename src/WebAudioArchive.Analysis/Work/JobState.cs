using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Work;

public static class JobStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// state.json beside a segment's results: what it was made from (fingerprint and versions), how far it got (chunks
/// done) and, when it failed or is waiting, why and when to try again. A crash leaves "running", which resumes.
/// </summary>
public sealed record JobState(
    int Schema,
    string JobId,
    string Fingerprint,
    string TranscriptVersion,
    string DetectorVersion,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    IReadOnlyList<int> ChunksDone,
    int ChunkCount,
    DateTimeOffset UpdatedUtc,
    double AudioSeconds,
    double TranscribeSeconds,
    double WallSeconds)
{
    public const int CurrentSchema = 1;
}

public enum JobAction
{
    Skip,
    Wait,
    Run,
    Redetect,
}

public sealed record JobDecision(JobAction Action, string Reason);

public static class JobRules
{
    public const int MaxAttempts = 5;

    /// <summary>How long to wait when whisper is unreachable; not counted as an attempt.</summary>
    public static readonly TimeSpan UnavailableRetry = TimeSpan.FromMinutes(5);

    public static JobDecision Decide(JobState? state, string fingerprint, string transcriptVersion, string detectorVersion, DateTimeOffset now)
    {
        if (state is null) return new(JobAction.Run, "new");
        if (state.Fingerprint != fingerprint) return new(JobAction.Run, "the segment changed");
        if (state.TranscriptVersion != transcriptVersion) return new(JobAction.Run, "the transcriber or its options changed");
        if (state.Status == JobStatus.Done)
            return state.DetectorVersion == detectorVersion ? new(JobAction.Skip, "done") : new(JobAction.Redetect, "the detectors changed");
        if (state.Attempts >= MaxAttempts)
        {
            return new(JobAction.Skip, state.Status == JobStatus.Failed
                ? $"failed {state.Attempts} times: {state.LastError}"
                : $"interrupted or retried {state.Attempts} times without finishing (last state {state.Status}): parked");
        }
        if (state.NextAttemptUtc is { } next && next > now) return new(JobAction.Wait, $"retry at {next:u}");
        return new(JobAction.Run, state.Status == JobStatus.Running ? "resume after an interruption" : "retry");
    }

    /// <summary>5 minutes after the first failure, doubling, at most 6 hours.</summary>
    public static DateTimeOffset NextAttempt(int attempts, DateTimeOffset now) =>
        now + TimeSpan.FromMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
}

public static class JobStore
{
    public static JobState? Read(string dir) => AnalysisJson.TryRead<JobState>(Path.Combine(dir, AnalysisPaths.StateFile), out _);

    public static void Write(string dir, JobState state) => AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.StateFile), state);
}
