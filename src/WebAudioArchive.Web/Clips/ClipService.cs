using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Web.Clips;

/// <summary>A prepared clip: the cached file plus the plan it came from.</summary>
public sealed record PreparedClip(string Station, ClipFile File, ClipPlan Plan)
{
    public string Url => $"/media/clip/{File.Id}.mp3";
    public string DownloadUrl => Url + "?download=1";

    /// <summary>Where each piece starts in the clip (seconds of audio) and the UTC wall time there, for the player.</summary>
    public IReadOnlyList<(double ClipSecond, DateTimeOffset Utc)> Timeline()
    {
        var list = new List<(double, DateTimeOffset)>();
        double at = 0;
        foreach (var p in Plan.Pieces)
        {
            var bps = (double)p.Segment.BytesPerSecond;
            list.Add((at, p.StartUtc));
            // The audio jumps at a gap inside a segment, so the wall time does too.
            foreach (var g in p.Segment.SpanGaps.Where(g => g.StartUtc >= p.StartUtc && g.EndUtc <= p.EndUtc))
                list.Add((at + (PositionModel.Offset(p.Segment, g.EndUtc) - p.StartByte) / bps, g.EndUtc));
            at += (p.EndByte - p.StartByte) / bps;
        }

        return list;
    }

    public ClipClock Clock() => new(Timeline(), File.Seconds);

    public IReadOnlyList<ClipGap> Gaps() => ClipEvents.Gaps(Plan);

    public IReadOnlyList<ClipRestart> Restarts() => ClipEvents.Restarts(Plan);
}

/// <summary>
/// The one way into clip making, used by both the API and the Player page. Station and recorder are checked against
/// the configuration and nothing the user typed is ever used as a path.
/// </summary>
public sealed class ClipService(WebSettings settings, SegmentCatalog catalog, TimeProvider time, ILogger<ClipService> log)
{
    readonly ClipBuilder _builder = new(settings.CacheDir, log);

    public PreparedClip Prepare(string? station, ClipRequest request)
    {
        var plan = Plan(station, request);
        var file = _builder.Build(settings.Station, plan);
        return new PreparedClip(settings.Station, file, plan);
    }

    /// <summary>
    /// The clip for this request when it is already in the cache, without building anything (planning only reads the
    /// catalog), so a page can show it in the same render rather than drawing a busy state first and the player after.
    /// Null when the clip would have to be built; then call <see cref="Prepare"/> off the render thread. Invalid requests
    /// throw <see cref="ClipException"/> as <see cref="Prepare"/> does.
    /// </summary>
    public PreparedClip? TryPrepareCached(string? station, ClipRequest request)
    {
        var plan = Plan(station, request);
        return _builder.TryReuse(settings.Station, plan) is { } file ? new PreparedClip(settings.Station, file, plan) : null;
    }

    ClipPlan Plan(string? station, ClipRequest request)
    {
        if (station is not null && station != settings.Station)
            throw new ClipException(400, $"Unknown station '{station}'. This app serves '{settings.Station}'.");
        if (request.Recorder != "auto" && settings.Find(request.Recorder) is null)
            throw new ClipException(400, $"Unknown recorder '{request.Recorder}'. Use auto or one of: {string.Join(", ", settings.Recorders.Select(r => r.Id))}.");

        return ClipPlanner.Plan(request, catalog.All(), time.GetUtcNow(), ClipPlanner.DefaultMaxClip);
    }

    /// <summary>Parses the API's query values. Times are ISO 8601; a time without an offset is taken as UTC. Margins are minutes.</summary>
    public static ClipRequest ParseQuery(string? from, string? to, string? marginBefore, string? marginAfter, string? recorder)
    {
        var f = ParseUtc("from", from);
        var t = ParseUtc("to", to);
        var mb = ParseMinutes("marginBefore", marginBefore);
        var ma = ParseMinutes("marginAfter", marginAfter);
        var r = string.IsNullOrWhiteSpace(recorder) ? "auto" : recorder.Trim().ToLowerInvariant();
        if (r != "auto" && !WebSettings.IdPattern().IsMatch(r))
            throw new ClipException(400, $"recorder: '{recorder}' is not auto or a recorder id.");
        return new ClipRequest(f, t, mb, ma, r);
    }

    public static DateTimeOffset ParseUtc(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ClipException(400, $"{name} is required, as a UTC time such as 2026-10-15T15:15:00Z.");
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            throw new ClipException(400, $"{name}: '{value}' is not a time. Use UTC such as 2026-10-15T15:15:00Z.");
        return t.ToUniversalTime();
    }

    static TimeSpan ParseMinutes(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.Zero;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || double.IsNaN(m) || m < 0 || m > ClipPlanner.MaxMargin.TotalMinutes)
            throw new ClipException(400, $"{name}: '{value}' must be a number of minutes from 0 to {ClipPlanner.MaxMargin.TotalMinutes:0}.");
        return TimeSpan.FromMinutes(m);
    }
}
