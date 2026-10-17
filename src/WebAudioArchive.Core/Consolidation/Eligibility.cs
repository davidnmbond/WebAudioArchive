using System.Globalization;
using System.Text.Json;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>Which UTC hours may be merged now: closed for <see cref="MinAge"/> and with analysis finished on their sources.</summary>
public static class Eligibility
{
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    /// <summary>
    /// True when analysis is not required (<paramref name="analysisRoot"/> is null) or the segment's analysis state is done
    /// or failed. A missing or unreadable state file is not settled.
    /// </summary>
    public static bool AnalysisSettled(string? analysisRoot, string station, SourceSegment s)
    {
        if (analysisRoot is null) return true;

        var start = s.Meta.StartUtc.UtcDateTime;
        var jobId = string.Create(CultureInfo.InvariantCulture, $"{s.Recorder}/{start:yyyy}/{start:MM-dd}/{s.Stem}");
        string file;
        try
        {
            file = Path.Combine(new AnalysisPaths(analysisRoot, station).SegmentDir(jobId), AnalysisPaths.StateFile);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is JobStatus.Done or JobStatus.Failed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// UTC hours, oldest first, that are not merged yet, ended at least <see cref="MinAge"/> ago, have every overlapping
    /// source segment closed that long and <paramref name="settled"/>, and that no open .part still reaches into.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Hours(
        IReadOnlyList<SourceSegment> a,
        IReadOnlyList<SourceSegment> b,
        IReadOnlySet<DateTimeOffset> mergedHours,
        DateTimeOffset? earliestOpenStartUtc,
        DateTimeOffset now,
        Func<SourceSegment, bool> settled)
    {
        var all = a.Concat(b).ToList();
        var candidates = new SortedSet<DateTimeOffset>();
        foreach (var s in all)
        {
            var last = s.Meta.EndUtc > s.Meta.StartUtc ? s.Meta.EndUtc.AddTicks(-1) : s.Meta.StartUtc;
            for (var h = FloorHour(s.Meta.StartUtc); h <= FloorHour(last); h = h.AddHours(1)) candidates.Add(h);
        }

        var result = new List<DateTimeOffset>();
        foreach (var hour in candidates)
        {
            var end = hour.AddHours(1);
            if (mergedHours.Contains(hour) || end + MinAge > now) continue;
            if (earliestOpenStartUtc is { } open && open < end) continue;

            var overlapping = all.Where(s => s.Meta.StartUtc < end && s.Meta.EndUtc > hour).ToList();
            if (overlapping.All(s => s.Meta.EndUtc + MinAge <= now && settled(s))) result.Add(hour);
        }

        return result;
    }

    static DateTimeOffset FloorHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
