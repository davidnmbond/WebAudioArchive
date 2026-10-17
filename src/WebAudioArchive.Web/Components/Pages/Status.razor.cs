using System.Globalization;
using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Status;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Status
{
    public const int RefreshSeconds = 15;

    [Inject] public required StatusService StatusService { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }

    IReadOnlyList<RecorderStatus>? Statuses { get; set; }
    DateTimeOffset? Checked { get; set; }
    Timer? _timer;

    string CheckedText => Utc(Checked);

    protected override async Task OnInitializedAsync()
    {
        // Show the last reading at once when there is one: the circuit's first render then matches the prerendered
        // page (and an in-app visit draws the cards straight away), instead of "Reading the recorders..." in between.
        // While prerendering, a stale reading is replaced by a fresh one first, as before.
        var last = StatusService.Last;
        if (last is not null && (RendererInfo.IsInteractive || !IsStale(last, Time.GetUtcNow()))) Show(last);
        else await LoadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender) return;
        var due = Checked is null ? TimeSpan.Zero : DueIn(Checked.Value, Time.GetUtcNow());
        _timer = new Timer(_ => InvokeAsync(RefreshAsync), null, due, TimeSpan.FromSeconds(RefreshSeconds));
    }

    /// <summary>A reading older than one refresh period is stale.</summary>
    public static bool IsStale(StatusSnapshot s, DateTimeOffset now) => now - s.CheckedUtc >= TimeSpan.FromSeconds(RefreshSeconds);

    /// <summary>How long until a reading taken at <paramref name="checkedUtc"/> is due for its refresh (zero when overdue).</summary>
    public static TimeSpan DueIn(DateTimeOffset checkedUtc, DateTimeOffset now)
    {
        var due = TimeSpan.FromSeconds(RefreshSeconds) - (now - checkedUtc);
        return due < TimeSpan.Zero ? TimeSpan.Zero : due;
    }

    async Task RefreshAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    async Task LoadAsync() => Show(await StatusService.ReadAsync(CancellationToken.None));

    void Show(StatusSnapshot s)
    {
        Statuses = s.Statuses;
        Checked = s.CheckedUtc;
    }

    public void Dispose() => _timer?.Dispose();

    static string Utc(DateTimeOffset? t) => t is null ? "-" : ClipPlanner.Utc(t.Value);

    static string YesNo(bool? b) => b is null ? "-" : b.Value ? "yes" : "no";

    static string Uptime(double? s) => DurationText.Humanize(s);

    static string Seconds(double? s) => s is null ? "-" : s.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    static string LastError(RecorderHealth? h) =>
        h is null || !h.Reachable ? "-" : h.LastError is null ? "none" : $"{h.LastError} ({Utc(h.LastErrorUtc)})";

    static string Coverage(RecorderStatus s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Coverage24hFraction:0.0%} ({s.Coverage24hSeconds / 3600:0.00} h in {s.Segments24h} segments)");

    static string Latest(RecorderStatus s) =>
        s.Latest is null ? "none" : $"{Utc(s.Latest.StartUtc)} to {Utc(s.Latest.EndUtc)}{(s.Latest.IsOpen ? " (being recorded)" : "")}";

    static string LevelText(HealthLevel l) => l switch
    {
        HealthLevel.Green => "OK",
        HealthLevel.Amber => "WARNING",
        _ => "PROBLEM",
    };

    static string LevelIcon(HealthLevel l) => l switch
    {
        HealthLevel.Green => "fas fa-check-circle",
        HealthLevel.Amber => "fas fa-exclamation-triangle",
        _ => "fas fa-times-circle",
    };

    static string BadgeClass(HealthLevel l) => l switch
    {
        HealthLevel.Green => "text-bg-success",
        HealthLevel.Amber => "text-bg-warning",
        _ => "text-bg-danger",
    };

    static string BorderClass(HealthLevel l) => l switch
    {
        HealthLevel.Green => "border-success",
        HealthLevel.Amber => "border-warning",
        _ => "border-danger",
    };
}
