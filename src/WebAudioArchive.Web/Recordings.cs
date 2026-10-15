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
