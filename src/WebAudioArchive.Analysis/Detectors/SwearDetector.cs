using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>A matched word: its segment and word index, the word as transcribed, the matched form, tier, times (seconds into the audio) and the words around it.</summary>
public sealed record SwearHit(int Segment, int Word, string Text, string Form, string Tier, bool Masked, double Confidence, double Start, double End, string Context);

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

    public static IReadOnlyList<SwearHit> Detect(TranscriptDocument t, SwearList list)
    {
        var hits = new List<SwearHit>();
        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is not null) continue;
            for (var j = 0; j < s.Words.Count; j++)
            {
                var w = s.Words[j];
                var m = list.Match(w.Text);
                if (m is null) continue;
                var from = Math.Max(0, j - ContextWords);
                var to = Math.Min(s.Words.Count, j + ContextWords + 1);
                var context = string.Join(' ', s.Words.Skip(from).Take(to - from).Select(x => x.Text));
                var confidence = Math.Round(Math.Clamp(w.Probability, 0, 1) * m.Certainty, 3);
                hits.Add(new SwearHit(i, j, w.Text, m.Form, m.Tier, m.Masked, confidence, w.Start, Math.Max(w.Start, w.End), context));
            }
        }

        return hits;
    }

    /// <summary>mild is low, strong medium, severe high; before the watershed each is one step more serious.</summary>
    public static string Severity(string tier, bool beforeWatershed) =>
        (SwearTiers.Rank(tier) + (beforeWatershed ? 1 : 0)) switch
        {
            <= 1 => Severities.Low,
            2 => Severities.Medium,
            _ => Severities.High,
        };
}
