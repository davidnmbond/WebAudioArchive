using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>A word with its times in seconds from the start of the segment's audio (map to UTC with the document's timeline).</summary>
public sealed record TranscriptWord(string Text, double Start, double End, double Probability);

/// <summary>
/// One whisper segment. <see cref="Suppressed"/> is null for speech the detectors use, or the reason it is ignored
/// (non-speech, no-speech, known-hallucination, repetition). Suppressed segments are kept so a person can see them.
/// </summary>
public sealed record TranscriptSegment(
    double Start,
    double End,
    string Text,
    double NoSpeechProb,
    double AvgLogProb,
    string? Suppressed,
    IReadOnlyList<TranscriptWord> Words);

/// <summary>transcript.json: the transcript of one master segment. Times are offsets into its audio; the timeline maps them to UTC.</summary>
public sealed record TranscriptDocument(
    int Schema,
    string Station,
    string Recorder,
    string Instance,
    string JobId,
    string SourceFile,
    DateTimeOffset SegmentStartUtc,
    DateTimeOffset SegmentEndUtc,
    double AudioSeconds,
    IReadOnlyList<TimelineAnchor> Timeline,
    string Engine,
    string Model,
    string Language,
    string TranscriptVersion,
    string Decoder,
    DateTimeOffset CreatedUtc,
    double TranscribeSeconds,
    IReadOnlyList<TranscriptSegment> Segments)
{
    public const int CurrentSchema = 1;

    /// <summary>MPEG frames the decoder found (equals the sidecar's count, or the job fails), frames it could only write as silence, and frames it recovered by priming a fresh decoder.</summary>
    public long? DecodedFrames { get; init; }

    public int? SilencedFrames { get; init; }

    public int? RecoveredFrames { get; init; }

    /// <summary>The model file size in bytes and the device it ran on ("cuda" or "cpu"), for information only: neither decides whether this transcript is current.</summary>
    public long? ModelBytes { get; init; }

    public string? Device { get; init; }

    /// <summary>Quality flags for readers, for example "silenced-frames" when more than the allowed fraction of frames could not be decoded. Null when there are none.</summary>
    public IReadOnlyList<string>? Flags { get; init; }

    public DateTimeOffset ToUtc(double audioOffset) => SegmentTimeline.ToUtc(Timeline, audioOffset);

    [JsonIgnore]
    public DateTimeOffset AudioFromUtc => ToUtc(0);

    [JsonIgnore]
    public DateTimeOffset AudioToUtc => ToUtc(AudioSeconds);
}
