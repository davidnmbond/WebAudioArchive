using System.Globalization;
using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Player
{
    [Inject] public required ClipService Clips { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [Inject] public required TimeProvider Time { get; set; }
    [Inject] public required ILogger<Player> Log { get; set; }

    [SupplyParameterFromQuery(Name = "from")] public string? QueryFrom { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? QueryTo { get; set; }
    [SupplyParameterFromQuery(Name = "recorder")] public string? QueryRecorder { get; set; }
    [SupplyParameterFromQuery(Name = "margin")] public string? QueryMargin { get; set; }

    PlayerForm Form { get; } = new();
    PreparedClip? Clip { get; set; }
    string? Error { get; set; }
    bool Busy { get; set; }
    string? _loadedQuery;

    string Duration => TimeSpan.FromSeconds(Math.Round(Clip!.File.Seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                       + string.Create(CultureInfo.InvariantCulture, $" ({Clip.File.Seconds:0.0} s)");

    string Size => string.Create(CultureInfo.InvariantCulture, $"{Clip!.File.Bytes / 1_000_000.0:0.00} MB ({Clip.File.Bytes} bytes)");

    static string Utc(DateTimeOffset t) => ClipPlanner.Utc(t);

    static string Text(ChangeEventArgs e) => e.Value?.ToString() ?? "";

    protected override void OnInitialized()
    {
        Form.MarginMinutes = Settings.DefaultMarginMinutes;
        var now = Time.GetUtcNow();
        Form.Date = DateOnly.FromDateTime(now.UtcDateTime);
    }

    protected override async Task OnParametersSetAsync()
    {
        // A deep link (?from=...&to=...&recorder=auto) fills the form and prepares the clip straight away.
        var query = $"{QueryFrom}|{QueryTo}|{QueryRecorder}|{QueryMargin}";
        if (query == _loadedQuery) return;
        _loadedQuery = query;
        if (Form.TryLoad(QueryFrom, QueryTo, QueryRecorder, QueryMargin)) await PrepareAsync();
    }

    async Task PrepareAsync()
    {
        Busy = true;
        Error = null;
        try
        {
            var request = Form.ToRequest();
            Clip = await Task.Run(() => Clips.Prepare(null, request));
        }
        catch (ClipException ex)
        {
            Clip = null;
            Error = ex.Message;
        }
        catch (ClipIntegrityException ex)
        {
            Clip = null;
            Error = "The clip could not be built as whole MP3 frames: " + ex.Message;
            Log.LogError(ex, "Clip integrity failure");
        }
        finally
        {
            Busy = false;
        }
    }
}
