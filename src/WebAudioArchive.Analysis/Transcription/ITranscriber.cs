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
/// Speech to text, kept behind this interface so the engine can change (the whisper.cpp server over HTTP, decision D19) and tests use a fake.
/// TranscribeAsync throws <see cref="TranscriberUnavailableException"/> when the engine cannot be reached (the job waits
/// and retries, it is not failed) and <see cref="TranscriptionFailedException"/> when this input cannot be transcribed.
/// </summary>
public interface ITranscriber
{
    /// <summary>For information, recorded in the transcript: the engine and, once loaded, the device (for example "whisper.cpp server at pdl-rune-02.panoramicdata.com:8080"). It changes between runs, so it is never part of the job version.</summary>
    string Engine { get; }

    /// <summary>
    /// What decides, with the transcript options, whether a stored transcript is current: the engine family ("whisper.cpp"; a stored "whisper.net 1" from the retired in-process engine counts as equal, see JobRules.SameTranscriptVersion).
    /// Never the model, its size, the device or the load state, so switching the model (for example to base.en) or the server
    /// does not re-transcribe the archive. <see cref="ModelIdentity"/> is recorded with each job
    /// and compared only when re-transcription is asked for (WAA_RETRANSCRIBE, or --rebuild --retranscribe).
    /// </summary>
    string Identity { get; }

    string Model { get; }

    /// <summary>The model file's size in bytes, or null when unknown or the file is missing. Information only.</summary>
    long? ModelBytes => null;

    /// <summary>The model name and file size ("ggml-base.en|147951465"): recorded with each finished job, compared only when re-transcription is asked for.</summary>
    string ModelIdentity => $"{Model}|{(ModelBytes is { } bytes ? bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}";

    /// <summary>Drops a locally loaded model so its memory is free for another program. The whisper server owns its model, so the default does nothing.</summary>
    void ResetModel()
    {
    }

    /// <summary>Where the model runs, for /healthz: "remote" for the whisper server unless it reports its device, or "unknown". For information only; never part of the job version.</summary>
    string Device => "unknown";

    Task<bool> IsReadyAsync(CancellationToken ct);

    Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public sealed class TranscriberUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class TranscriptionFailedException(string message) : Exception(message);

/// <summary>The engine ran out of memory or hit a GPU error: probably not this input's fault, so the job waits and retries later without using an attempt.</summary>
public sealed class TransientTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);
