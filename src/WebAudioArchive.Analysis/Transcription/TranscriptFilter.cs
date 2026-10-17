using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Decides which whisper segments the detectors ignore (D13). They stay in the transcript with the reason. Applied when
/// the transcript is assembled and again when events are rebuilt, so tuning it never needs whisper.
/// </summary>
public static partial class TranscriptFilter
{
    public const string NonSpeech = "non-speech";
    public const string NoSpeech = "no-speech";
    public const string KnownHallucination = "known-hallucination";
    public const string Repetition = "repetition";

    public static string?[] Reasons(IReadOnlyList<(string Text, double NoSpeechProb, double AvgLogProb)> segments, TranscriptionOptions o)
    {
        var reasons = new string?[segments.Count];
        var known = new HashSet<string>(o.Hallucinations.Select(Key), StringComparer.Ordinal);
        var runStart = 0;
        string? previous = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var (text, noSpeech, logProb) = segments[i];
            var key = Key(text);
            if (key.Length == 0) reasons[i] = NonSpeech;
            else if (noSpeech > o.MaxNoSpeechProb && logProb < o.MinAvgLogProb) reasons[i] = NoSpeech;
            else if (known.Contains(key)) reasons[i] = KnownHallucination;

            if (key.Length > 0 && key == previous)
            {
                if (i - runStart + 1 >= o.MaxRepeats)
                    for (var k = runStart; k <= i; k++) reasons[k] ??= Repetition;
            }
            else
            {
                runStart = i;
            }

            previous = key;
        }

        return reasons;
    }

    /// <summary>Lower-case words without punctuation, with annotations such as [BLANK_AUDIO], (music) and note signs removed.</summary>
    public static string Key(string text) =>
        string.Join(' ', Annotation().Replace(text, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(WordNormaliser.Normalise)
            .Where(w => w.Length > 0 && w.Any(char.IsLetterOrDigit)));

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|[♪♫♬]+")]
    private static partial Regex Annotation();
}
