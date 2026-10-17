namespace WebAudioArchive.Core.Analysis;

/// <summary>The values of the "status" field in a segment's analysis state.json.</summary>
public static class JobStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
}
