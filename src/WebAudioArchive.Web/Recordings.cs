using System.Globalization;
using PanoramicData.Blazor.Interfaces;
using PanoramicData.Blazor.Models;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web;

/// <summary>One row of the Recordings table. Times are UTC (DateTime with Kind Utc).</summary>
public sealed class RecordingRow
{
    public required string Key { get; init; }
    public required string Recorder { get; init; }
    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }
    public required double AudioSeconds { get; init; }
    public required string Duration { get; init; }
    public required double SizeMb { get; init; }
    public required string EndedBy { get; init; }
    public required int Gaps { get; init; }
    public required string Open { get; init; }
    public required string PlayerUrl { get; init; }
}

/// <summary>A segment or a recording gap on the Recordings calendar. Times UTC.</summary>
public sealed record RecordingCalendarItem(
    string Key, string Recorder, string Kind, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Title, string Tooltip, string CssClass, string PlayerUrl);

/// <summary>All segments of every recorder, newest first, optionally limited to one UTC date.</summary>
public sealed class RecordingsQuery(SegmentCatalog catalog)
{
    public IReadOnlyList<RecordingRow> Rows(DateOnly? utcDate)
    {
        var rows = catalog.All()
            .SelectMany(r => r.Segments)
            .Where(s => utcDate is null || Overlaps(s, utcDate.Value))
            .OrderByDescending(s => s.StartUtc)
            .ThenBy(s => s.Recorder)
            .Select(ToRow)
            .ToList();
        return rows;
    }

    /// <summary>
    /// The Recordings calendar's items overlapping [from, to): every segment of every recorder, and every recording gap
    /// (a gap in a segment's sidecar, or missing audio between one segment and the next of the same recorder).
    /// </summary>
    public IReadOnlyList<RecordingCalendarItem> CalendarItems(DateTimeOffset from, DateTimeOffset to)
    {
        var items = new List<RecordingCalendarItem>();
        foreach (var r in catalog.All())
        {
            var label = r.Recorder.ToUpperInvariant();
            CatalogSegment? previous = null;
            foreach (var s in r.Segments)
            {
                if (s.EndUtc > from && s.StartUtc < to)
                {
                    var title = s.IsOpen ? $"{label}: recording" : $"{label}: segment";
                    items.Add(new RecordingCalendarItem(
                        r.Recorder + "|" + s.AudioPath, label, "segment", s.StartUtc, s.EndUtc, title,
                        $"Recorder {label}, {ClipPlanner.Utc(s.StartUtc)} to {ClipPlanner.Utc(s.EndUtc)}{(s.IsOpen ? " (being recorded)" : $", ended by {s.EndedBy}")}, {s.SpanGaps.Count} gaps",
                        $"rec-item rec-{r.Recorder}{(s.IsOpen ? " rec-open" : "")}",
                        PlayerLink.For(s.StartUtc, s.EndUtc, r.Recorder)));
                }

                foreach (var g in s.SpanGaps.Where(g => g.EndUtc > from && g.StartUtc < to))
                    items.Add(Gap(r.Recorder, g.StartUtc, g.EndUtc, g.Reason));

                if (previous is not null)
                {
                    var missingFrom = previous.EndUtc;
                    var missingTo = PositionModel.AudioStartUtc(s);
                    if ((missingTo - missingFrom).TotalSeconds > ClipEvents.MinGapSeconds && missingTo > from && missingFrom < to)
                        items.Add(Gap(r.Recorder, missingFrom, missingTo, "not recorded"));
                }

                previous = s;
            }
        }

        return items.OrderBy(i => i.StartUtc).ThenBy(i => i.Recorder, StringComparer.Ordinal).ToList();
    }

    static RecordingCalendarItem Gap(string recorder, DateTimeOffset start, DateTimeOffset end, string reason)
    {
        var label = recorder.ToUpperInvariant();
        var seconds = (end - start).TotalSeconds;
        return new RecordingCalendarItem(
            $"{recorder}|gap|{start.UtcTicks}", label, "gap", start, end,
            string.Create(CultureInfo.InvariantCulture, $"{label}: gap {seconds:0.0} s"),
            string.Create(CultureInfo.InvariantCulture, $"Recorder {label} has no audio from {ClipPlanner.Utc(start)} to {ClipPlanner.Utc(end)} ({seconds:0.0} s, {reason})"),
            $"rec-item rec-gap rec-{recorder}",
            // A minute either side of the gap; for a gap of hours, the minute either side of where it starts (a clip is at most 4 hours).
            PlayerLink.For(start.AddMinutes(-1), end - start > TimeSpan.FromHours(3) ? start.AddMinutes(1) : end.AddMinutes(1), recorder));
    }

    static bool Overlaps(CatalogSegment s, DateOnly date)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return s.StartUtc < dayStart.AddDays(1) && s.EndUtc >= dayStart;
    }

    public static RecordingRow ToRow(CatalogSegment s) => new()
    {
        Key = s.Recorder + "|" + s.AudioPath,
        Recorder = s.Recorder.ToUpperInvariant(),
        StartUtc = s.StartUtc.UtcDateTime,
        EndUtc = s.EndUtc.UtcDateTime,
        AudioSeconds = Math.Round(s.AudioSeconds, 1),
        Duration = TimeSpan.FromSeconds(Math.Round(s.AudioSeconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        SizeMb = Math.Round(s.Bytes / 1_000_000.0, 2),
        EndedBy = s.EndedBy ?? "(recording)",
        Gaps = s.SpanGaps.Count,
        Open = s.IsOpen ? "yes" : "no",
        PlayerUrl = PlayerLink.For(s.StartUtc, s.EndUtc, s.Recorder),
    };
}

/// <summary>Feeds PDTable: sorting and paging over rows prepared by the page.</summary>
public sealed class RecordingsDataProvider : IDataProviderService<RecordingRow>
{
    public IReadOnlyList<RecordingRow> Rows { get; set; } = [];

    public Task<DataResponse<RecordingRow>> GetDataAsync(DataRequest<RecordingRow> request, CancellationToken cancellationToken)
    {
        var rows = Rows;
        var query = rows.AsQueryable();
        if (request.SortFieldExpression is not null && request.SortDirection != SortDirection.None)
        {
            query = request.SortDirection == SortDirection.Descending
                ? query.OrderByDescending(request.SortFieldExpression)
                : query.OrderBy(request.SortFieldExpression);
        }

        var total = rows.Count;
        if (request.Skip is > 0) query = query.Skip(request.Skip.Value);
        if (request.Take is > 0) query = query.Take(request.Take.Value);
        return Task.FromResult(new DataResponse<RecordingRow>(query.ToList(), total));
    }

    public Task<OperationResponse> DeleteAsync(RecordingRow item, CancellationToken cancellationToken) => ReadOnly();

    public Task<OperationResponse> UpdateAsync(RecordingRow item, IDictionary<string, object?> delta, CancellationToken cancellationToken) => ReadOnly();

    public Task<OperationResponse> CreateAsync(RecordingRow item, CancellationToken cancellationToken) => ReadOnly();

    static Task<OperationResponse> ReadOnly() => Task.FromResult(new OperationResponse { ErrorMessage = "Recordings are read-only." });
}

public static class PlayerLink
{
    public static string For(DateTimeOffset from, DateTimeOffset to, string recorder) =>
        string.Create(CultureInfo.InvariantCulture,
            $"/player?from={Iso(from)}&to={Iso(to)}&recorder={Uri.EscapeDataString(recorder)}");

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
