using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Config;

public sealed record StationConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ListenUrl { get; init; }
    public string? ScheduleUrl { get; init; }
    public string Timezone { get; init; } = "Europe/London";
    public bool Enabled { get; init; } = true;
}

public sealed class ConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static partial class StationConfigLoader
{
    sealed record Root(List<StationConfig>? Stations);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<StationConfig> Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException([$"{path}: file not found"]);
        return Parse(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<StationConfig> Parse(string json, string source)
    {
        Root? root;
        try
        {
            root = JsonSerializer.Deserialize<Root>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigException([$"{source}: {ex.Message} (at {ex.Path})"]);
        }

        var stations = root?.Stations ?? [];
        var errors = new List<string>();
        if (stations.Count == 0) errors.Add($"{source}: stations must contain at least one station");

        var seen = new HashSet<string>();
        for (var i = 0; i < stations.Count; i++)
        {
            var s = stations[i];
            var at = $"{source}: stations[{i}]";
            if (!IdPattern().IsMatch(s.Id)) errors.Add($"{at}.id '{s.Id}' must be lowercase letters, digits and hyphens");
            if (!seen.Add(s.Id)) errors.Add($"{at}.id duplicate id '{s.Id}'");
            if (!IsHttpUrl(s.ListenUrl)) errors.Add($"{at}.listenUrl '{s.ListenUrl}' is not an absolute http(s) URL");
            if (s.ScheduleUrl is not null && !IsHttpUrl(s.ScheduleUrl)) errors.Add($"{at}.scheduleUrl '{s.ScheduleUrl}' is not an absolute http(s) URL");
            try { TimeZoneInfo.FindSystemTimeZoneById(s.Timezone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                errors.Add($"{at}.timezone '{s.Timezone}' is not a known IANA time zone");
            }
        }

        if (errors.Count > 0) throw new ConfigException(errors);
        return stations;
    }

    static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
