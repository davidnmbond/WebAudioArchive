using System.Globalization;
using System.Text;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Validation;

/// <summary>A hand label: something a person heard (type as in events.json), UTC start and end, and an optional note.</summary>
public sealed record Labelled(string Type, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Note);

/// <summary>
/// Counts for one type. <see cref="ReviewOnly"/> is the number of suppressed (review-only) detections of that type: they are
/// reported but never counted as true or false positives, and never rescue a missed label.
/// </summary>
public sealed record Score(string Type, int TruePositives, int FalsePositives, int FalseNegatives, int ReviewOnly = 0)
{
    public double? Precision => TruePositives + FalsePositives == 0 ? null : (double)TruePositives / (TruePositives + FalsePositives);

    public double? Recall => TruePositives + FalseNegatives == 0 ? null : (double)TruePositives / (TruePositives + FalseNegatives);
}

/// <summary>
/// Precision and recall of detections against labels, per type. A detection matches a label of the same type when
/// their spans overlap once both are widened by the tolerance; each label and each detection is used at most once.
/// Suppressed swear events (<see cref="AnalysisEvent.IsReviewOnly"/>) are probable hallucinations kept for review: they
/// are excluded from precision and recall and counted separately.
/// </summary>
public static class DetectorScore
{
    public const double AudioToleranceSeconds = 0.25;
    public const double TranscriptToleranceSeconds = 1;

    public static IReadOnlyList<Score> Compare(IReadOnlyList<Labelled> labels, IReadOnlyList<AnalysisEvent> detected, TimeSpan tolerance)
    {
        var types = labels.Select(l => l.Type).Concat(detected.Select(d => d.Type)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var scores = new List<Score>();
        foreach (var type in types)
        {
            var open = detected.Where(d => d.Type == type && !d.IsReviewOnly).OrderBy(d => d.StartUtc).ToList();
            var review = detected.Count(d => d.Type == type && d.IsReviewOnly);
            var tp = 0;
            var fn = 0;
            foreach (var label in labels.Where(l => l.Type == type).OrderBy(l => l.StartUtc))
            {
                var match = open.FirstOrDefault(d => d.StartUtc - tolerance < label.EndUtc + tolerance && label.StartUtc - tolerance < d.EndUtc + tolerance);
                if (match is null)
                {
                    fn++;
                    continue;
                }

                open.Remove(match);
                tp++;
            }

            scores.Add(new Score(type, tp, open.Count, fn, review));
        }

        return scores;
    }

    /// <summary>
    /// CSV with a header: type,startUtc,endUtc,note. Times must be UTC ending in Z. Types are those of events.json:
    /// filler, swear, clipping, dead-air.
    /// </summary>
    public static IReadOnlyList<Labelled> ReadCsv(string path)
    {
        var list = new List<Labelled>();
        foreach (var (line, n) in File.ReadAllLines(path).Select((l, i) => (l, i + 1)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = line.Split(',', 4);
            if (cells.Length < 3) throw new FormatException($"{path} line {n}: expected type,startUtc,endUtc,note");
            list.Add(new Labelled(cells[0].Trim(), Utc(cells[1], path, n), Utc(cells[2], path, n), cells.Length > 3 && cells[3].Trim().Length > 0 ? cells[3].Trim() : null));
        }

        return list;
    }

    public static string Table(IReadOnlyList<Score> scores)
    {
        var sb = new StringBuilder("| Type | True positives | False positives | False negatives | Precision | Recall | Review only |\n|---|---|---|---|---|---|---|\n");
        foreach (var s in scores)
            sb.Append(CultureInfo.InvariantCulture, $"| {s.Type} | {s.TruePositives} | {s.FalsePositives} | {s.FalseNegatives} | {F(s.Precision)} | {F(s.Recall)} | {s.ReviewOnly} |\n");
        return sb.ToString();
    }

    /// <summary>The text --validate prints: audio and transcript types scored separately, with their tolerances.</summary>
    public static string Report(IReadOnlyList<Labelled> labels, IReadOnlyList<AnalysisEvent> detected, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        static bool IsAudio(string type) => type is EventTypes.Clipping or EventTypes.DeadAir;
        static bool IsTranscript(string type) => type is EventTypes.Swear or EventTypes.Filler;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{labels.Count} labels, {detected.Count} detections ({detected.Count(d => d.IsReviewOnly)} review only), {fromUtc.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'} to {toUtc.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        sb.AppendLine("Suppressed swear events are excluded from precision and recall and shown in the Review only column.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Audio events ({AudioToleranceSeconds:0.##} s tolerance)");
        sb.AppendLine(Table(Compare(labels.Where(l => IsAudio(l.Type)).ToList(), detected.Where(d => IsAudio(d.Type)).ToList(), TimeSpan.FromSeconds(AudioToleranceSeconds))));
        sb.AppendLine(CultureInfo.InvariantCulture, $"Transcript events ({TranscriptToleranceSeconds:0.##} s tolerance, word times are approximate)");
        sb.AppendLine(Table(Compare(labels.Where(l => IsTranscript(l.Type)).ToList(), detected.Where(d => IsTranscript(d.Type)).ToList(), TimeSpan.FromSeconds(TranscriptToleranceSeconds))));
        return sb.ToString();
    }

    static string F(double? v) => v is null ? "n/a" : v.Value.ToString("0.00", CultureInfo.InvariantCulture);

    static DateTimeOffset Utc(string text, string path, int line)
    {
        var t = text.Trim();
        if (!t.EndsWith('Z') || !DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var value))
            throw new FormatException($"{path} line {line}: '{t}' is not a UTC time ending in Z");
        return value;
    }
}
