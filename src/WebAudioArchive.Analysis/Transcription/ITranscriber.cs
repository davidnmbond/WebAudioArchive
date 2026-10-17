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
    /// <summary>For information, recorded in the transcript: the engine and, once loaded, the device (for example "whisper.net 1.9.1 (cuda)"). It changes between runs, so it is never part of the job version.</summary>
    string Engine { get; }

    /// <summary>
    /// What decides whether a stored transcript is current: the engine package version, the model and the model file size.
    /// Never the device or load state, so a GPU to CPU fallback does not re-transcribe the archive.
    /// </summary>
    string Identity { get; }

    string Model { get; }

    Task<bool> IsReadyAsync(CancellationToken ct);

    Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public sealed class TranscriberUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class TranscriptionFailedException(string message) : Exception(message);

/// <summary>The engine ran out of memory or hit a GPU error: probably not this input's fault, so the job waits and retries later without using an attempt.</summary>
public sealed class TransientTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);
