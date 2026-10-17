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
    int SuppressedSwearRuns)
{
    /// <summary>The highest clipping peak (0 to 1) of any clipping event overlapping the show; null when there was none.</summary>
    public double? WorstClippingPeak { get; init; }

    /// <summary>The tier counts split at the watershed (spec 3.6): before and after, by severity tier. Suppressed runs are excluded; each before total adds up to <see cref="SwearsBeforeWatershed"/>.</summary>
    public int SwearsMildBeforeWatershed { get; init; }

    public int SwearsMildAfterWatershed { get; init; }

    public int SwearsStrongBeforeWatershed { get; init; }

    public int SwearsStrongAfterWatershed { get; init; }

    public int SwearsSevereBeforeWatershed { get; init; }

    public int SwearsSevereAfterWatershed { get; init; }
}

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
    /// <summary>2 adds the worst clipping peak and the per-tier before and after watershed counts to the counts (older files read with those at zero or null).</summary>
    public const int CurrentSchema = 2;
}
