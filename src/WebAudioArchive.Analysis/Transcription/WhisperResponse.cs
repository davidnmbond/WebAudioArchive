using Whisper.net;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Turns Whisper.net's segments into <see cref="RawSegment"/>s. Whisper.net gives per token the text, start and end in
/// centiseconds, the DTW time in centiseconds (-1 when DTW is off), the probability and its log; per segment the
/// no-speech probability. avg_logprob is the mean log probability of the segment's text tokens, as whisper.cpp's
/// server computes it.
/// </summary>
public static class WhisperResponse
{
    public readonly record struct Token(string Text, double Start, double End, double Dtw, double Probability);

    public static RawSegment FromSegment(SegmentData s)
    {
        var start = s.Start.TotalSeconds;
        var end = Math.Max(start, s.End.TotalSeconds);
        var tokens = (s.Tokens ?? []).Select(t => new Token(t.Text ?? "", t.Start / 100.0, t.End / 100.0, t.DtwTimestamp, t.Probability)).ToList();
        var logs = (s.Tokens ?? []).Where(t => !IsSpecial((t.Text ?? "").Trim())).Select(t => (double)t.ProbabilityLog).ToList();
        var avgLogProb = logs.Count == 0 ? 0 : Math.Round(logs.Average(), 4);
        return new RawSegment(start, end, (s.Text ?? "").Trim(), Math.Round(s.NoSpeechProbability, 4), avgLogProb, MergeTokens(tokens));
    }

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

    static bool IsSpecial(string s) =>
        (s.StartsWith("[_", StringComparison.Ordinal) && s.EndsWith(']')) || (s.StartsWith("<|", StringComparison.Ordinal) && s.EndsWith("|>", StringComparison.Ordinal));
}
