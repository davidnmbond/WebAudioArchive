using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>A finished chunk of a job: where it lies in the audio and what whisper returned for it, already shifted to segment time.</summary>
public sealed record PartialChunk(int Index, double Start, double End, IReadOnlyList<RawSegment> Segments);

/// <summary>transcript.partial.json: chunks finished so far, so a restart resumes instead of transcribing again.</summary>
public sealed record TranscriptPartial(int Schema, string Fingerprint, string TranscriptVersion, int ChunkCount, double TranscribeSeconds, IReadOnlyList<PartialChunk> Chunks)
{
    public const int CurrentSchema = 1;
}

/// <summary>What the decoder reports about a segment: frames found, frames written as silence, frames recovered.</summary>
public sealed record DecodeStats(long Frames, int Silenced, int Recovered);

public static class TranscriptAssembler
{
    public static TranscriptDocument Build(
        SourceSegment s, string station, IReadOnlyList<TimelineAnchor> anchors, IEnumerable<RawSegment> segments, TranscriptionOptions o,
        string engine, string model, string transcriptVersion, string decoder, DateTimeOffset now, double transcribeSeconds, DecodeStats? decode = null)
    {
        var ordered = segments.OrderBy(x => x.Start).ToList();
        var reasons = TranscriptFilter.Reasons(ordered.Select(x => (x.Text, x.NoSpeechProb, x.AvgLogProb)).ToList(), o);
        var list = ordered.Select((x, i) => new TranscriptSegment(
                Round(x.Start), Round(x.End), x.Text, x.NoSpeechProb, x.AvgLogProb, reasons[i],
                x.Words.Select(w => w with { Start = Round(w.Start), End = Round(w.End) }).ToList()))
            .ToList();
        return new TranscriptDocument(TranscriptDocument.CurrentSchema, station, s.Recorder, s.Meta.Instance, s.JobId, s.Meta.File,
            s.Meta.StartUtc, s.Meta.EndUtc, s.Meta.AudioSeconds, anchors, engine, model, o.Language, transcriptVersion, decoder,
            now, Math.Round(transcribeSeconds, 3), list)
        {
            DecodedFrames = decode?.Frames,
            SilencedFrames = decode?.Silenced,
            RecoveredFrames = decode?.Recovered,
            Flags = decode is not null && decode.Silenced > o.MaxSilencedFrameFraction * decode.Frames ? ["silenced-frames"] : null,
        };
    }

    /// <summary>The same transcript with suppression worked out again under the current options (used when events are rebuilt).</summary>
    public static TranscriptDocument Refilter(TranscriptDocument t, TranscriptionOptions o)
    {
        var reasons = TranscriptFilter.Reasons(t.Segments.Select(x => (x.Text, x.NoSpeechProb, x.AvgLogProb)).ToList(), o);
        return t with { Segments = t.Segments.Select((x, i) => x with { Suppressed = reasons[i] }).ToList() };
    }

    static double Round(double x) => Math.Round(x, 3);
}
