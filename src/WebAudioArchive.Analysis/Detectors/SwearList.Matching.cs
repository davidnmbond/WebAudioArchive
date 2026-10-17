namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// A matched form, its top tier, whether the word was masked with asterisks, and how sure the match is (1, or less when masked).
/// SeverityTier is the tier to score with: the lowest candidate tier when a masked word could be forms from several tiers
/// (so "s***" is not scored as strong when it may be "sod"), otherwise the tier itself. CandidateTiers lists the distinct
/// tiers the masked word could be, lowest first, for the event detail.
/// </summary>
public sealed record SwearMatch(string Form, string Tier, bool Masked, double Certainty, string SeverityTier, IReadOnlyList<string> CandidateTiers);

public sealed partial class SwearList
{
    static readonly char[] Hyphens = ['-', '‐', '‑', '‒', '–'];

    /// <summary>
    /// Whole-word match after normalising. A hyphenated word ("shit-hot", "fucking-hell") matches as the joined word or as
    /// any one of its parts, the highest tier winning, and is reported once; a joined form on the allow list never
    /// matches. A masked word ("f***", "f*ck", "s***ing") matches forms with the same first letters, the same ending and
    /// a length within 2; the highest tier wins, at certainty 0.5 when every candidate is in that tier and 0.3 otherwise
    /// (scored by the lowest candidate tier). A word with no letter before the first '*' never matches.
    /// </summary>
    public SwearMatch? Match(string rawWord)
    {
        var joined = WordNormaliser.Normalise(rawWord);
        if (rawWord.IndexOfAny(Hyphens) < 0) return MatchWord(joined);

        if (joined.Length == 0 || Allow.Contains(joined)) return null;
        var best = MatchWord(joined);
        foreach (var part in rawWord.Split(Hyphens, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = MatchWord(WordNormaliser.Normalise(part));
            if (m is not null && (best is null || SwearTiers.Rank(m.Tier) > SwearTiers.Rank(best.Tier))) best = m;
        }

        return best;
    }

    SwearMatch? MatchWord(string w)
    {
        if (w.Length == 0 || Allow.Contains(w)) return null;
        if (!w.Contains('*')) return Forms.TryGetValue(w, out var tier) ? new SwearMatch(w, tier, false, 1.0, tier, [tier]) : null;

        var first = w.IndexOf('*');
        var last = w.LastIndexOf('*');
        if (first == 0 || w[first..(last + 1)].Any(c => c != '*')) return null;
        var prefix = w[..first];
        var suffix = w[(last + 1)..];
        var candidates = Forms
            .Where(f => f.Key.Length > prefix.Length + suffix.Length
                && f.Key.StartsWith(prefix, StringComparison.Ordinal)
                && f.Key.EndsWith(suffix, StringComparison.Ordinal)
                && Math.Abs(f.Key.Length - w.Length) <= 2)
            .ToList();
        if (candidates.Count == 0) return null;
        var best = candidates.MaxBy(c => SwearTiers.Rank(c.Value));
        var tiers = candidates.Select(c => c.Value).Distinct().OrderBy(SwearTiers.Rank).ToList();
        var certainty = tiers.Count == 1 ? 0.5 : 0.3;
        return new SwearMatch(w, best.Value, true, certainty, tiers[0], tiers);
    }
}
