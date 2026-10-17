using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record DeadAirEvent(double StartSeconds, double EndSeconds, double QuietestDbfs)
{
    public double Seconds => EndSeconds - StartSeconds;
}

/// <summary>
/// Dead air: every 50 ms window (loudest channel) below ThresholdDbfs for at least MinSeconds. Pauses in speech and
/// the gap between songs are far shorter, which is how intentional pauses are excluded; quiet room tone above the
/// threshold is not dead air. A recording gap is not dead air either: the audio is joined across it, so it never
/// reaches the decoder (gaps are reported from the sidecars instead).
/// </summary>
public static class DeadAirDetector
{
    public const string Name = "dead-air";
    public const string Version = "1";

    public static IReadOnlyList<DeadAirEvent> Detect(AudioFeatures f, DeadAirOptions o)
    {
        var events = new List<DeadAirEvent>();
        var w = f.WindowSeconds;
        var start = -1;
        var quietest = 0.0;
        for (var i = 0; i <= f.WindowDbfsTenths.Count; i++)
        {
            var quiet = i < f.WindowDbfsTenths.Count && f.WindowDbfs(i) < o.ThresholdDbfs;
            if (quiet)
            {
                if (start < 0)
                {
                    start = i;
                    quietest = f.WindowDbfs(i);
                }
                else
                {
                    quietest = Math.Min(quietest, f.WindowDbfs(i));
                }

                continue;
            }

            if (start < 0) continue;
            var from = start * w;
            var to = Math.Min(i * w, f.Seconds);
            if (to - from >= o.MinSeconds - 1e-9) events.Add(new DeadAirEvent(from, to, quietest));
            start = -1;
        }

        return events;
    }

    public static string Severity(DeadAirEvent e, DeadAirOptions o) => e.Seconds >= o.SevereSeconds ? Severities.High : Severities.Medium;
}
