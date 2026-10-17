namespace WebAudioArchive.Core.Analysis;

/// <summary>An analysed segment that overlaps a show's window.</summary>
public sealed record ShowSource(string Recorder, string JobId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// Per-show figures for the weekly report (spec 3.8), over the show proper (start to end, not the margins). Counts are
/// detections, not proven events. FillersPerMinute is per minute of transcribed speech, an estimate, null under a minute.
/// AnalysedSeconds is how much of the show proper had analysed audio. SuppressedSwearRuns counts swear runs that detection
/// set aside as probable hallucination (review only): they are never in the tier counts or the watershed total.
/// </summary>
public sealed record ShowCounts(
    int SwearsMild,
    int SwearsStrong,
    int SwearsSevere,
    int SwearsBeforeWatershed,
    int ClippingEvents,
    double ClippedMs,
    int DeadAirEvents,
    double DeadAirSeconds,
    int Fillers,
    double SpeechMinutes,
    double? FillersPerMinute,
    double AnalysedSeconds,
    int SuppressedSwearRuns);

/// <summary>show-analysis.json: one scheduled show mapped onto the analysed segments. Rebuilt whenever its segments change.</summary>
public sealed record ShowAnalysis(
    int Schema,
    string ShowId,
    string Station,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset WindowFromUtc,
    DateTimeOffset WindowToUtc,
    DateTimeOffset BuiltUtc,
    bool Complete,
    IReadOnlyList<ShowSource> Sources,
    ShowCounts Counts,
    IReadOnlyList<string> Notes)
{
    public const int CurrentSchema = 1;
}
