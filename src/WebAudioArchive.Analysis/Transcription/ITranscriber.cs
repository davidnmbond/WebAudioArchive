using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>One chunk of 16 kHz mono 16-bit WAV, the priming prompt and the language.</summary>
public sealed record TranscriptionRequest(byte[] Wav, string Prompt, string Language);

/// <summary>A segment as the engine returned it. Times are seconds from the start of the request's audio until shifted.</summary>
public sealed record RawSegment(double Start, double End, string Text, double NoSpeechProb, double AvgLogProb, IReadOnlyList<TranscriptWord> Words)
{
    public RawSegment Shift(double seconds) => this with
    {
        Start = Start + seconds,
        End = End + seconds,
        Words = Words.Select(w => w with { Start = w.Start + seconds, End = w.End + seconds }).ToList(),
    };
}

/// <summary>
/// Speech to text, kept behind this interface so the engine can change (whisper.cpp through Whisper.net today) and tests use a fake.
/// TranscribeAsync throws <see cref="TranscriberUnavailableException"/> when the engine cannot be reached (the job waits
/// and retries, it is not failed) and <see cref="TranscriptionFailedException"/> when this input cannot be transcribed.
/// </summary>
public interface ITranscriber
{
    string Engine { get; }

    string Model { get; }

    Task<bool> IsReadyAsync(CancellationToken ct);

    Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public sealed class TranscriberUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class TranscriptionFailedException(string message) : Exception(message);
