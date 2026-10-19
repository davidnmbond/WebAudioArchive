using System.Globalization;
using System.Text.Json;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Core.Consolidation;

/// <summary>Where analysis stands with one source segment, read from its state.json.</summary>
public enum SegmentAnalysis
{
    /// <summary>No state file: analysis has not queued the segment (it queues another recorder's segment only where the preferred one has a hole).</summary>
    NotQueued,

    /// <summary>Waiting or running, or a state file that cannot be read or names another status: analysis may still read it.</summary>
    Pending,

    /// <summary>Done or failed.</summary>
    Finished,
}

/// <summary>Which UTC hours may be merged now: closed for <see cref="MinAge"/> and with analysis finished with them.</summary>
public static class Eligibility
{
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    /// <summary>The segment's analysis state, from &lt;analysis&gt;/&lt;station&gt;/segments/&lt;recorder/yyyy/MM-dd/stem&gt;/state.json.</summary>
    public static SegmentAnalysis AnalysisOf(string analysisRoot, string station, SourceSegment s)
    {
        var start = s.Meta.StartUtc.UtcDateTime;
        var jobId = string.Create(CultureInfo.InvariantCulture, $"{s.Recorder}/{start:yyyy}/{start:MM-dd}/{s.Stem}");
        string file;
        try
        {
            file = Path.Combine(new AnalysisPaths(analysisRoot, station).SegmentDir(jobId), AnalysisPaths.StateFile);
        }
        catch (ArgumentException)
        {
            return SegmentAnalysis.Pending;
        }

        try
        {
            if (!File.Exists(file)) return SegmentAnalysis.NotQueued;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var finished = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is JobStatus.Done or JobStatus.Failed;
            return finished ? SegmentAnalysis.Finished : SegmentAnalysis.Pending;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SegmentAnalysis.NotQueued;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return SegmentAnalysis.Pending;
        }
    }

    /// <summary>
    /// True when analysis is not required (<paramref name="analysisRoot"/> is null), or when no segment overlapping the
    /// hour, of either recorder, is pending and at least one of them is finished. Analysis queues only the preferred
    /// recorder's segments (and the other's where the preferred one has a hole), so a segment it never queued does not
    /// hold the hour back.
    /// </summary>
    public static bool HourSettled(string? analysisRoot, string station, IReadOnlyList<SourceSegment> overlapping)
    {
        if (analysisRoot is null) return true;
        var finished = false;
        foreach (var s in overlapping)
        {
            switch (AnalysisOf(analysisRoot, station, s))
            {
                case SegmentAnalysis.Pending:
                    return false;
                case SegmentAnalysis.Finished:
                    finished = true;
                    break;
            }
        }

        return finished;
    }

    /// <summary>
    /// UTC hours, oldest first, that are not merged yet, ended at least <see cref="MinAge"/> ago, have every overlapping
    /// source segment closed that long, that no open .part still reaches into, and for which <paramref name="settled"/>,
    /// given every source segment of both recorders overlapping the hour, is true.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Hours(
        IReadOnlyList<SourceSegment> a,
        IReadOnlyList<SourceSegment> b,
        IReadOnlySet<DateTimeOffset> mergedHours,
        DateTimeOffset? earliestOpenStartUtc,
        DateTimeOffset now,
        Func<IReadOnlyList<SourceSegment>, bool> settled)
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
            if (overlapping.All(s => s.Meta.EndUtc + MinAge <= now) && settled(overlapping)) result.Add(hour);
        }

        return result;
    }

    static DateTimeOffset FloorHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
