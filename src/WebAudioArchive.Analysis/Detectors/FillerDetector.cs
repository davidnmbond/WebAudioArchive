using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record FillerHit(int Segment, int Word, string Text, string Canonical, double Start, double End, double Confidence);

/// <summary>The fillers found and the seconds of speech they were found in (non-suppressed segments with words), for a rate per minute.</summary>
public sealed record FillerResult(IReadOnlyList<FillerHit> Hits, double SpeechSeconds);

/// <summary>
/// Spec 3.6: counts um, uh, er, erm, ah and hmm (drawn-out spellings folded: "ummm" is "um"). "like" is never counted;
/// "you know" only when configured, and only when set off by commas. Whisper drops many fillers even with the verbatim
/// prompt, so this is an estimate; Task 18 records its measured precision and recall. Confidence is the word
/// probability times 0.8 for that reason.
/// </summary>
public static partial class FillerDetector
{
    public const string Name = "filler";
    public const string Version = "1";
    const double EstimateFactor = 0.8;

    public static string? Canonical(string normalised) => normalised switch
    {
        _ when Um().IsMatch(normalised) => "um",
        _ when Uh().IsMatch(normalised) => "uh",
        _ when Er().IsMatch(normalised) => "er",
        _ when Erm().IsMatch(normalised) => "erm",
        _ when Ah().IsMatch(normalised) => "ah",
        _ when Hmm().IsMatch(normalised) => "hmm",
        _ => null,
    };

    public static FillerResult Detect(TranscriptDocument t, FillerOptions o)
    {
        var words = new HashSet<string>(o.Words, StringComparer.Ordinal);
        var hits = new List<FillerHit>();
        double speech = 0;
        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is not null || s.Words.Count == 0) continue;
            speech += Math.Max(0, s.End - s.Start);
            for (var j = 0; j < s.Words.Count; j++)
            {
                var w = s.Words[j];
                var canonical = Canonical(WordNormaliser.Normalise(w.Text));
                if (canonical is not null && words.Contains(canonical))
                {
                    hits.Add(Hit(i, j, w.Text, canonical, w.Start, w.End, w.Probability));
                    continue;
                }

                if (o.CountYouKnow && j + 1 < s.Words.Count && IsYouKnow(s.Words, j))
                {
                    var next = s.Words[j + 1];
                    hits.Add(Hit(i, j, w.Text + " " + next.Text, "you know", w.Start, next.End, Math.Min(w.Probability, next.Probability)));
                }
            }
        }

        return new FillerResult(hits, speech);
    }

    /// <summary>"you know" set off as an aside: "you" follows a word ending in a comma, or opens the segment and "know" ends in a comma. "Do you know, the way" is a question, not a filler.</summary>
    static bool IsYouKnow(IReadOnlyList<TranscriptWord> words, int j)
    {
        if (WordNormaliser.Normalise(words[j].Text) != "you" || WordNormaliser.Normalise(words[j + 1].Text) != "know") return false;
        var knowEndsWithComma = words[j + 1].Text.TrimEnd().EndsWith(',');
        var afterComma = j > 0 && words[j - 1].Text.TrimEnd().EndsWith(',');
        return afterComma || (j == 0 && knowEndsWithComma);
    }

    static FillerHit Hit(int segment, int word, string text, string canonical, double start, double end, double probability) =>
        new(segment, word, text, canonical, start, Math.Max(start, end), Math.Round((double.IsNaN(probability) ? 0 : Math.Clamp(probability, 0, 1)) * EstimateFactor, 3));

    [GeneratedRegex("^u+m+$")]
    private static partial Regex Um();

    [GeneratedRegex("^u+h+$")]
    private static partial Regex Uh();

    [GeneratedRegex("^e+r+$")]
    private static partial Regex Er();

    [GeneratedRegex("^e+r+m+$")]
    private static partial Regex Erm();

    [GeneratedRegex("^a+h+$")]
    private static partial Regex Ah();

    [GeneratedRegex("^h+m+$")]
    private static partial Regex Hmm();
}
