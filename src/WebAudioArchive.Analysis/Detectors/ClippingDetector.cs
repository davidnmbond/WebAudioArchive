using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record ClipEvent(double StartSeconds, double EndSeconds, int Runs, long ClippedSamples, float Peak, IReadOnlyList<int> Channels)
{
    public double DurationMs => (EndSeconds - StartSeconds) * 1000;
}

/// <summary>Events, whether the level's run list was truncated (then counts are a floor), and how many runs the level saw.</summary>
public sealed record ClippingResult(IReadOnlyList<ClipEvent> Events, bool Truncated, long RunCount);

/// <summary>
/// Spec 3.6: an event is a run of at least MinRunSamples consecutive samples at or above Threshold of full scale on a
/// channel; runs on any channel less than MergeWithinMs apart are one event. Provisional thresholds (Task 18).
/// </summary>
public static class ClippingDetector
{
    public const string Name = "clipping";
    public const string Version = "1";

    public static ClippingResult Detect(AudioFeatures f, ClippingOptions o)
    {
        var level = f.Clips.FirstOrDefault(c => Math.Abs(c.Level - o.Threshold) < 1e-9)
            ?? throw new InvalidOperationException($"features.json has no clip runs at level {o.Threshold}; decode again with that level in ClipLevels");
        var rate = (double)f.SampleRate;
        var merge = o.MergeWithinMs / 1000.0;
        var events = new List<ClipEvent>();

        double start = 0, end = 0;
        var runs = 0;
        long samples = 0;
        float peak = 0;
        var channels = new SortedSet<int>();
        foreach (var r in level.Runs.Where(r => r.Length >= o.MinRunSamples).OrderBy(r => r.StartFrame))
        {
            var rs = r.StartFrame / rate;
            var re = (r.StartFrame + r.Length) / rate;
            if (runs > 0 && rs - end <= merge)
            {
                end = Math.Max(end, re);
                runs++;
                samples += r.Length;
                peak = Math.Max(peak, r.Peak);
                channels.Add(r.Channel);
                continue;
            }

            if (runs > 0) events.Add(new ClipEvent(start, end, runs, samples, peak, channels.ToList()));
            (start, end, runs, samples, peak) = (rs, re, 1, r.Length, r.Peak);
            channels = [r.Channel];
        }

        if (runs > 0) events.Add(new ClipEvent(start, end, runs, samples, peak, channels.ToList()));
        return new ClippingResult(events, level.Truncated, level.Count);
    }

    public static string Severity(ClipEvent e, ClippingOptions o) => e.DurationMs >= o.MediumMs ? Severities.Medium : Severities.Low;
}
