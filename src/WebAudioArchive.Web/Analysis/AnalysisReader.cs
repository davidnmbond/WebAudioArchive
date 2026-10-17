using System.Globalization;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Web.Analysis;

public sealed class AnalysisQueryException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The web app's read-only view of the analysis output (WAA_ANALYSIS, mounted read-only): events, transcript lines and
/// show documents for a UTC range or a show id. It never writes there, and no value from a request is turned into a path:
/// times are parsed, and a show id is parsed and then matched against the folders that exist.
/// <para>
/// SECURITY: these endpoints have no sign-in. Like the rest of the web app they must only be published on loopback until
/// sign-in is added (plan 4a); the transcripts and swear events here are about identifiable presenters. Do not expose them
/// before that.
/// </para>
/// <para>
/// Swear hits that detection set aside as probable hallucination are review only: <see cref="Events"/> leaves them out unless
/// <c>includeReview</c> is asked for, and the markers draw them as a different kind.
/// </para>
/// </summary>
public sealed class AnalysisReader(WebSettings settings)
{
    public static readonly TimeSpan MaxEventsRange = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxTranscriptRange = TimeSpan.FromHours(6);

    AnalysisPaths Paths => settings.AnalysisDir is { } dir && Directory.Exists(dir)
        ? new AnalysisPaths(dir, settings.Station)
        : throw new AnalysisQueryException(404, "Analysis is not set up for this web app (WAA_ANALYSIS is not set or its folder does not exist yet).");

    IReadOnlyList<string> Preference => settings.Recorders.Select(r => r.Id).ToList();

    /// <summary>Both ends must be UTC times ending in Z, from before to, at most <paramref name="max"/> apart.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(string? from, string? to, TimeSpan max)
    {
        var f = Utc(from, "from");
        var t = Utc(to, "to");
        if (t <= f) throw new AnalysisQueryException(400, "to must be after from.");
        if (t - f > max) throw new AnalysisQueryException(400, string.Create(CultureInfo.InvariantCulture, $"The range may be at most {max.TotalHours:0} hours."));
        return (f, t);
    }

    public IReadOnlyList<AnalysisEvent> Events(DateTimeOffset from, DateTimeOffset to, string? types, bool includeReview = false)
    {
        var wanted = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return AnalysisStore.DayEvents(Paths, from, to)
            .Where(e => wanted.Count == 0 || wanted.Contains(e.Type))
            .Where(e => includeReview || !e.IsReviewOnly)
            .ToList();
    }

    public IReadOnlyList<TimedLine> Transcript(DateTimeOffset from, DateTimeOffset to) =>
        TranscriptSlicer.Slice(AnalysisStore.Transcripts(Paths, from, to), from, to, Preference);

    public (ShowAnalysis? Analysis, ShowEnrichment? Enrichment) Show(string id)
    {
        var paths = Paths;
        if (!ShowOccurrence.TryParseId(id, out var station, out var start) || station != settings.Station
            || AnalysisStore.FindShowDir(paths, id, start) is not { } dir)
            throw new AnalysisQueryException(404, $"There is no analysis for show '{(id.Length > 64 ? id[..64] : id)}'.");
        var analysis = AnalysisJson.TryRead<ShowAnalysis>(Path.Combine(dir, AnalysisPaths.ShowAnalysisFile), out _)
            ?? throw new AnalysisQueryException(404, $"The show {id} has not been analysed yet.");
        return (analysis, AnalysisJson.TryRead<ShowEnrichment>(Path.Combine(dir, AnalysisPaths.EnrichmentFile), out _));
    }

    public static IResult Respond(Func<object> make)
    {
        try
        {
            return Results.Json(make());
        }
        catch (AnalysisQueryException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
        }
    }

    static DateTimeOffset Utc(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('Z')
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
            throw new AnalysisQueryException(400, $"{name} must be a UTC time ending in Z, such as 2026-10-15T19:00:00Z.");
        return t;
    }
}
