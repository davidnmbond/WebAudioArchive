using System.Text.RegularExpressions;

namespace WebAudioArchive.Web;

/// <summary>One recorder whose output the web app reads: its id (a, b), data root and optional /healthz URL.</summary>
public sealed record RecorderSettings(string Id, string Root, Uri? HealthUrl)
{
    public string Label => Id.ToUpperInvariant();
}

public sealed class SettingsException(string message) : Exception(message);

/// <summary>
/// Web app settings, read from environment variables (or any configuration source with the same names):
/// WAA_STATION (default burst), WAA_RECORDERS (a=/data/a,b=/data/b), WAA_RECORDER_HEALTH
/// (a=http://recorder-a:8080/healthz,...), WAA_MERGED (optional, the consolidator's merged root), WAA_CONSOLIDATOR (optional, its base URL, http://consolidator:8080), WAA_CACHE (default /cache).
/// </summary>
public sealed partial record WebSettings(string Station, IReadOnlyList<RecorderSettings> Recorders, string CacheDir)
{
    public TimeSpan CacheMaxAge { get; init; } = TimeSpan.FromHours(24);
    public int DefaultMarginMinutes { get; init; } = 5;

    /// <summary>The analysis output folder (WAA_ANALYSIS, read-only), or null when analysis is not set up.</summary>
    public string? AnalysisDir { get; init; }

    /// <summary>Recorder id of the consolidator's merged hours.</summary>
    public const string MergedId = "merged";

    /// <summary>The consolidator's output root (WAA_MERGED, read-only: data/merged), or null when merged hours are not read.</summary>
    public string? MergedRoot { get; init; }

    /// <summary>The consolidator base URL (WAA_CONSOLIDATOR), or null when the consolidator is not used.</summary>
    public Uri? ConsolidatorUrl { get; init; }

    public RecorderSettings? Find(string id) => Recorders.FirstOrDefault(r => r.Id == id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    public static partial Regex IdPattern();

    /// <summary>Throws <see cref="SettingsException"/> naming the setting when one is missing or wrong.</summary>
    public static WebSettings Load(Func<string, string?> get)
    {
        var station = (get("WAA_STATION") ?? "burst").Trim();
        if (!IdPattern().IsMatch(station))
            throw new SettingsException($"WAA_STATION: '{station}' is not a valid station id (lower-case letters, digits and dashes).");

        var recordersText = get("WAA_RECORDERS");
        if (string.IsNullOrWhiteSpace(recordersText))
            throw new SettingsException("WAA_RECORDERS is not set. Give each recorder id and its data root, for example WAA_RECORDERS=a=/data/a,b=/data/b");

        var roots = ParsePairs("WAA_RECORDERS", recordersText);
        if (roots.ContainsKey(MergedId))
            throw new SettingsException($"WAA_RECORDERS: the recorder id '{MergedId}' is reserved for the consolidator's merged copy (WAA_MERGED); give the recorder another id.");
        var health = ParsePairs("WAA_RECORDER_HEALTH", get("WAA_RECORDER_HEALTH") ?? "");
        foreach (var id in health.Keys.Where(id => !roots.ContainsKey(id)))
            throw new SettingsException($"WAA_RECORDER_HEALTH: recorder '{id}' is not listed in WAA_RECORDERS.");

        var recorders = new List<RecorderSettings>();
        foreach (var (id, root) in roots)
        {
            if (!Directory.Exists(root))
                throw new SettingsException($"WAA_RECORDERS: the data root '{root}' for recorder '{id}' does not exist.");

            Uri? url = null;
            if (health.TryGetValue(id, out var h) && !Uri.TryCreate(h, UriKind.Absolute, out url))
                throw new SettingsException($"WAA_RECORDER_HEALTH: '{h}' for recorder '{id}' is not an absolute URL.");
            recorders.Add(new RecorderSettings(id, root, url));
        }

        var cache = get("WAA_CACHE");
        var analysis = get("WAA_ANALYSIS");
        var merged = get("WAA_MERGED");
        var consolidator = get("WAA_CONSOLIDATOR");
        Uri? consolidatorUrl = null;
        if (!string.IsNullOrWhiteSpace(consolidator)
            && (!Uri.TryCreate(consolidator.Trim(), UriKind.Absolute, out consolidatorUrl) || consolidatorUrl.Scheme is not ("http" or "https")))
            throw new SettingsException($"WAA_CONSOLIDATOR: '{consolidator}' is not an absolute http(s) URL.");
        return new WebSettings(station, recorders, string.IsNullOrWhiteSpace(cache) ? "/cache" : cache.Trim())
        {
            AnalysisDir = string.IsNullOrWhiteSpace(analysis) ? null : analysis.Trim(),
            MergedRoot = string.IsNullOrWhiteSpace(merged) ? null : merged.Trim(),
            ConsolidatorUrl = consolidatorUrl,
        };
    }

    static Dictionary<string, string> ParsePairs(string name, string text)
    {
        // Ordered as given: the first recorder listed wins ties when choosing automatically.
        var result = new Dictionary<string, string>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                throw new SettingsException($"{name}: '{part}' is not of the form id=value.");
            var id = part[..eq].Trim().ToLowerInvariant();
            if (!IdPattern().IsMatch(id))
                throw new SettingsException($"{name}: '{id}' is not a valid recorder id (lower-case letters, digits and dashes).");
            if (!result.TryAdd(id, part[(eq + 1)..].Trim()))
                throw new SettingsException($"{name}: recorder '{id}' is listed twice.");
        }

        return result;
    }
}
