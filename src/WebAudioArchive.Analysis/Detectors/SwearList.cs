using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Detectors;

public static class SwearTiers
{
    public const string Mild = "mild";
    public const string Strong = "strong";
    public const string Severe = "severe";

    public static readonly string[] All = [Mild, Strong, Severe];

    public static int Rank(string tier) => tier switch { Mild => 1, Strong => 2, Severe => 3, _ => 0 };
}

/// <summary>
/// The tiered word list (config/analysis/swearwords.json): each entry and each entry plus a listed suffix is a form
/// that matches a whole transcript word; forms on the allow list never match. Matching is added in Task 7.
/// </summary>
public sealed partial class SwearList
{
    sealed record ListFile(string? Version, List<string>? Suffixes, Dictionary<string, List<string>>? Tiers, List<string>? Allow);

    SwearList(string version, IReadOnlyDictionary<string, string> forms, IReadOnlySet<string> allow, IReadOnlyList<string> suffixes)
    {
        Version = version;
        Forms = forms;
        Allow = allow;
        Suffixes = suffixes;
    }

    public string Version { get; }

    /// <summary>Every matchable form (lower case) and its tier.</summary>
    public IReadOnlyDictionary<string, string> Forms { get; }

    public IReadOnlySet<string> Allow { get; }

    public IReadOnlyList<string> Suffixes { get; }

    public static SwearList Load(string path) => Parse(File.ReadAllText(path), path);

    public static SwearList Parse(string json, string source)
    {
        ListFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ListFile>(json, AnalysisConfigLoader.Options);
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{source}: {ex.Message}"]);
        }

        var errors = new List<string>();
        if (file is null || string.IsNullOrWhiteSpace(file.Version)) errors.Add($"{source}: version is required");
        var suffixes = file?.Suffixes ?? [];
        var allow = new HashSet<string>(file?.Allow ?? [], StringComparer.Ordinal);
        foreach (var s in suffixes.Concat(allow))
            if (!Letters().IsMatch(s)) errors.Add($"{source}: '{s}' must be lower-case letters only");

        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tier, words) in file?.Tiers ?? new Dictionary<string, List<string>>())
        {
            if (SwearTiers.Rank(tier) == 0)
            {
                errors.Add($"{source}: unknown tier '{tier}' (use mild, strong or severe)");
                continue;
            }

            foreach (var w in words)
            {
                if (!Letters().IsMatch(w))
                {
                    errors.Add($"{source}: '{w}' in tier {tier} must be lower-case letters only");
                    continue;
                }

                if (seen.TryGetValue(w, out var other) && other != tier)
                {
                    errors.Add($"{source}: '{w}' is in more than one tier ({other} and {tier})");
                    continue;
                }

                seen[w] = tier;
                foreach (var form in suffixes.Select(s => w + s).Prepend(w))
                    if (!forms.TryGetValue(form, out var existing) || SwearTiers.Rank(existing) < SwearTiers.Rank(tier))
                        forms[form] = tier;
            }
        }

        if (forms.Count == 0) errors.Add($"{source}: tiers must list at least one word");
        if (errors.Count > 0) throw new AnalysisConfigException(errors);
        foreach (var a in allow) forms.Remove(a);
        return new SwearList(file!.Version!, forms, allow, suffixes);
    }

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex Letters();
}
