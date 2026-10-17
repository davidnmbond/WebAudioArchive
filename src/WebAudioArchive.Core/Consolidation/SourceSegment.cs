using System.Text.Json;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>
/// One closed master segment of a recorder, as the consolidator reads it: its audio file under the recorder's station
/// master folder (&lt;root&gt;/&lt;station&gt;/master) and its sidecar.
/// </summary>
public sealed record SourceSegment(string Recorder, string StationMasterDir, string AudioPath, SegmentMeta Meta)
{
    /// <summary>The audio file relative to <see cref="StationMasterDir"/>, with forward slashes: 2026/10-16/140000.mp3.</summary>
    public string RelativeFile => Path.GetRelativePath(StationMasterDir, AudioPath).Replace('\\', '/');

    /// <summary>The file name without .mp3: HHmmss, or HHmmss-n after a restart.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(AudioPath);

    /// <summary>Core's position model for this segment. Worked out on each call so a copy made with <c>with</c> never keeps stale anchors.</summary>
    public IReadOnlyList<TimelineAnchor> Anchors => SegmentTimeline.Anchors(Meta);

    public int BytesPerSecond => (Meta.Format?.BitrateKbps is > 0 and var kbps ? kbps : DefaultBitrateKbps) * 125;

    const int DefaultBitrateKbps = 128;

    /// <summary>
    /// Every closed segment under &lt;recorderRoot&gt;/&lt;station&gt;/master (an .mp3 with a readable sidecar naming it), in
    /// start order. Open .part files and sidecars that cannot be read are left out. A sidecar without a format gets one
    /// from the audio's first frame header. Read-only.
    /// </summary>
    public static IReadOnlyList<SourceSegment> Scan(string recorderRoot, string station, string recorder)
    {
        var master = Path.Combine(recorderRoot, station, "master");
        var list = new List<SourceSegment>();
        if (!Directory.Exists(master)) return list;

        foreach (var json in Directory.EnumerateFiles(master, "*.json", SearchOption.AllDirectories))
        {
            if (!json.EndsWith(".json", StringComparison.Ordinal)) continue;
            var seg = TryRead(json, master, recorder);
            if (seg is not null) list.Add(seg);
        }

        list.Sort((x, y) =>
        {
            var byStart = x.Meta.StartUtc.CompareTo(y.Meta.StartUtc);
            return byStart != 0 ? byStart : string.CompareOrdinal(x.AudioPath, y.AudioPath);
        });
        return list;
    }

    static SourceSegment? TryRead(string json, string master, string recorder)
    {
        SegmentMeta? meta;
        try
        {
            meta = SegmentMetaStore.Read(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }

        // The sidecar names its audio file; only a plain .mp3 name in the same folder is accepted.
        if (meta is null || string.IsNullOrEmpty(meta.File) || meta.StartUtc == default || meta.EndUtc < meta.StartUtc || meta.Bytes < 0
            || Path.GetFileName(meta.File) != meta.File || !meta.File.EndsWith(".mp3", StringComparison.Ordinal))
            return null;

        var audio = Path.Combine(Path.GetDirectoryName(json)!, meta.File);
        if (!File.Exists(audio)) return null;

        if (meta.Format is null && ReadFormat(audio) is { } format) meta = meta with { Format = format };
        return new SourceSegment(recorder, master, audio, meta);
    }

    static SegmentFormat? ReadFormat(string audio)
    {
        try
        {
            using var fs = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[Mp3FrameHeader.HeaderSize];
            return fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && Mp3FrameHeader.TryParse(head, out var h)
                ? new SegmentFormat(h.Codec, h.BitrateKbps, h.SampleRateHz, h.Channels)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
