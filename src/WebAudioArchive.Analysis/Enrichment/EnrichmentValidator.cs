using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Enrichment;

public sealed class EnrichmentInvalidException(string message) : Exception(message);

public sealed record EnrichmentParts(
    List<DerivedClaim> Hosts,
    List<DerivedClaim> Guests,
    List<DerivedClaim> Topics,
    List<DerivedSong> Songs,
    List<DerivedClaim> Artists,
    List<DerivedClaim> Locations,
    List<DerivedClaim> CallsToAction,
    string? Language,
    string? Summary,
    int Dropped,
    List<string> DropReasons);

/// <summary>
/// Spec 3.7 and 6: every claim needs a quote that is in the transcript (compared in lower case with punctuation and
/// spacing ignored), and for names, titles and places the quote must contain the name itself. Anything else is dropped
/// and counted with the reason. A reply that is not an object of the expected shape is invalid (retried).
/// </summary>
public static partial class EnrichmentValidator
{
    public const int MaxSummaryChars = 600;

    public static EnrichmentParts Validate(string content, IReadOnlyList<TimedLine> lines, int maxItems)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(StripWrapping(content));
        }
        catch (JsonException ex)
        {
            throw new EnrichmentInvalidException($"the answer is not JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new EnrichmentInvalidException("the answer is not a JSON object");
            var normalisedLines = lines.Select(l => (Line: l, Text: Normalise(l.Text))).ToList();
            var whole = " " + string.Join(' ', normalisedLines.Select(l => l.Text)) + " ";
            var reasons = new List<string>();

            // Where the quote was said: the line holding all of it, or (for a quote running over two lines) the line holding its first three words.
            DateTimeOffset? Where(string quote)
            {
                var head = string.Join(' ', quote.Split(' ').Take(3));
                return normalisedLines.FirstOrDefault(l => l.Text.Contains(quote, StringComparison.Ordinal)).Line?.StartUtc
                    ?? normalisedLines.FirstOrDefault(l => l.Text.Contains(head, StringComparison.Ordinal)).Line?.StartUtc;
            }

            List<DerivedClaim> Claims(string name, bool nameInQuote)
            {
                var result = new Dictionary<string, DerivedClaim>(StringComparer.Ordinal);
                foreach (var item in Items(root, name))
                {
                    var value = Text(item, "value").Trim();
                    var quote = Text(item, "quote").Trim();
                    var q = Normalise(quote);
                    if (value.Length == 0) { reasons.Add($"{name}: an entry with no value"); continue; }
                    if (q.Length < 3 || !whole.Contains(" " + q + " ", StringComparison.Ordinal)) { reasons.Add($"{name} '{value}': the quote is not in the transcript"); continue; }
                    if (nameInQuote && !(" " + q + " ").Contains(" " + Normalise(value) + " ", StringComparison.Ordinal)) { reasons.Add($"{name} '{value}': the quote does not contain the name"); continue; }
                    var claim = new DerivedClaim(value, NullIfEmpty(Text(item, "detail")), Confidence(item), quote, Where(q));
                    var key = Normalise(value);
                    if (!result.TryGetValue(key, out var existing) || existing.Confidence < claim.Confidence) result[key] = claim;
                }

                return result.Values.Take(maxItems).ToList();
            }

            var songs = new Dictionary<string, DerivedSong>(StringComparer.Ordinal);
            foreach (var item in Items(root, "songs"))
            {
                var title = Text(item, "title").Trim();
                var artist = NullIfEmpty(Text(item, "artist"));
                var quote = Text(item, "quote").Trim();
                var q = " " + Normalise(quote) + " ";
                if (title.Length == 0) { reasons.Add("songs: an entry with no title"); continue; }
                if (q.Trim().Length < 3 || !whole.Contains(q, StringComparison.Ordinal)) { reasons.Add($"songs '{title}': the quote is not in the transcript"); continue; }
                if (!q.Contains(" " + Normalise(title) + " ", StringComparison.Ordinal)) { reasons.Add($"songs '{title}': the quote does not contain the title"); continue; }
                if (artist is not null && !q.Contains(" " + Normalise(artist) + " ", StringComparison.Ordinal))
                {
                    reasons.Add($"songs '{title}': artist '{artist}' is not in the quote, so it was left out");
                    artist = null;
                }

                var song = new DerivedSong(title, artist, Confidence(item), quote, Where(q.Trim()));
                var key = Normalise(title);
                if (!songs.TryGetValue(key, out var existing) || existing.Confidence < song.Confidence) songs[key] = song;
            }

            var hosts = Claims("hosts", true);
            var guests = Claims("guests", true);
            var topics = Claims("topics", false);
            var artists = Claims("artists", true);
            var locations = Claims("locations", true);
            var calls = Claims("callsToAction", false);
            var summary = NullIfEmpty(Text(root, "summary"));
            if (summary is not null && summary.Length > MaxSummaryChars) summary = summary[..MaxSummaryChars];
            var language = NullIfEmpty(Text(root, "language"));
            if (language is not null && language.Length > 8) language = null;

            return new EnrichmentParts(hosts, guests, topics, songs.Values.Take(maxItems).ToList(), artists, locations, calls, language, summary,
                reasons.Count, reasons);
        }
    }

    /// <summary>Lower case, letters and digits only, single spaces: "I'm Amy," is "i m amy".</summary>
    public static string Normalise(string text) => NotWord().Replace(text.ToLower(CultureInfo.InvariantCulture), " ").Trim();

    /// <summary>The JSON inside a code fence, after a thinking block, or with spaces around it.</summary>
    public static string StripWrapping(string content)
    {
        var t = content.Trim();
        var thinkEnd = t.IndexOf("</think>", StringComparison.Ordinal);
        if (t.StartsWith("<think>", StringComparison.Ordinal) && thinkEnd > 0) t = t[(thinkEnd + "</think>".Length)..].Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = t.IndexOf('\n');
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && close > firstLine) t = t[(firstLine + 1)..close].Trim();
        }

        return t;
    }

    static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array) throw new EnrichmentInvalidException($"'{name}' is not a list");
        return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
    }

    static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Confidence(JsonElement e) =>
        e.TryGetProperty("confidence", out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? Math.Clamp(v.GetDouble(), 0, 1) : 0.5;

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();
}
