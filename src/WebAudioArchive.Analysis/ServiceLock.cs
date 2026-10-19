using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WebAudioArchive.Analysis;

/// <summary>
/// A lock file in the analysis output folder that shows the service is running. The service writes its process id, host name,
/// process start time and start time, and refreshes the file's write time every pass. The maintenance commands (--rebuild,
/// --unpark) refuse to run while it is live, because they write the same files as the service: stop the service first.
/// A lock is stale, and ignored, when it has not been refreshed for <see cref="StaleAfter"/> (a killed container leaves one),
/// or when it is ours: a restarted container has the same host name and is PID 1 again, so a lock that names this host and
/// this process id was left by an earlier life of this very process slot. On the same host a different pid is live only if
/// that process exists and started when the lock says (a reused pid is not the service). A lock from another host (another
/// container sharing the folder, for example the maintenance run) is live for as long as it is fresh: its pid means nothing here.
/// </summary>
public sealed class ServiceLock(string dir)
{
    public const string FileName = ".analysis-service.lock";

    /// <summary>The service refreshes the lock each pass (every 60 s); five minutes without a refresh means it is gone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>How far a recorded process start time may differ from the real one (file systems and clocks round) before the pid is called reused.</summary>
    static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    string PathOfLock => Path.Combine(dir, FileName);

    bool _held;

    /// <summary>Takes the lock unless a live service holds it; <paramref name="problem"/> says who does.</summary>
    public bool TryAcquire(DateTimeOffset now, out string? problem)
    {
        problem = Describe(dir, now);
        if (problem is not null) return false;
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathOfLock, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            host = Environment.MachineName,
            processStartUtc = Stamp(ProcessStart(Environment.ProcessId) ?? now),
            startedUtc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        }));
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
        return holder is null ? null : holder + " Stop the service first (stop the analysis container, or scale its Kubernetes deployment to 0), run the command, then start it again.";
    }

    static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    static string? Describe(string dir, DateTimeOffset now)
    {
        var file = Path.Combine(dir, FileName);
        if (!File.Exists(file)) return null;
        try
        {
            if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) > StaleAfter) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var pid = root.GetProperty("pid").GetInt32();
            var host = root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            var sameHost = host is null || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            // Another host: the pid cannot be checked here, so a fresh lock is live.
            if (!sameHost) return $"An analysis service is already running on {dir} (host {host}, process {pid}).";
            // Ours: this process slot (PID 1 in a restarted container) held it before a hard exit.
            if (pid == Environment.ProcessId) return null;
            if (!IsAlive(pid)) return null;
            if (root.TryGetProperty("processStartUtc", out var s) && s.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var recorded)
                && ProcessStart(pid) is { } actual && (actual - recorded).Duration() > StartTolerance)
                return null;
            return $"An analysis service is already running on {dir} (process {pid}).";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static DateTimeOffset? ProcessStart(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
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
