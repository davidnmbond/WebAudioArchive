using System.Globalization;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web;

/// <summary>
/// The Player page's inputs: a UTC date, From and To as HH:mm:ss (UTC), an optional margin either side, and the
/// recorder (auto, a, b). A To earlier than From means the next UTC day, so a window can cross midnight.
/// </summary>
public sealed class PlayerForm
{
    public DateOnly? Date { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool AddMargin { get; set; }
    public int MarginMinutes { get; set; } = 5;
    public string Recorder { get; set; } = "auto";

    static readonly string[] TimeFormats = ["HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm"];

    public ClipRequest ToRequest()
    {
        if (Date is null) throw new ClipException(400, "Choose a UTC date.");
        var from = ParseTime("From", From);
        var to = ParseTime("To", To);
        var day = new DateTimeOffset(Date.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var fromUtc = day + from.ToTimeSpan();
        var toUtc = day + to.ToTimeSpan();
        if (to < from) toUtc = toUtc.AddDays(1);
        if (AddMargin && (MarginMinutes < 0 || MarginMinutes > ClipPlanner.MaxMargin.TotalMinutes))
            throw new ClipException(400, $"The margin must be from 0 to {ClipPlanner.MaxMargin.TotalMinutes:0} minutes.");
        var margin = AddMargin ? TimeSpan.FromMinutes(MarginMinutes) : TimeSpan.Zero;
        var recorder = string.IsNullOrWhiteSpace(Recorder) ? "auto" : Recorder.Trim().ToLowerInvariant();
        return new ClipRequest(fromUtc, toUtc, margin, margin, recorder);
    }

    /// <summary>Fills the form from a deep link (?from=2026-10-15T15:15:00Z&amp;to=...&amp;recorder=auto). Returns false if from or to is missing or unreadable.</summary>
    public bool TryLoad(string? from, string? to, string? recorder, string? marginMinutes)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
        DateTimeOffset f, t;
        try
        {
            f = ClipService.ParseUtc("from", from);
            t = ClipService.ParseUtc("to", to);
        }
        catch (ClipException)
        {
            return false;
        }

        Date = DateOnly.FromDateTime(f.UtcDateTime);
        From = f.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        To = t.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Recorder = string.IsNullOrWhiteSpace(recorder) ? "auto" : recorder.Trim().ToLowerInvariant();
        if (int.TryParse(marginMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0)
        {
            AddMargin = true;
            MarginMinutes = m;
        }

        return true;
    }

    static TimeOnly ParseTime(string name, string value)
    {
        if (TimeOnly.TryParseExact(value?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        throw new ClipException(400, $"{name}: '{value}' is not a UTC time of day as HH:mm:ss.");
    }
}
