using System.Text.Json;

namespace WebAudioArchive.Core.Recording;

public sealed record SegmentFormat(string Codec, int BitrateKbps, int SampleRateHz, int Channels);

public sealed record SegmentStream(string Url, string HowFound, string? IcyName, string? IcyGenre, int IcyMetaInt);

public sealed record SegmentTitle(string Title, long AtFileByte, DateTimeOffset Utc);

public sealed record SegmentGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Seconds, string Reason);

/// <summary>One run of frames in a merged hour: taken from one recorder's file, between FromUtc and ToUtc.</summary>
/// <param name="File">Relative to that recorder's station master folder, for example 2026/10-16/140000.mp3.</param>
public sealed record MergeSource(string Recorder, string File, int FirstFrame, int FrameCount, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>A source file used by a merge, with its checksum.</summary>
public sealed record MergeInput(string Recorder, string File, string Sha256, long Bytes);

/// <summary>How a merged hour was built from the A and B recordings. Sha256 is the checksum of the merged audio.</summary>
public sealed record MergeInfo(
    IReadOnlyList<MergeSource> Sources,
    IReadOnlyList<MergeInput> Inputs,
    string Sha256,
    int FramesFromA,
    int FramesFromB);

/// <summary>
/// Sidecar for one master segment. EndedBy is hour-boundary, shutdown or crash. A merged hour has Instance "merged",
/// Gaps for what neither recorder has, and a Merge block; other sidecars have no Merge block.
/// </summary>
public sealed record SegmentMeta(
    int Schema,
    string Instance,
    string Station,
    string File,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Bytes,
    int Frames,
    double AudioSeconds,
    SegmentFormat? Format,
    SegmentStream? Stream,
    IReadOnlyList<SegmentTitle> Titles,
    IReadOnlyList<SegmentGap> Gaps,
    string EndedBy,
    MergeInfo? Merge = null);

public static class SegmentMetaStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Written to a temporary file and renamed, so a reader never sees half a file.</summary>
    public static void Write(string jsonPath, SegmentMeta meta)
    {
        var tmp = jsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, Options));
        File.Move(tmp, jsonPath, overwrite: true);
    }

    public static SegmentMeta? Read(string jsonPath) =>
        File.Exists(jsonPath) ? JsonSerializer.Deserialize<SegmentMeta>(File.ReadAllText(jsonPath), Options) : null;
}
