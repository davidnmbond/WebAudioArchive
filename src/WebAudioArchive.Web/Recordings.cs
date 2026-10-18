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
    public required string PlayUrl { get; init; }
}

/// <summary>A segment or a recording gap on the Recordings calendar. Times UTC.</summary>
public sealed record RecordingCalendarItem(
    string Key, string Recorder, string Kind, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Title, string Tooltip, string CssClass, string PlayUrl);

/// <summary>All segments of every recorder, newest first, optionally limited to one UTC date.</summary>
public sealed class RecordingsQuery(SegmentCatalog catalog, TimeProvider time)
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
    /// The Recordings calendar's items overlapping [from, to): every segment of every recorder, every recording gap
    /// (a gap in a segment's sidecar, or missing audio between one segment and the next of the same recorder), and every
    /// stretch that no recorder has, from the first audio of any recorder up to now.
    /// </summary>
    public IReadOnlyList<RecordingCalendarItem> CalendarItems(DateTimeOffset from, DateTimeOffset to)
    {
        var items = new List<RecordingCalendarItem>();
        var all = catalog.All();
        foreach (var r in all)
        {
            var label = r.Recorder == WebSettings.MergedId ? "M" : r.Recorder.ToUpperInvariant();
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
                        PlayLink.For(s.StartUtc, r.Recorder)));
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

        var first = all.SelectMany(r => r.Segments).Select(PositionModel.AudioStartUtc).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var now = time.GetUtcNow();
        var missingFromBoth = WindowCoverage.Combined(all, first > from ? first : from, now < to ? now : to, ClipEvents.MinGapSeconds, WindowCoverage.LiveEdgeSeconds);
        items.AddRange(missingFromBoth.Missing.Select(m => MissingFromAll(m.Start, m.End)));

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
            PlayLink.For(start, recorder));
    }

    static RecordingCalendarItem MissingFromAll(DateTimeOffset start, DateTimeOffset end)
    {
        var seconds = (end - start).TotalSeconds;
        return new RecordingCalendarItem(
            $"missing|{start.UtcTicks}", "A+B", "missing", start, end,
            string.Create(CultureInfo.InvariantCulture, $"A+B: missing {seconds:0.0} s"),
            string.Create(CultureInfo.InvariantCulture, $"Neither recorder has audio from {ClipPlanner.Utc(start)} to {ClipPlanner.Utc(end)} ({seconds:0.0} s)"),
            "rec-item rec-missing",
            PlayLink.For(start, "auto"));
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
        PlayUrl = PlayLink.For(s.StartUtc, s.Recorder),
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

/// <summary>A link that opens the show on air at a UTC time (or that off-air hour), from a recorder: /at finds the show.</summary>
public static class PlayLink
{
    public static string For(DateTimeOffset at, string recorder) =>
        string.Create(CultureInfo.InvariantCulture, $"/at?utc={Iso(at)}&recorder={Uri.EscapeDataString(recorder)}");

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
