using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Merges whisper.cpp tokens into words. The server's verbose_json (see <see cref="WhisperServerResponse"/>) gives per token the text,
/// start and end in seconds, the DTW time in centiseconds (-1 when DTW is off) and the probability; a token that starts with a
/// space starts a new word.
/// </summary>
public static class WhisperResponse
{
    public readonly record struct Token(string Text, double Start, double End, double Dtw, double Probability);

    /// <summary>A token that starts with a space starts a new word; others continue it ("wel" "come" ","). A known DTW time (centiseconds) gives the word's start.</summary>
    public static IReadOnlyList<TranscriptWord> MergeTokens(IEnumerable<Token> tokens)
    {
        var words = new List<TranscriptWord>();
        string? text = null;
        double start = 0, end = 0, p = 1;
        foreach (var t in tokens)
        {
            var trimmed = t.Text.Trim();
            if (trimmed.Length == 0 || IsSpecial(trimmed)) continue;
            var tokenStart = t.Dtw >= 0 ? t.Dtw * 0.01 : t.Start;
            if (text is null || t.Text.StartsWith(' '))
            {
                if (text is not null) words.Add(new TranscriptWord(text, start, Math.Max(start, end), Math.Round(p, 4)));
                (text, start, end, p) = (trimmed, tokenStart, t.End, t.Probability);
            }
            else
            {
                text += trimmed;
                end = Math.Max(end, t.End);
                p = Math.Min(p, t.Probability);
            }
        }

        if (text is not null) words.Add(new TranscriptWord(text, start, Math.Max(start, end), Math.Round(p, 4)));
        return words;
    }

    /// <summary>
    /// The mean of ln(probability) over the segment's text tokens (special and timestamp tokens and empty ones left out), 0 when there
    /// are none. This is the figure <see cref="TranscriptFilter"/> was tuned on (the in-process engine's definition). whisper.cpp's
    /// server reports its own avg_logprob, but divides by all tokens including the special ones, so short segments arrive about
    /// 40 percent too close to 0 and the filter's MinAvgLogProb rule would stop firing; the client therefore recomputes it.
    /// </summary>
    public static double AverageLogProbability(IEnumerable<Token> tokens)
    {
        double sum = 0;
        var n = 0;
        foreach (var t in tokens)
        {
            var trimmed = t.Text.Trim();
            if (trimmed.Length == 0 || IsSpecial(trimmed)) continue;
            sum += Math.Log(Math.Max(t.Probability, 1e-10));
            n++;
        }

        return n == 0 ? 0 : sum / n;
    }

    static bool IsSpecial(string s) =>
        (s.StartsWith("[_", StringComparison.Ordinal) && s.EndsWith(']')) || (s.StartsWith("<|", StringComparison.Ordinal) && s.EndsWith("|>", StringComparison.Ordinal));
}
