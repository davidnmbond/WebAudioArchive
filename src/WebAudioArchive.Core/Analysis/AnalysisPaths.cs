using System.Globalization;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Analysis;

/// <summary>
/// The analysis output layout, under the analysis root (data/analysis on the host):
/// &lt;station&gt;/segments/&lt;recorder&gt;/&lt;yyyy&gt;/&lt;MM-dd&gt;/&lt;stem&gt;/ (state, features, transcript, events per master segment),
/// &lt;station&gt;/events/&lt;yyyy&gt;/&lt;MM-dd&gt;.json (the UTC day's merged timeline) and
/// &lt;station&gt;/shows/&lt;yyyy&gt;/&lt;show id&gt;/ (show-analysis.json, enrichment.json).
/// </summary>
public sealed partial class AnalysisPaths(string root, string station)
{
    public const string StateFile = "state.json";
    public const string FeaturesFile = "features.json";
    public const string PartialFile = "transcript.partial.json";
    public const string TranscriptFile = "transcript.json";
    public const string EventsFile = "events.json";
    public const string ShowAnalysisFile = "show-analysis.json";
    public const string EnrichmentFile = "enrichment.json";

    public string Root => root;

    public string Station => station;

    public string StationDir => Path.Combine(root, station);

    public string SegmentsDir => Path.Combine(StationDir, "segments");

    public string EventsDir => Path.Combine(StationDir, "events");

    public string ShowsDir => Path.Combine(StationDir, "shows");

    /// <summary>The folder for a job id such as a/2026/10-16/080000. Only plain path parts are accepted.</summary>
    public string SegmentDir(string jobId)
    {
        var parts = jobId.Split('/');
        if (parts.Length < 2 || parts.Any(p => !SafePart().IsMatch(p) || p is "." or ".."))
            throw new ArgumentException($"'{jobId}' is not a valid job id", nameof(jobId));
        return Path.Combine([SegmentsDir, .. parts]);
    }

    public string DayEvents(DateOnly utcDate) =>
        Path.Combine(EventsDir, utcDate.ToString("yyyy", CultureInfo.InvariantCulture), utcDate.ToString("MM-dd", CultureInfo.InvariantCulture) + ".json");

    public string ShowDir(string showId, DateTimeOffset startUtc)
    {
        if (!SafePart().IsMatch(showId)) throw new ArgumentException($"'{showId}' is not a valid show id", nameof(showId));
        return Path.Combine(ShowsDir, startUtc.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture), showId);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafePart();
}
