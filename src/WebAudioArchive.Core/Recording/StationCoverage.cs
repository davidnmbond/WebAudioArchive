namespace WebAudioArchive.Core.Recording;

public sealed record CoverageSnapshot(double Fraction24h, double Fraction30d, DateTimeOffset ComputedUtc);

/// <summary>
/// A station's latest coverage figures. Reading sidecars is slow on some disks (a Docker bind mount on Windows takes
/// hundreds of milliseconds for a day's worth), so it is done in the background and /healthz reports what was found.
/// </summary>
public sealed class StationCoverage
{
    static readonly TimeSpan[] Windows = [TimeSpan.FromHours(24), TimeSpan.FromDays(30)];

    volatile CoverageSnapshot? _latest;

    /// <summary>The most recent figures, or null before the first refresh.</summary>
    public CoverageSnapshot? Latest => _latest;

    public CoverageSnapshot Refresh(string masterDir, DateTimeOffset now)
    {
        var c = CoverageReader.Read(masterDir, now, Windows);
        var snapshot = new CoverageSnapshot(c[0].Fraction, c[1].Fraction, now);
        _latest = snapshot;
        return snapshot;
    }
}
