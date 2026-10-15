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

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            _timer = new Timer(_ => InvokeAsync(RefreshAsync), null, TimeSpan.FromSeconds(RefreshSeconds), TimeSpan.FromSeconds(RefreshSeconds));
    }

    async Task RefreshAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    async Task LoadAsync()
    {
        Statuses = await StatusService.GetAsync(CancellationToken.None);
        Checked = Time.GetUtcNow();
    }

    public void Dispose() => _timer?.Dispose();

    static string Utc(DateTimeOffset? t) => t is null ? "-" : ClipPlanner.Utc(t.Value);

    static string YesNo(bool? b) => b is null ? "-" : b.Value ? "yes" : "no";

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
