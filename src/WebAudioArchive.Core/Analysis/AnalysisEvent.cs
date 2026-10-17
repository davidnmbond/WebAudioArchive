using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

public static class EventTypes
{
    public const string Swear = "swear";
    public const string Clipping = "clipping";
    public const string Filler = "filler";
    public const string DeadAir = "dead-air";

    /// <summary>Reserved for song identification (spec 3.9); nothing writes it yet.</summary>
    public const string Song = "song";
}

public static class Severities
{
    public const string Info = "info";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public static class EventSources
{
    public const string Audio = "audio";
    public const string Transcript = "transcript";
    public const string Llm = "llm";
}

/// <summary>
/// One detected event on the station timeline. Times are UTC; <see cref="AudioOffset"/> and <see cref="AudioSeconds"/>
/// locate it in the segment the job read. Detected, not proven: <see cref="Confidence"/> is the detector's own estimate.
/// </summary>
public sealed record AnalysisEvent(
    string Id,
    string Type,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Severity,
    double Confidence,
    string Detector,
    string DetectorVersion,
    string Source,
    string Recorder,
    string JobId,
    double AudioOffset,
    double AudioSeconds,
    string? Label,
    IReadOnlyDictionary<string, string>? Evidence)
{
    /// <summary>How far before an event the show page starts playing it.</summary>
    public const double LinkPaddingSeconds = 5;

    /// <summary>A swear hit that detection set aside as probable hallucination (repeated or invented text): for review only, never counted, shown or aggregated as swearing.</summary>
    [JsonIgnore]
    public bool IsReviewOnly => Type == EventTypes.Swear && Evidence?.GetValueOrDefault("suppressed") is { } s && s != "no";

    /// <summary>The web app's show page for the show on air at this event, from that recorder, with the event selected (/at finds the show).</summary>
    public string Link => $"/at?utc={Utc(StartUtc)}&recorder={Recorder}";

    /// <summary>Stable across rebuilds: the same detection on the same audio always gets the same id.</summary>
    public static string MakeId(string type, string jobId, double audioOffset, string? discriminator = null)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{type}|{jobId}|{Math.Round(audioOffset, 3):0.000}|{discriminator}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>A UTC stretch of audio that a recorder's analysed segment covers.</summary>
public sealed record SourceSpan(string Recorder, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// events.json. Scope is "segment:&lt;job id&gt;" for one master segment or "day:&lt;yyyy-MM-dd&gt;" for the merged UTC day.
/// Versions names the detector and transcript versions used; Coverage is the audio the events were looked for in.
/// Rebuildable at any time from transcript.json and features.json, without touching audio.
/// </summary>
public sealed record EventsDocument(
    int Schema,
    string Station,
    string Scope,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset BuiltUtc,
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyList<SourceSpan> Coverage,
    IReadOnlyDictionary<string, double>? Stats,
    IReadOnlyList<AnalysisEvent> Events)
{
    public const int CurrentSchema = 1;

    [JsonIgnore]
    public bool IsDay => Scope.StartsWith("day:", StringComparison.Ordinal);
}
