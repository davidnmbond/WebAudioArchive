using System.Globalization;

namespace WebAudioArchive.Core.Recording;

public sealed record Coverage(double CoveredSeconds, double Fraction);

public static class CoverageReader
{
    /// <summary>
    /// A day folder is skipped when it ends this long before the window starts. Segments roll on the hour, so a
    /// segment filed under day D ends by the start of D+1; the extra day is a safety margin.
    /// </summary>
    static readonly TimeSpan DayFolderMargin = TimeSpan.FromDays(1);

    /// <summary>Seconds of audio in the window ending at <paramref name="now"/>, from segment sidecars: each segment's span clipped to the window, minus gaps that fall inside that span.</summary>
    public static Coverage Read(string masterDir, DateTimeOffset now, TimeSpan window) => Read(masterDir, now, [window])[0];

    /// <summary>Coverage for several windows ending at <paramref name="now"/>, reading each sidecar once. Results are in the order of <paramref name="windows"/>.</summary>
    public static IReadOnlyList<Coverage> Read(string masterDir, DateTimeOffset now, IReadOnlyList<TimeSpan> windows)
    {
        var covered = new double[windows.Count];
        if (windows.Count > 0 && Directory.Exists(masterDir))
        {
            var earliest = now - windows.Max();
            foreach (var path in SidecarsSince(masterDir, earliest))
            {
                SegmentMeta? meta;
                try { meta = SegmentMetaStore.Read(path); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { continue; }
                if (meta is null) continue;

                for (var i = 0; i < windows.Count; i++)
                    covered[i] += SecondsInside(meta, now - windows[i], now);
            }
        }

        return [.. windows.Select((w, i) => new Coverage(covered[i], Math.Min(1, covered[i] / w.TotalSeconds)))];
    }

    /// <summary>Sidecars under yyyy/MM-dd folders that could overlap a window starting at <paramref name="from"/>. A folder whose name is not a date is read in full.</summary>
    static IEnumerable<string> SidecarsSince(string masterDir, DateTimeOffset from)
    {
        foreach (var yearDir in Directory.EnumerateDirectories(masterDir))
        {
            foreach (var dayDir in Directory.EnumerateDirectories(yearDir))
            {
                if (DateTime.TryParseExact($"{Path.GetFileName(yearDir)}-{Path.GetFileName(dayDir)}", "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)
                    && new DateTimeOffset(day, TimeSpan.Zero).AddDays(1) + DayFolderMargin <= from)
                    continue;

                foreach (var path in Directory.EnumerateFiles(dayDir, "*.json", SearchOption.AllDirectories)) yield return path;
            }

            foreach (var path in Directory.EnumerateFiles(yearDir, "*.json")) yield return path;
        }

        foreach (var path in Directory.EnumerateFiles(masterDir, "*.json")) yield return path;
    }

    static double SecondsInside(SegmentMeta meta, DateTimeOffset from, DateTimeOffset now)
    {
        var start = Max(meta.StartUtc, from);
        var end = Min(meta.EndUtc, now);
        if (end <= start) return 0;

        var seconds = (end - start).TotalSeconds;
        foreach (var gap in meta.Gaps ?? [])
        {
            var gs = Max(gap.StartUtc, start);
            var ge = Min(gap.EndUtc, end);
            if (ge > gs) seconds -= (ge - gs).TotalSeconds;
        }

        return Math.Max(0, seconds);
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
