using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;

namespace WebAudioArchive.Analysis.Settings;

public sealed record ClippingOptions
{
    /// <summary>A sample at or above this fraction of full scale (either sign) counts as clipped. Must be in ClipLevels.</summary>
    public double Threshold { get; init; } = 0.99;
    public int MinRunSamples { get; init; } = 3;
    public int MergeWithinMs { get; init; } = 250;
    public int MediumMs { get; init; } = 100;
}

public sealed record DeadAirOptions
{
    public double ThresholdDbfs { get; init; } = -50;
    public double MinSeconds { get; init; } = 5;
    public double SevereSeconds { get; init; } = 30;
}

public sealed record SwearOptions
{
    public string WordList { get; init; } = "swearwords.json";

    /// <summary>Station local time the watershed starts (after this, strong language is less serious).</summary>
    public string WatershedStart { get; init; } = "21:00";

    /// <summary>Station local time the watershed ends (from this until WatershedStart is "before the watershed").</summary>
    public string WatershedEnd { get; init; } = "05:30";
}

public sealed record FillerOptions
{
    public IReadOnlyList<string> Words { get; init; } = ["um", "uh", "er", "erm", "ah", "hmm"];
    public bool CountYouKnow { get; init; }
}

public sealed record TranscriptionOptions
{
    public string Language { get; init; } = "en";
    public double ChunkSeconds { get; init; } = 300;
    public double BoundarySearchSeconds { get; init; } = 8;
    public IReadOnlyList<string> Glossary { get; init; } = [];
    public string VerbatimPrompt { get; init; } = "Um, so, er, welcome back. Uh, hmm, erm, right.";
    public double MaxNoSpeechProb { get; init; } = 0.6;
    public double MinAvgLogProb { get; init; } = -1.0;
    public IReadOnlyList<string> Hallucinations { get; init; } = [];
    public int MaxRepeats { get; init; } = 3;

    /// <summary>More silenced (undecodable) frames than this fraction of a segment's frames flags the transcript and events (default 0.1 percent). Not part of the transcript key.</summary>
    public double MaxSilencedFrameFraction { get; init; } = 0.001;

    /// <summary>The options that change what whisper is asked; a change re-transcribes. Filter options do not.</summary>
    public string TranscriptKey() =>
        AnalysisConfigLoader.Hash(string.Join('|', Language, ChunkSeconds.ToString(CultureInfo.InvariantCulture),
            BoundarySearchSeconds.ToString(CultureInfo.InvariantCulture), string.Join(',', Glossary), VerbatimPrompt));
}

public sealed record EnrichmentOptions
{
    public string PromptDir { get; init; } = "prompts";
    public string PromptName { get; init; } = "enrich-v1";
    public int NumCtx { get; init; } = 32768;
    public int MaxTranscriptChars { get; init; } = 60000;
    public int Seed { get; init; } = 42;
    public double Temperature { get; init; } = 0.1;
    public int MaxItems { get; init; } = 40;
    public int MaxOutputTokens { get; init; } = 8192;
}

public sealed record AnalysisConfig
{
    public required string Version { get; init; }
    public ClippingOptions Clipping { get; init; } = new();
    public DeadAirOptions DeadAir { get; init; } = new();
    public SwearOptions Swearing { get; init; } = new();
    public FillerOptions Fillers { get; init; } = new();
    public TranscriptionOptions Transcription { get; init; } = new();
    public EnrichmentOptions Enrichment { get; init; } = new();
}

/// <summary>The config, its swear list, where it was read from, and the version stamped on every event it produces.</summary>
public sealed record LoadedConfig(AnalysisConfig Config, SwearList Swears, string ConfigDir, string DetectorVersion);

public sealed class AnalysisConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid analysis configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static partial class AnalysisConfigLoader
{
    /// <summary>Bump when detector code changes what it reports, so stored events are rebuilt from transcripts and features.</summary>
    public const string DetectorCodeVersion = "1";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LoadedConfig Load(string path)
    {
        if (!File.Exists(path)) throw new AnalysisConfigException([$"{path}: file not found"]);
        var json = File.ReadAllText(path);
        AnalysisConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AnalysisConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{path}: {ex.Message}"]);
        }

