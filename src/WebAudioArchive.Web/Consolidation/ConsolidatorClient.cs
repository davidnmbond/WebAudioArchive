using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Web.Consolidation;

/// <summary>One hour the consolidator could not merge, and why.</summary>
public sealed record ConsolidatorFailure(DateTimeOffset HourUtc, string Reason);

/// <summary>What the consolidator's /healthz reports (issue #104).</summary>
public sealed record ConsolidatorHealth(
    string Station,
    string Mode,
    DateTimeOffset? LastMergedHourUtc,
    int BacklogHours,
    int WaitingForAnalysis,
    IReadOnlyList<ConsolidatorFailure> Failures,
    int Held,
    int Ready,
    int HeldFailed,
    long DeletedBytes,
    DateTimeOffset? LastRunUtc,
    string? LastError);

/// <summary>A held original that is ready to delete: recorder id and the file under held/ (yyyy/MM-dd/HHmmss.mp3).</summary>
public sealed partial record HeldItem(string Recorder, string File, long Bytes, DateTimeOffset? HeldUtc, DateTimeOffset? CheckedUtc)
{
    [GeneratedRegex(@"^(\d{4})/(\d{2})-(\d{2})/(\d{2})(\d{2})(\d{2})\.mp3$")]
    private static partial Regex FilePattern();

    /// <summary>The UTC start of the segment, read from its file name; null when the name is not of that form.</summary>
    public DateTimeOffset? StartUtc => FilePattern().Match(File) is { Success: true } m
        ? new DateTimeOffset(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture), TimeSpan.Zero)
        : null;

    public string RecorderLabel => Recorder.ToUpperInvariant();
}

/// <summary>One requested deletion: deleted, or the reason the consolidator refused it.</summary>
public sealed record DeletionResult(string? Recorder, string? File, bool Deleted, string? Reason);

public sealed class ConsolidatorException(string message) : Exception(message);

public static class ByteText
{
    public static string Humanize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{v:0.0} {units[i]}");
    }
}

/// <summary>
/// Reads the consolidator's HTTP API (WAA_CONSOLIDATOR) and sends it confirmed deletions. The consolidator decides
/// whether each deletion is allowed; this only passes on what a manager confirmed.
/// </summary>
public sealed class ConsolidatorClient(WebSettings settings, IHttpClientFactory http)
{
    public const string HttpClientName = "consolidator";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => settings.ConsolidatorUrl is not null;

    /// <summary>The consolidator's health, or null when it is not configured or did not answer.</summary>
    public async Task<ConsolidatorHealth?> HealthAsync(CancellationToken ct) => (await TryHealthAsync(ct)).Health;

    /// <summary>The health, or why there is none.</summary>
    public async Task<(ConsolidatorHealth? Health, string? Error)> TryHealthAsync(CancellationToken ct)
    {
        if (settings.ConsolidatorUrl is null) return (null, "WAA_CONSOLIDATOR is not set.");
        var url = new Uri(settings.ConsolidatorUrl, "/healthz");
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (null, $"{url} answered {(int)response.StatusCode}");
            var health = await response.Content.ReadFromJsonAsync<ConsolidatorHealth>(Json, ct);
            return health is null ? (null, $"{url} sent no health") : (health, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or NotSupportedException)
        {
            return (null, $"{url} did not answer ({ex.Message})");
        }
    }

    /// <summary>The held originals the consolidator says are ready to delete. Throws <see cref="ConsolidatorException"/> when it cannot be read.</summary>
    public async Task<IReadOnlyList<HeldItem>> ReadyAsync(CancellationToken ct) =>
        await SendAsync<List<HeldItem>>(HttpMethod.Get, "/held", null, ct) ?? [];

    /// <summary>Sends the confirmed deletions; each item comes back with its own result.</summary>
    public async Task<IReadOnlyList<DeletionResult>> DeleteAsync(string confirmedBy, IReadOnlyList<HeldItem> items, CancellationToken ct)
    {
        var body = new { confirmedBy, items = items.Select(i => new { recorder = i.Recorder, file = i.File }).ToList() };
        return await SendAsync<List<DeletionResult>>(HttpMethod.Post, "/deletions", body, ct) ?? [];
    }

    async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (settings.ConsolidatorUrl is null) throw new ConsolidatorException("WAA_CONSOLIDATOR is not set.");
        var url = new Uri(settings.ConsolidatorUrl, path);
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ConsolidatorException($"{url} answered {(int)response.StatusCode}");
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or NotSupportedException)
        {
            throw new ConsolidatorException($"{url} did not answer ({ex.Message})");
        }
    }
}

/// <summary>
/// The review page state: which ready originals are chosen, the confirmation step, and the results. Nothing is sent
/// to the consolidator until <see cref="RequestConfirmation"/> and then <see cref="ConfirmAsync"/>, in that order.
/// </summary>
public sealed class ConsolidationReview(ConsolidatorClient client)
{
    readonly HashSet<HeldItem> _selected = [];

    public IReadOnlyList<HeldItem> Items { get; private set; } = [];
    public string? Error { get; private set; }
    public bool Confirming { get; private set; }
    public IReadOnlyList<DeletionResult> Results { get; private set; } = [];

    /// <summary>The chosen items, in list order.</summary>
    public IReadOnlyList<HeldItem> Selected => [.. Items.Where(_selected.Contains)];

    public bool IsSelected(HeldItem item) => _selected.Contains(item);

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Items = await client.ReadyAsync(ct);
            Error = null;
        }
        catch (ConsolidatorException ex)
        {
            Items = [];
            Error = ex.Message;
        }

        _selected.IntersectWith(Items);
    }

    public void Toggle(HeldItem item)
    {
        if (!_selected.Remove(item)) _selected.Add(item);
        Confirming = false; // a changed choice has to be confirmed again
    }

    /// <summary>Asks the manager to confirm; does nothing when nothing is chosen.</summary>
    public void RequestConfirmation() => Confirming = _selected.Count > 0;

    public void Cancel() => Confirming = false;

    /// <summary>Posts the chosen items. Throws when the confirmation step was not taken.</summary>
    public async Task ConfirmAsync(string confirmedBy, CancellationToken ct)
    {
        if (!Confirming || _selected.Count == 0) throw new InvalidOperationException("Deletion has not been confirmed.");
        var chosen = Selected;
        Confirming = false;
        try
        {
            Results = await client.DeleteAsync(confirmedBy, chosen, ct);
            Error = null;
        }
        catch (ConsolidatorException ex)
        {
            Results = [];
            Error = ex.Message;
            return;
        }

        _selected.Clear();
        await LoadAsync(ct);
    }
}
