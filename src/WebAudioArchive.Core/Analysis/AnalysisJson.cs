using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>JSON for every analysis file: camelCase, UTC offsets as written, nulls left out, written atomically.</summary>
public static class AnalysisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Written to a unique temporary file and renamed, so a reader never sees half a file.</summary>
    public static void Write<T>(string path, T value)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;

    /// <summary>Null with a reason instead of an exception, for files that may be half written by an older version or damaged.</summary>
    public static T? TryRead<T>(string path, out string? error) where T : class
    {
        try
        {
            error = null;
            return Read<T>(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }
}