        if (config is null) throw new AnalysisConfigException([$"{path}: empty configuration"]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var errors = Validate(config, path);

        SwearList? swears = null;
        var swearPath = Path.Combine(dir, config.Swearing.WordList);
        var swearJson = "";
        if (!File.Exists(swearPath))
        {
            errors.Add($"{path}: swearing.wordList '{config.Swearing.WordList}' was not found beside the config");
        }
        else
        {
            swearJson = File.ReadAllText(swearPath);
            try
            {
                swears = SwearList.Parse(swearJson, swearPath);
            }
            catch (AnalysisConfigException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }

        if (errors.Count > 0 || swears is null) throw new AnalysisConfigException(errors);
        var version = $"{config.Version}|{Hash(json)}|{swears.Version}|{Hash(swearJson)}|code{DetectorCodeVersion}";
        return new LoadedConfig(config, swears, dir, version);
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    static List<string> Validate(AnalysisConfig c, string path)
    {
        var e = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) e.Add($"{path}: {message}");
        }

        Check(!string.IsNullOrWhiteSpace(c.Version), "version must not be empty");

        Check(ClipLevels.IndexOf(c.Clipping.Threshold) >= 0,
            $"clipping.threshold {c.Clipping.Threshold.ToString(CultureInfo.InvariantCulture)} must be one of {string.Join(", ", ClipLevels.Levels.Select(l => l.ToString(CultureInfo.InvariantCulture)))} (the levels kept in features.json)");
        Check(c.Clipping.MinRunSamples >= ClipLevels.MinStoredRun && c.Clipping.MinRunSamples <= 10_000, $"clipping.minRunSamples must be from {ClipLevels.MinStoredRun} to 10000");
        Check(c.Clipping.MergeWithinMs is >= 0 and <= 10_000, "clipping.mergeWithinMs must be from 0 to 10000");
        Check(c.Clipping.MediumMs > 0, "clipping.mediumMs must be above 0");

        Check(c.DeadAir.ThresholdDbfs is >= -120 and <= -10, "deadAir.thresholdDbfs must be from -120 to -10");
        Check(c.DeadAir.MinSeconds is > 0 and <= 600, "deadAir.minSeconds must be above 0 and at most 600");
        Check(c.DeadAir.SevereSeconds >= c.DeadAir.MinSeconds, "deadAir.severeSeconds must be at least deadAir.minSeconds");

        Check(IsClock(c.Swearing.WatershedStart), "swearing.watershedStart must be HH:mm");
        Check(IsClock(c.Swearing.WatershedEnd), "swearing.watershedEnd must be HH:mm");

        Check(c.Fillers.Words.Count > 0, "fillers.words must not be empty");
        foreach (var w in c.Fillers.Words)
        {
            Check(FillerWord().IsMatch(w), $"fillers.words '{w}' must be lower-case letters");
            Check(w != "like", "fillers.words must not contain 'like': it is too ambiguous to count (spec 3.6)");
        }

        var t = c.Transcription;
        Check(LanguageCode().IsMatch(t.Language), "transcription.language must be a 2 or 3 letter code such as en");
        Check(t.ChunkSeconds is >= 30 and <= 1800, "transcription.chunkSeconds must be from 30 to 1800");
        Check(t.BoundarySearchSeconds >= 0 && t.BoundarySearchSeconds <= t.ChunkSeconds / 4, "transcription.boundarySearchSeconds must be from 0 to a quarter of chunkSeconds");
        Check(t.MaxNoSpeechProb is >= 0 and <= 1, "transcription.maxNoSpeechProb must be from 0 to 1");
        Check(t.MaxRepeats >= 2, "transcription.maxRepeats must be at least 2");
        Check(t.MaxSilencedFrameFraction is >= 0 and <= 1, "transcription.maxSilencedFrameFraction must be between 0 and 1");

        var n = c.Enrichment;
        Check(n.NumCtx is >= 2048 and <= 262_144, "enrichment.numCtx must be from 2048 to 262144");
        Check(n.MaxTranscriptChars is >= 1000 and <= 1_000_000, "enrichment.maxTranscriptChars must be from 1000 to 1000000");
        Check(n.Temperature is >= 0 and <= 2, "enrichment.temperature must be from 0 to 2");
        Check(n.MaxItems is >= 1 and <= 200, "enrichment.maxItems must be from 1 to 200");
        return e;
    }

    static bool IsClock(string s) => TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("^[a-z]+( [a-z]+)?$")]
    private static partial Regex FillerWord();

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();
}
