namespace WebAudioArchive.Analysis.Detectors;

/// <summary>A matched form, its tier, whether the word was masked with asterisks, and how sure the match is (1, or less when masked).</summary>
public sealed record SwearMatch(string Form, string Tier, bool Masked, double Certainty);

public sealed partial class SwearList
{
    /// <summary>
    /// Whole-word match after normalising. A masked word ("f***", "f*ck", "s***ing") matches forms with the same first
    /// letters, the same ending and a length within 2; the highest tier wins, at certainty 0.5 when every candidate is
    /// in that tier and 0.3 otherwise. A word with no letter before the first '*' never matches.
    /// </summary>
    public SwearMatch? Match(string rawWord)
    {
        var w = WordNormaliser.Normalise(rawWord);
        if (w.Length == 0 || Allow.Contains(w)) return null;
        if (!w.Contains('*')) return Forms.TryGetValue(w, out var tier) ? new SwearMatch(w, tier, false, 1.0) : null;

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
        var certainty = candidates.All(c => c.Value == best.Value) ? 0.5 : 0.3;
        return new SwearMatch(w, best.Value, true, certainty);
    }
}
