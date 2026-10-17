using System.Globalization;
using System.Text;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Validation;

/// <summary>
/// The thresholds table that docs/analysis-validation.md carries between its markers, generated from the loaded config so a
/// test can fail when the document and config/analysis/analysis.json disagree.
/// </summary>
public static class ThresholdsDoc
{
    public static string Render(LoadedConfig loaded)
    {
        var c = loaded.Config;
        static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Config version {c.Version}.\n\n");
        sb.Append("| Detector | Rule | Values |\n|---|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| Clipping | a run of at least N consecutive samples at or above the threshold (either sign); runs closer than the merge window are one event; medium severity from the given length | threshold {N(c.Clipping.Threshold)} of full scale, N = {c.Clipping.MinRunSamples}, merge {c.Clipping.MergeWithinMs} ms, medium from {c.Clipping.MediumMs} ms |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| Dead air | every 50 ms window (loudest channel) below the level for at least the minimum length | below {N(c.DeadAir.ThresholdDbfs)} dBFS for {N(c.DeadAir.MinSeconds)} s; high severity from {N(c.DeadAir.SevereSeconds)} s |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| Swearing | whole words and listed suffixes from {c.Swearing.WordList}, masked forms at reduced confidence, allow list for near misses; severity from the tier, one step higher before the watershed; repeated runs are suppressed and reported for review only | watershed {c.Swearing.WatershedStart} to {c.Swearing.WatershedEnd} Europe/London |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| Fillers | listed words only (drawn-out spellings folded), never \"like\"; \"you know\" counted: {(c.Fillers.CountYouKnow ? "on" : "off")} | {string.Join(", ", c.Fillers.Words)}; reported as a rate per minute of transcribed speech, an estimate |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| Hallucination filter | suppress no-speech, annotations, known phrases, repeats | no_speech_prob above {N(c.Transcription.MaxNoSpeechProb)} with avg_logprob below {N(c.Transcription.MinAvgLogProb)}; {c.Transcription.MaxRepeats} or more repeats |\n");
        return sb.ToString();
    }
}
