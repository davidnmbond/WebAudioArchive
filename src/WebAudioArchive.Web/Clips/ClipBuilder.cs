using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Web.Clips;

public sealed record ClipFile(string Id, string FilePath, long Bytes, double Seconds, string DownloadName, bool Reused);

/// <summary>What is kept beside a cached clip, so the media endpoint can name the download.</summary>
public sealed record ClipInfo(string DownloadName, double Seconds, long Bytes);

/// <summary>
/// Joins a plan's pieces into one MP3 in the cache directory. The file is written to a temporary name, checked to be
/// whole frames from end to end, then renamed into place, so a reader never sees half a clip. The name is a hash of the
/// station, recorder and exact piece list, including each file's size and last write time, so a .part that has grown
/// gives a new clip while an unchanged request reuses the cached one.
/// </summary>
public sealed class ClipBuilder(string cacheDir, ILogger log)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string IdFor(string station, ClipPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(station).Append('\n').Append(plan.Recorder).Append('\n');
        sb.Append(plan.WindowFrom.UtcTicks).Append('\n').Append(plan.WindowTo.UtcTicks).Append('\n');
        foreach (var p in plan.Pieces)
        {
            sb.Append(p.Segment.AudioPath).Append('|').Append(p.Segment.FileLength).Append('|')
              .Append(p.Segment.LastWriteUtc.Ticks).Append('|').Append(p.StartByte).Append('|').Append(p.EndByte).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    public static string DownloadName(string station, ClipPlan plan) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{station}_{plan.WindowFrom.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{plan.WindowTo.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{plan.Recorder.ToUpperInvariant()}.mp3");

    public static string PathFor(string cacheDir, string id) => Path.Combine(cacheDir, id + ".mp3");

    public static ClipInfo? ReadInfo(string cacheDir, string id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClipInfo>(File.ReadAllText(Path.Combine(cacheDir, id + ".json")), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ClipFile Build(string station, ClipPlan plan)
    {
        Directory.CreateDirectory(cacheDir);
        var id = IdFor(station, plan);
        var final = PathFor(cacheDir, id);
        var name = DownloadName(station, plan);

        var known = ReadInfo(cacheDir, id);
        if (known is not null && File.Exists(final))
        {
            // Touch it so housekeeping, which removes the oldest first, keeps a clip that is in use.
            try { File.SetLastWriteTimeUtc(final, DateTime.UtcNow); }
            catch (IOException) { }
            return new ClipFile(id, final, known.Bytes, known.Seconds, known.DownloadName, Reused: true);
        }

        var tmp = Path.Combine(cacheDir, $"{id}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                foreach (var piece in plan.Pieces)
                {
                    using var input = new FileStream(piece.Segment.AudioPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                    input.Position = piece.StartByte;
                    CopyExactly(input, output, piece.EndByte - piece.StartByte, piece.Segment.AudioPath);
                }

                output.Flush();
                var walk = Mp3Walker.Walk(output);
                var info = new ClipInfo(name, walk.Seconds, walk.Bytes);
                output.Close();

                File.Move(tmp, final, overwrite: true);
                var infoTmp = tmp + ".json";
                File.WriteAllText(infoTmp, JsonSerializer.Serialize(info, Json));
                File.Move(infoTmp, Path.Combine(cacheDir, id + ".json"), overwrite: true);
                log.LogInformation("Built clip {Id}: {Pieces} pieces, {Bytes} bytes, {Seconds:0.0} s from recorder {Recorder}",
                    id, plan.Pieces.Count, info.Bytes, info.Seconds, plan.Recorder);
                return new ClipFile(id, final, info.Bytes, info.Seconds, name, Reused: false);
            }
        }
        catch (ClipIntegrityException ex)
        {
            log.LogError("Clip {Id} is not whole MP3 frames and was discarded: {Error}", id, ex.Message);
            throw;
        }
        finally
        {
            TryDelete(tmp);
            TryDelete(tmp + ".json");
        }
    }

    static void CopyExactly(Stream input, Stream output, long count, string path)
    {
        var buf = new byte[1 << 16];
        while (count > 0)
        {
            var n = input.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n == 0) throw new ClipIntegrityException($"{path} ended {count} bytes early");
            output.Write(buf, 0, n);
            count -= n;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Cache housekeeping: drop clips older than the maximum age, then the oldest until under the size cap.</summary>
public static class ClipCache
{
    /// <summary>Returns how many clips were removed. Only files in the cache directory itself are touched.</summary>
    public static int Housekeep(string cacheDir, DateTimeOffset now, TimeSpan maxAge, long maxBytes, ILogger log)
    {
        if (!Directory.Exists(cacheDir)) return 0;
        var dir = new DirectoryInfo(cacheDir);

        foreach (var tmp in dir.EnumerateFiles("*.tmp*"))
        {
            if (now.UtcDateTime - tmp.LastWriteTimeUtc > TimeSpan.FromHours(1)) Delete(tmp, log);
        }

        var clips = dir.EnumerateFiles("*.mp3")
            .Where(f => ClipBuilder.IsValidId(Path.GetFileNameWithoutExtension(f.Name)))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        var removed = 0;
        var total = clips.Sum(f => f.Length);
        foreach (var clip in clips)
        {
            var old = now.UtcDateTime - clip.LastWriteTimeUtc > maxAge;
            if (!old && total <= maxBytes) continue;
            total -= clip.Length;
            Delete(clip, log);
            Delete(new FileInfo(Path.ChangeExtension(clip.FullName, ".json")), log);
            removed++;
        }

        foreach (var info in dir.EnumerateFiles("*.json"))
        {
            if (!File.Exists(Path.ChangeExtension(info.FullName, ".mp3"))) Delete(info, log);
        }

        if (removed > 0) log.LogInformation("Cache housekeeping removed {Count} clips; {Bytes} bytes remain", removed, total);
        return removed;
    }

    static void Delete(FileInfo f, ILogger log)
    {
        try
        {
            if (f.Exists) f.Delete();
        }
        catch (IOException ex)
        {
            log.LogWarning("Could not remove {File} from the cache: {Error}", f.FullName, ex.Message);
        }
    }
}
