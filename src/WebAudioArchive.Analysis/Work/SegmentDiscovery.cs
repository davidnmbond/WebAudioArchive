using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Work;

/// <summary>A closed master segment: recorder, sidecar, audio, the job id it is analysed under (recorder/yyyy/MM-dd/stem) and its sidecar.</summary>
public sealed record SourceSegment(string Recorder, string SidecarPath, string AudioPath, string JobId, SegmentMeta Meta)
{
    /// <summary>Changes whenever the recorder rewrites the segment (crash recovery), so the job runs again.</summary>
    public string Fingerprint => string.Create(CultureInfo.InvariantCulture, $"{Meta.Bytes}:{Meta.Frames}:{Meta.EndUtc.UtcTicks}:{Meta.EndedBy}");

    public int SampleRate => Meta.Format?.SampleRateHz ?? 44100;

    public int Channels => Meta.Format?.Channels ?? 2;
}

/// <summary>
/// Finds closed segments (a sidecar beside its .mp3, sizes agreeing) under each recorder's read-only root. Open .part
/// files, temporary sidecars and segments of another station are ignored. Parsed sidecars are cached by path, size and
/// write time, so a pass over a year of hourly segments reads only what changed.
/// </summary>
public sealed class SegmentDiscovery(IReadOnlyList<RecorderRoot> recorders, string station, ILogger log)
{
    readonly ConcurrentDictionary<string, (long Length, DateTime Written, SegmentMeta? Meta, string? Problem)> _cache = new();

    public IReadOnlyList<SourceSegment> Scan(ICollection<string>? problems = null)
    {
        var list = new List<SourceSegment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in recorders)
        {
            var master = Path.Combine(r.Root, station, "master");
            if (!Directory.Exists(master)) continue;
            foreach (var json in Directory.EnumerateFiles(master, "*.json", SearchOption.AllDirectories))
            {
                if (!json.EndsWith(".json", StringComparison.Ordinal)) continue;
                seen.Add(json);
                var segment = Read(r.Id, master, json, out var problem);
                if (segment is not null)
                {
                    list.Add(segment);
                }
                else if (problem is not null)
                {
                    problems?.Add(problem);
                    log.LogDebug("Not analysed: {Problem}", problem);
                }
            }
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.TryRemove(stale, out _);
        return list.OrderByDescending(s => s.Meta.StartUtc).ThenBy(s => s.Recorder, StringComparer.Ordinal).ToList();
    }

    SourceSegment? Read(string recorder, string master, string json, out string? problem)
    {
        SegmentMeta? meta;
        try
        {
            var info = new FileInfo(json);
            var jsonLength = info.Length;
            var written = info.LastWriteTimeUtc;
            if (_cache.TryGetValue(json, out var hit) && hit.Length == jsonLength && hit.Written == written)
            {
                meta = hit.Meta;
                problem = hit.Problem;
            }
            else
            {
                (meta, problem) = Parse(json);
                _cache[json] = (jsonLength, written, meta, problem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted or locked between listing the folder and reading it: skipped this pass, found again on the next.
            problem = $"{json}: not readable now ({ex.Message})";
            return null;
        }

        if (meta is null) return null;
        var audio = Path.Combine(Path.GetDirectoryName(json)!, meta.File);
        long length;
        try
        {
            length = new FileInfo(audio).Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            problem = $"{json}: its audio {meta.File} is missing";
            return null;
        }

        if (length != meta.Bytes)
        {
            problem = $"{json}: {meta.File} is {length} bytes but the sidecar says {meta.Bytes}; skipped until they agree";
            return null;
        }

        var relative = Path.GetRelativePath(master, json).Replace('\\', '/');
        problem = null;
        return new SourceSegment(recorder, json, audio, recorder + "/" + relative[..^".json".Length], meta);
    }

    (SegmentMeta? Meta, string? Problem) Parse(string json)
    {
        SegmentMeta? meta;
        try
        {
            meta = SegmentMetaStore.Read(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, $"{json}: unreadable sidecar ({ex.Message})");
        }

        if (meta is null || string.IsNullOrEmpty(meta.File) || meta.StartUtc == default || meta.EndUtc < meta.StartUtc || meta.Bytes < 0)
            return (null, $"{json}: incomplete sidecar");
        if (Path.GetFileName(meta.File) != meta.File || meta.File.Contains("..", StringComparison.Ordinal))
            return (null, $"{json}: file name {meta.File} is not a plain name");
        if (!string.Equals(meta.Station, station, StringComparison.Ordinal))
            return (null, $"{json}: station '{meta.Station}', not '{station}'");
        return (meta, null);
    }
}
