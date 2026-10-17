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
            MoveOver(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>The waits between the tries of a replace that a reader is blocking (the web app reads these files over a Windows bind mount).</summary>
    public static readonly IReadOnlyList<TimeSpan> ReplaceRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="temp"/>. A reader holding the target open can make the replace fail on
    /// Windows (a bind mount), so it is tried again after 50, 100 and 200 ms before the error is allowed through.
    /// <paramref name="move"/> and <paramref name="sleep"/> are test seams.
    /// </summary>
    public static void MoveOver(string temp, string target, Action<string, string>? move = null, Action<TimeSpan>? sleep = null)
    {
        move ??= static (from, to) => File.Move(from, to, overwrite: true);
        sleep ??= Thread.Sleep;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                move(temp, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceRetryDelays.Count)
            {
                sleep(ReplaceRetryDelays[attempt]);
            }
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
