using System.Text.Json;

namespace WebAudioArchive.Core.Recording;

public sealed record SegmentFormat(string Codec, int BitrateKbps, int SampleRateHz, int Channels);

public sealed record SegmentStream(string Url, string HowFound, string? IcyName, string? IcyGenre, int IcyMetaInt);

public sealed record SegmentTitle(string Title, long AtFileByte, DateTimeOffset Utc);

public sealed record SegmentGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Seconds, string Reason);

/// <summary>Sidecar for one master segment. EndedBy is hour-boundary, shutdown or crash.</summary>
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
    string EndedBy);

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
