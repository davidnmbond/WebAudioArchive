using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Analysis.Settings;

/// <summary>One recorder whose closed segments are analysed: its id (a, b) and its data root, mounted read-only.</summary>
public sealed record RecorderRoot(string Id, string Root);

/// <summary>Where whisper runs (D18): Auto tries the CUDA runtime and falls back to the CPU.</summary>
public enum WhisperDevice
{
    Auto,
    Cpu,
    Cuda,
}

public sealed class AnalysisSettingsException(string message) : Exception(message);

/// <summary>
/// Analysis service settings, from environment variables (or any configuration source with the same names). Every
/// problem names the setting. Defaults are the values used in docker/docker-compose.yml.
/// </summary>
public sealed partial record AnalysisSettings(
    string Station,
    IReadOnlyList<RecorderRoot> Recorders,
    IReadOnlyList<string> Preference,
    string OutputDir,
    string ScratchDir,
    long ScratchMaxBytes,
    long MinFreeBytes,
    string WhisperModelPath,
    WhisperDevice WhisperDevice,
    int WhisperThreads,
    bool WhisperDtw,
    Uri OllamaUrl,
    string OllamaModel,
    bool EnrichmentEnabled,
    string ConfigPath,
    string? StationsConfigPath,
    string? ScheduleFile,
    DateTimeOffset? Since,
    TimeSpan LagAlarm,
    int Port)
{
    /// <summary>The model label recorded in transcripts: the model file name without its extension.</summary>
    public string WhisperModel => Path.GetFileNameWithoutExtension(WhisperModelPath);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    public static AnalysisSettings Load(Func<string, string?> get)
    {
        string Text(string name, string fallback)
        {
            var v = get(name);
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }

        string? Optional(string name)
        {
            var v = get(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        double Number(string name, double fallback, double min, double max)
        {
            var v = get(name);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < min || d > max)
                throw new AnalysisSettingsException(string.Create(CultureInfo.InvariantCulture, $"{name}: '{v}' must be a number from {min} to {max}."));
            return d;
        }

        var station = Text("WAA_STATION", "burst");
        if (!IdPattern().IsMatch(station))
            throw new AnalysisSettingsException($"WAA_STATION: '{station}' is not a valid station id (lower-case letters, digits and dashes).");

        var recordersText = get("WAA_RECORDERS");
        if (string.IsNullOrWhiteSpace(recordersText))
            throw new AnalysisSettingsException("WAA_RECORDERS is not set. Give each recorder id and its read-only data root, for example WAA_RECORDERS=a=/data/a,b=/data/b");
        var recorders = new List<RecorderRoot>();
        foreach (var (id, root) in ParsePairs("WAA_RECORDERS", recordersText))
        {
            if (!Directory.Exists(root))
                throw new AnalysisSettingsException($"WAA_RECORDERS: the data root '{root}' for recorder '{id}' does not exist.");
            recorders.Add(new RecorderRoot(id, root));
        }

        var preference = Text("WAA_ANALYSIS_PREFERENCE", string.Join(',', recorders.Select(r => r.Id)))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct()
            .ToList();
        foreach (var p in preference)
            if (recorders.All(r => r.Id != p))
                throw new AnalysisSettingsException($"WAA_ANALYSIS_PREFERENCE: recorder '{p}' is not listed in WAA_RECORDERS.");
        foreach (var r in recorders.Where(r => !preference.Contains(r.Id)))
            preference.Add(r.Id);

        var device = Text("WAA_WHISPER_DEVICE", "auto").ToLowerInvariant() switch
        {
            "auto" => WhisperDevice.Auto,
            "cpu" => WhisperDevice.Cpu,
            "cuda" => WhisperDevice.Cuda,
            var v => throw new AnalysisSettingsException($"WAA_WHISPER_DEVICE: '{v}' must be auto, cpu or cuda."),
        };
        var dtw = Text("WAA_WHISPER_DTW", "off").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            var v => throw new AnalysisSettingsException($"WAA_WHISPER_DTW: '{v}' must be on or off."),
        };
        var ollama = HttpUrl("WAA_OLLAMA_URL", Text("WAA_OLLAMA_URL", "http://host.docker.internal:11434"));
        var allowRemote = string.Equals(Optional("WAA_OLLAMA_ALLOW_REMOTE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!allowRemote && !IsLocalOrPrivate(ollama))
            throw new AnalysisSettingsException($"WAA_OLLAMA_URL: '{ollama}' is not on this machine or a private network, so transcripts would leave it. Use a local Ollama, or set WAA_OLLAMA_ALLOW_REMOTE=true if that host is yours.");

        var model = Text("WAA_OLLAMA_MODEL", "qwen3.6:latest");
        if (model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
            throw new AnalysisSettingsException($"WAA_OLLAMA_MODEL: '{model}' is an Ollama cloud model, which sends the transcript off the machine. Choose a local model such as qwen3.6:latest.");

        var enrichment = Text("WAA_ENRICHMENT", "on").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            var v => throw new AnalysisSettingsException($"WAA_ENRICHMENT: '{v}' must be on or off."),
        };

        DateTimeOffset? since = null;
        if (Optional("WAA_ANALYSIS_SINCE") is { } sinceText)
        {
            if (!sinceText.EndsWith('Z')
                || !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var s))
                throw new AnalysisSettingsException($"WAA_ANALYSIS_SINCE: '{sinceText}' must be a UTC time ending in Z, such as 2026-10-15T00:00:00Z.");
            since = s;
        }

        return new AnalysisSettings(
            station,
            recorders,
            preference,
            Text("WAA_ANALYSIS_OUT", "/analysis"),
            Text("WAA_SCRATCH", "/scratch"),
            (long)Number("WAA_SCRATCH_MAX_MB", 2048, 64, 1_048_576) * 1024 * 1024,
            (long)(Number("WAA_MIN_FREE_GB", 20, 0, 1_000_000) * 1024 * 1024 * 1024),
            Text("WAA_WHISPER_MODEL_PATH", "/models/ggml-large-v3-turbo-q8_0.bin"),
            device,
            (int)Number("WAA_WHISPER_THREADS", 4, 1, 64),
            dtw,
            ollama,
            model,
            enrichment,
            Text("WAA_ANALYSIS_CONFIG", "/config/analysis/analysis.json"),
            Optional("WAA_STATIONS_CONFIG"),
            Optional("WAA_SCHEDULE_FILE"),
            since,
            TimeSpan.FromHours(Number("WAA_LAG_ALARM_HOURS", 6, 0.1, 24 * 365)),
            (int)Number("WAA_PORT", 8080, 1, 65535));
    }

    static Uri HttpUrl(string name, string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u
            : throw new AnalysisSettingsException($"{name}: '{value}' is not an absolute http(s) URL.");

    /// <summary>Loopback, Docker's host alias, a single-label name (a compose service) or a private address.</summary>
    static bool IsLocalOrPrivate(Uri u)
    {
        var host = u.Host.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" or "host.docker.internal") return true;
        if (!IPAddress.TryParse(host, out var ip)) return !host.Contains('.');
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal);
    }

    static List<(string Id, string Value)> ParsePairs(string name, string text)
    {
        var result = new List<(string, string)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                throw new AnalysisSettingsException($"{name}: '{part}' is not of the form id=value.");
            var id = part[..eq].Trim().ToLowerInvariant();
            if (!IdPattern().IsMatch(id))
                throw new AnalysisSettingsException($"{name}: '{id}' is not a valid recorder id (lower-case letters, digits and dashes).");
            if (result.Any(r => r.Item1 == id))
                throw new AnalysisSettingsException($"{name}: recorder '{id}' is listed twice.");
            result.Add((id, part[(eq + 1)..].Trim()));
        }

        return result;
    }
}
