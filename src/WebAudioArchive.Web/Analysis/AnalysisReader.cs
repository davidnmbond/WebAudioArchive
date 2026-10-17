using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>The most transcript lines one request may return; more is a 413 (ask for a shorter range).</summary>
    public const int MaxTranscriptLines = 20000;

    /// <summary>Dates outside these years are refused (400): they are never real broadcasts and overflow date arithmetic.</summary>
    public const int MinYear = 2000;

    public const int MaxYear = 2100;

    const int MaxCachedTranscripts = 16;

    readonly ConcurrentDictionary<string, (long Length, DateTime Written, TranscriptDocument Doc)> _cache = new();
    int _reads;

    /// <summary>How many transcript files have been parsed (a cache hit does not count); for diagnostics and tests.</summary>
    public int TranscriptFileReads => Volatile.Read(ref _reads);

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
        if (f.Year < MinYear || t.Year > MaxYear || t.Year < MinYear || f.Year > MaxYear)
            throw new AnalysisQueryException(400, string.Create(CultureInfo.InvariantCulture, $"Times must be in the years {MinYear} to {MaxYear}."));
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

    public IReadOnlyList<TimedLine> Transcript(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            var lines = TranscriptSlicer.Slice(AnalysisStore.Transcripts(Paths, from, to, ReadCached), from, to, Preference);
            if (lines.Count > MaxTranscriptLines)
                throw new AnalysisQueryException(413, $"That range has more than {MaxTranscriptLines} transcript lines; ask for a shorter range.");
            return lines;
        }
        catch (TranscriptTooLargeException)
        {
            throw new AnalysisQueryException(413, "A transcript in this range is too large to serve.");
        }
    }

    TranscriptDocument? ReadCached(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (_cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc) return hit.Doc;
        var doc = AnalysisStore.ReadTranscript(path);
        Interlocked.Increment(ref _reads);
        if (doc is null) return null;
        if (_cache.Count >= MaxCachedTranscripts) _cache.Clear();
        _cache[path] = (info.Length, info.LastWriteTimeUtc, doc);
        return doc;
    }

    /// <summary>An event as the API returns it: the stored fields and link, plus <c>reviewOnly</c> (a swear hit set aside as probable hallucination).</summary>
    public static JsonNode EventView(AnalysisEvent e)
    {
        var node = JsonSerializer.SerializeToNode(e, JsonSerializerOptions.Web)!.AsObject();
        node["reviewOnly"] = e.IsReviewOnly;
        return node;
    }

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

    /// <summary>Runs a query: 200 with its result, a query error keeps its status (400, 404, 413), and a file error is a 500 with a generic message (never a path).</summary>
    public static (int Status, object Body) Run(Func<object> make)
    {
        try
        {
            return (200, make());
        }
        catch (AnalysisQueryException ex)
        {
            return (ex.StatusCode, new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (500, new { error = "The analysis files could not be read." });
        }
    }

    public static IResult Respond(Func<object> make)
    {
        var (status, body) = Run(make);
        return Results.Json(body, statusCode: status);
    }

    static DateTimeOffset Utc(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('Z')
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
            throw new AnalysisQueryException(400, $"{name} must be a UTC time ending in Z, such as 2026-10-15T19:00:00Z.");
        return t;
    }
}
