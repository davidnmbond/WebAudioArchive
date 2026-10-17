using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WebAudioArchive.Analysis;

/// <summary>
/// A lock file in the analysis output folder that shows the service is running. The service writes its process id and
/// start time, and refreshes the file's write time every pass. The maintenance commands (--rebuild, --unpark) refuse to run
/// while it is live, because they write the same files as the service: stop the service first. A lock is stale, and ignored,
/// when its process no longer exists or it has not been refreshed for <see cref="StaleAfter"/> (a killed container leaves one).
/// </summary>
public sealed class ServiceLock(string dir)
{
    public const string FileName = ".analysis-service.lock";

    /// <summary>The service refreshes the lock each pass (every 60 s); five minutes without a refresh means it is gone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    string PathOfLock => Path.Combine(dir, FileName);

    bool _held;

    /// <summary>Takes the lock unless a live service holds it; <paramref name="problem"/> says who does.</summary>
    public bool TryAcquire(DateTimeOffset now, out string? problem)
    {
        problem = Describe(dir, now);
        if (problem is not null) return false;
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathOfLock, JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedUtc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }));
        _held = true;
        return true;
    }

    /// <summary>Marks the service as alive; call each pass.</summary>
    public void Refresh()
    {
        if (!_held) return;
        try
        {
            File.SetLastWriteTimeUtc(PathOfLock, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A lock that cannot be refreshed only makes the commands think the service is gone; the service keeps working.
        }
    }

    public void Release()
    {
        if (!_held) return;
        _held = false;
        try
        {
            File.Delete(PathOfLock);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Null when no live service holds the lock in <paramref name="dir"/>; otherwise a message for the operator.</summary>
    public static string? RunningMessage(string dir, DateTimeOffset now)
    {
        var holder = Describe(dir, now);
        return holder is null ? null : holder + " Stop the service first (docker compose stop analysis), run the command, then start it again.";
    }

    static string? Describe(string dir, DateTimeOffset now)
    {
        var file = Path.Combine(dir, FileName);
        if (!File.Exists(file)) return null;
        try
        {
            if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) > StaleAfter) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var pid = doc.RootElement.GetProperty("pid").GetInt32();
            if (!IsAlive(pid)) return null;
            return $"An analysis service is already running on {dir} (process {pid}).";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
