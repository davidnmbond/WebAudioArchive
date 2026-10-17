using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// A matched word: its segment and word index, the word as transcribed, the matched form, tier, times (seconds into the audio) and the words around it.
/// SeverityTier and CandidateTiers come from the match (see SwearMatch). SuppressedReason is null for a normal hit; for the
/// one event per suppressed run it names why the run was suppressed (for example "repetition"), RunLength is how many
/// swear words the run held, Segment, Word, Text and Form are the run's highest-tier word (the first of them) and Start and End span the whole run.
/// </summary>
public sealed record SwearHit(
    int Segment,
    int Word,
    string Text,
    string Form,
    string Tier,
    bool Masked,
    double Confidence,
    double Start,
    double End,
    string Context,
    string SeverityTier,
    IReadOnlyList<string> CandidateTiers,
    string? SuppressedReason = null,
    int RunLength = 1);

/// <summary>
/// Spec 3.6: whisper words against the tiered list, whole words and listed inflections. Speech to text can mis-hear or
/// soften words, so confidence is whisper's word probability times the match certainty, and every hit links to its
/// audio for a person to confirm. Song lyrics are transcribed too and are reported the same way (D12).
/// </summary>
public static class SwearDetector
{
    public const string Name = "swear";
    public const string Version = "1";
    const int ContextWords = 5;

    /// <summary>The most confidence a suppressed run can carry: it is probably repeated or invented text, so it is for review, not for counting.</summary>
    public const double SuppressedConfidenceCap = 0.3;

    /// <summary>Normal hits: swear words in segments that are not suppressed. Suppressed segments are reported by <see cref="DetectSuppressed"/>.</summary>
    public static IReadOnlyList<SwearHit> Detect(TranscriptDocument t, SwearList list)
    {
        var hits = new List<SwearHit>();
        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is not null) continue;
            for (var j = 0; j < s.Words.Count; j++)
            {
                var m = list.Match(s.Words[j].Text);
                if (m is not null) hits.Add(Hit(i, j, s, m));
            }
        }

        return hits;
    }

    /// <summary>
    /// One hit per run of consecutive suppressed segments (same reason) that holds swear words: the run's highest-tier word
    /// (the first of them), the number of swear words in the run, its time span, confidence capped at
    /// <see cref="SuppressedConfidenceCap"/>. Kept out of the normal count and score so repetition or hallucination cannot
    /// inflate them, but never dropped silently: a repeated swear can be a chant, a lyric or an angry speaker.
    /// </summary>
    public static IReadOnlyList<SwearHit> DetectSuppressed(TranscriptDocument t, SwearList list)
    {
        var runs = new List<SwearHit>();
        SwearHit? best = null;
        var count = 0;
        double start = 0, end = 0;
        var lastSegment = -2;
        string? reason = null;

        void Flush()
        {
            if (best is not null)
            {
                runs.Add(best with
                {
                    Start = start,
                    End = end,
                    RunLength = count,
                    Confidence = Math.Min(SuppressedConfidenceCap, best.Confidence),
                    SuppressedReason = reason,
                });
            }

            best = null;
            count = 0;
        }

        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is null) continue;
            var found = false;
            for (var j = 0; j < s.Words.Count; j++)
            {
                var m = list.Match(s.Words[j].Text);
                if (m is null) continue;
                if (!found && best is not null && (i != lastSegment + 1 || s.Suppressed != reason)) Flush();
                var hit = Hit(i, j, s, m);
                if (best is null)
                {
                    start = hit.Start;
                    reason = s.Suppressed;
                }

                found = true;
                end = hit.End;
                count++;
                if (best is null || SwearTiers.Rank(hit.Tier) > SwearTiers.Rank(best.Tier)) best = hit;
            }

            if (found) lastSegment = i;
        }

        Flush();
        return runs;
    }

    static SwearHit Hit(int segment, int word, TranscriptSegment s, SwearMatch m)
    {
        var w = s.Words[word];
        var from = Math.Max(0, word - ContextWords);
        var to = Math.Min(s.Words.Count, word + ContextWords + 1);
        var context = string.Join(' ', s.Words.Skip(from).Take(to - from).Select(x => x.Text));
        var probability = double.IsNaN(w.Probability) ? 0 : Math.Clamp(w.Probability, 0, 1);
        var confidence = Math.Round(probability * m.Certainty, 3);
        return new SwearHit(segment, word, w.Text, m.Form, m.Tier, m.Masked, confidence, w.Start, Math.Max(w.Start, w.End), context, m.SeverityTier, m.CandidateTiers);
    }

    /// <summary>mild is low, strong medium, severe high; before the watershed each is one step more serious. Pass the hit's SeverityTier.</summary>
    public static string Severity(string tier, bool beforeWatershed) =>
        (SwearTiers.Rank(tier) + (beforeWatershed ? 1 : 0)) switch
        {
            <= 1 => Severities.Low,
            2 => Severities.Medium,
            _ => Severities.High,
        };
}
