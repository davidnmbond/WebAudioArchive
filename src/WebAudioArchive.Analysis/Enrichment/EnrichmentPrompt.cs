using System.Globalization;
using System.Text;
using System.Text.Json;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// The committed prompt (config/analysis/prompts/&lt;name&gt;.system.md, .user.md, .schema.json). Its version is the name
/// plus a hash of the three files, recorded with every result, so editing a prompt re-enriches shows.
/// </summary>
public sealed record EnrichmentPrompt(string Name, string System, string UserTemplate, JsonElement Schema, string Version)
{
    public static EnrichmentPrompt Load(string configDir, EnrichmentOptions o)
    {
        var stem = Path.Combine(configDir, o.PromptDir, o.PromptName);
        string Read(string extension)
        {
            var path = stem + extension;
            if (!File.Exists(path)) throw new AnalysisConfigException([$"enrichment prompt file {path} is missing"]);
            return File.ReadAllText(path);
        }

        var system = Read(".system.md");
        var user = Read(".user.md");
        var schemaText = Read(".schema.json");
        if (!user.Contains("{{transcript}}", StringComparison.Ordinal))
            throw new AnalysisConfigException([$"{stem}.user.md must contain {{{{transcript}}}}"]);
        JsonElement schema;
        try
        {
            using var doc = JsonDocument.Parse(schemaText);
            schema = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{stem}.schema.json is not JSON: {ex.Message}"]);
        }

        return new EnrichmentPrompt(o.PromptName, system.Trim(), user, schema, o.PromptName + "+" + AnalysisConfigLoader.Hash(system + "\n" + user + "\n" + schemaText));
    }

    public string User(string station, string title, DateTimeOffset startUtc, DateTimeOffset endUtc, int part, int parts, IReadOnlyList<TimedLine> lines)
    {
        var transcript = new StringBuilder();
        foreach (var l in lines)
            transcript.Append('[').Append(l.StartUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ").AppendLine(l.Text);
        return UserTemplate
            .Replace("{{station}}", station, StringComparison.Ordinal)
            .Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{startUtc}}", startUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{endUtc}}", endUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{part}}", part.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{parts}}", parts.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{transcript}}", transcript.ToString().TrimEnd(), StringComparison.Ordinal);
    }
}
