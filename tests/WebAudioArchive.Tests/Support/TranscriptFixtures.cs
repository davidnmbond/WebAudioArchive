using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Tests.Support;

/// <summary>Hand-made transcripts: segment i starts at i * 10 s; its words are spaced 0.5 s apart, 0.4 s long, probability 0.9.</summary>
public static class TranscriptFixtures
{
    public static TranscriptDocument Of(DateTimeOffset start, params string[] lines) =>
        OfSegments(start, lines.Select(l => (l, (string?)null)).ToArray());

    public static TranscriptDocument OfSegments(DateTimeOffset start, params (string Text, string? Suppressed)[] lines)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < lines.Length; i++)
        {
            var at = i * 10.0;
            var words = lines[i].Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((w, j) => new TranscriptWord(w, at + j * 0.5, at + j * 0.5 + 0.4, 0.9))
                .ToList();
            segments.Add(new TranscriptSegment(at, at + Math.Max(1, words.Count * 0.5), lines[i].Text, 0.01, -0.2, lines[i].Suppressed, words));
        }

        return new TranscriptDocument(TranscriptDocument.CurrentSchema, "burst", "a", "home-a", "a/2026/10-16/190000", "190000.mp3",
            start, start.AddHours(1), 3600, [new TimelineAnchor(0, start)], "fake", "fake-model", "en", "tv1", "decoder test",
            start.AddHours(1), 1, segments);
    }
}
