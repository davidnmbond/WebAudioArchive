namespace WebAudioArchive.Core.Analysis;

public sealed record TimedWord(string Text, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Probability);

/// <summary>One transcript line (a whisper segment) in UTC, with the recorder and job it came from.</summary>
public sealed record TimedLine(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Text, string Recorder, string JobId, IReadOnlyList<TimedWord> Words);

/// <summary>Builds the station transcript for any UTC range (a show with its margins, a search hit) from per-segment transcripts.</summary>
public static class TranscriptSlicer
{
    public static IReadOnlyList<SourceSpan> Coverage(TranscriptDocument doc) =>
        SegmentTimeline.Pieces(doc.Timeline, doc.AudioSeconds).Select(p => new SourceSpan(doc.Recorder, p.From, p.To)).ToList();

    /// <summary>Lines overlapping [fromUtc, toUtc), suppressed lines left out, preferred recorders first where they overlap.</summary>
    public static IReadOnlyList<TimedLine> Slice(IEnumerable<TranscriptDocument> docs, DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string> preference)
    {
        var list = docs.ToList();
        var spans = list.SelectMany(Coverage).ToList();
        var lines = new List<TimedLine>();
        foreach (var d in list)
        {
            foreach (var s in d.Segments)
            {
                if (s.Suppressed is not null || string.IsNullOrWhiteSpace(s.Text)) continue;
                var start = d.ToUtc(s.Start);
                var end = d.ToUtc(Math.Max(s.Start, s.End));
                if (end <= fromUtc || start >= toUtc) continue;
                if (!RecorderPreference.Keep(d.Recorder, start, spans, preference)) continue;
                var words = s.Words
                    .Select(w => new TimedWord(w.Text, d.ToUtc(w.Start), d.ToUtc(Math.Max(w.Start, w.End)), w.Probability))
                    .ToList();
                lines.Add(new TimedLine(start, end, s.Text.Trim(), d.Recorder, d.JobId, words));
            }
        }

        return lines
            .OrderBy(l => l.StartUtc)
            .ThenBy(l => RecorderPreference.Rank(l.Recorder, preference))
            .ToList();
    }
}
