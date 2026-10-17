using System.Globalization;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>Runs every detector over one segment's transcript and features and turns the results into its events.json, in UTC.</summary>
public static class DetectorRunner
{
    /// <summary>Provisional confidences for the audio detectors until Task 18 measures them on real audio.</summary>
    public const double ClippingConfidence = 0.9;
    public const double DeadAirConfidence = 0.95;

    public static EventsDocument Run(TranscriptDocument t, AudioFeatures f, LoadedConfig config, Watershed watershed, DateTimeOffset now)
    {
        var c = config.Config;
        var v = config.DetectorVersion;
        DateTimeOffset Utc(double offset) => t.ToUtc(offset);
        var events = new List<AnalysisEvent>();

        var clipping = ClippingDetector.Detect(f, c.Clipping);
        foreach (var e in clipping.Events)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Clipping, t.JobId, e.StartSeconds), EventTypes.Clipping, Utc(e.StartSeconds), Utc(e.EndSeconds),
                ClippingDetector.Severity(e, c.Clipping), ClippingConfidence, ClippingDetector.Name, v, EventSources.Audio, t.Recorder, t.JobId,
                R(e.StartSeconds), R(e.EndSeconds - e.StartSeconds), string.Create(CultureInfo.InvariantCulture, $"Clipping {e.DurationMs:0} ms"),
                new Dictionary<string, string>
                {
                    ["peak"] = e.Peak.ToString("0.000", CultureInfo.InvariantCulture),
                    ["runs"] = e.Runs.ToString(CultureInfo.InvariantCulture),
                    ["clippedSamples"] = e.ClippedSamples.ToString(CultureInfo.InvariantCulture),
                    ["channels"] = string.Join(',', e.Channels),
                    ["threshold"] = c.Clipping.Threshold.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var deadAir = DeadAirDetector.Detect(f, c.DeadAir);
        foreach (var e in deadAir)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.DeadAir, t.JobId, e.StartSeconds), EventTypes.DeadAir, Utc(e.StartSeconds), Utc(e.EndSeconds),
                DeadAirDetector.Severity(e, c.DeadAir), DeadAirConfidence, DeadAirDetector.Name, v, EventSources.Audio, t.Recorder, t.JobId,
                R(e.StartSeconds), R(e.Seconds), string.Create(CultureInfo.InvariantCulture, $"Dead air {e.Seconds:0.0} s"),
                new Dictionary<string, string>
                {
                    ["quietestDbfs"] = e.QuietestDbfs.ToString("0.0", CultureInfo.InvariantCulture),
                    ["thresholdDbfs"] = c.DeadAir.ThresholdDbfs.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var swears = SwearDetector.Detect(t, config.Swears);
        var suppressedRuns = SwearDetector.DetectSuppressed(t, config.Swears);
        var beforeCount = 0;
        foreach (var h in swears.Concat(suppressedRuns))
        {
            var suppressed = h.SuppressedReason;
            var at = Utc(h.Start);
            var before = watershed.IsBeforeWatershed(at);
            if (before && suppressed is null) beforeCount++;
            var evidence = new Dictionary<string, string>
            {
                // "suppressed" is "no" for a normal hit, otherwise the reason (for example "repetition"); readers aggregate on it.
                ["suppressed"] = suppressed ?? "no",
                ["tier"] = h.Tier,
                ["severityTier"] = h.SeverityTier,
                ["candidateTiers"] = string.Join(',', h.CandidateTiers),
                ["word"] = h.Text,
                ["form"] = h.Form,
                ["masked"] = h.Masked ? "true" : "false",
                ["beforeWatershed"] = before ? "true" : "false",
                ["watershedStartUtc"] = watershed.StartUtcOn(at).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["context"] = h.Context,
                ["speechOrLyrics"] = "unknown",
            };
            if (suppressed is not null) evidence["runLength"] = h.RunLength.ToString(CultureInfo.InvariantCulture);
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Swear, t.JobId, h.Start, (suppressed is null ? "" : "suppressed:") + $"{h.Segment}:{h.Word}"), EventTypes.Swear, at, Utc(h.End),
                suppressed is null ? SwearDetector.Severity(h.SeverityTier, before) : Severities.Low,
                h.Confidence, SwearDetector.Name, v, EventSources.Transcript, t.Recorder, t.JobId,
                R(h.Start), R(h.End - h.Start),
                suppressed is null ? $"{h.SeverityTier}: {h.Text}" : $"suppressed: {suppressed}: {h.SeverityTier}: {h.Text} (run of {h.RunLength})",
                evidence));
        }

        var fillers = FillerDetector.Detect(t, c.Fillers);
        foreach (var h in fillers.Hits)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Filler, t.JobId, h.Start, $"{h.Segment}:{h.Word}"), EventTypes.Filler, Utc(h.Start), Utc(h.End),
                Severities.Info, h.Confidence, FillerDetector.Name, v, EventSources.Transcript, t.Recorder, t.JobId,
                R(h.Start), R(h.End - h.Start), h.Canonical,
                new Dictionary<string, string> { ["word"] = h.Text, ["filler"] = h.Canonical, ["estimate"] = "true" }));
        }

        var stats = new Dictionary<string, double>
        {
            ["speechSeconds"] = R(fillers.SpeechSeconds),
            ["fillers"] = fillers.Hits.Count,
            ["swears"] = swears.Count,
            ["suppressedSwearRuns"] = suppressedRuns.Count,
            ["swearsBeforeWatershed"] = beforeCount,
            ["clippingEvents"] = clipping.Events.Count,
            ["clippedMs"] = R(clipping.Events.Sum(e => e.DurationMs)),
            ["clippingTruncated"] = clipping.Truncated ? 1 : 0,
            ["deadAirEvents"] = deadAir.Count,
            ["deadAirSeconds"] = R(deadAir.Sum(e => e.Seconds)),
        };
        var versions = new Dictionary<string, string>
        {
            ["detectors"] = v,
            ["transcript"] = t.TranscriptVersion,
            ["engine"] = $"{t.Engine} {t.Model}",
            [ClippingDetector.Name] = ClippingDetector.Version,
            [DeadAirDetector.Name] = DeadAirDetector.Version,
            [SwearDetector.Name] = SwearDetector.Version + " list " + config.Swears.Version,
            [FillerDetector.Name] = FillerDetector.Version,
        };
        var coverage = TranscriptSlicer.Coverage(t);

        return new EventsDocument(EventsDocument.CurrentSchema, t.Station, "segment:" + t.JobId, t.AudioFromUtc, t.AudioToUtc, now,
            versions, coverage, stats, events.OrderBy(e => e.StartUtc).ThenBy(e => e.Type, StringComparer.Ordinal).ToList());
    }

    static double R(double x) => Math.Round(x, 3);
}
