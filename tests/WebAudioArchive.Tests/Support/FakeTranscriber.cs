using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A transcriber for tests: Respond gets each request and its call number (0 based) and returns the segments, or throws
/// to simulate whisper being down (TranscriberUnavailableException), a bad input, or a crash. Engine (which names the
/// device, for information) can be changed to simulate a GPU to CPU fallback; Identity (the engine package version, part of
/// the job version) ignores it. The model name and size are information only (a model switch does not re-transcribe).
/// </summary>
public sealed class FakeTranscriber : ITranscriber
{
    public Func<TranscriptionRequest, int, IReadOnlyList<RawSegment>> Respond { get; set; } = (_, _) => [];

    public List<TranscriptionRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public bool Ready { get; set; } = true;

    public string Engine { get; set; } = "fake";

    public string Identity { get; set; } = "fake";

    public string Model { get; set; } = "fake-model";

    public long? ModelBytes { get; set; } = 1000;

    public string Device { get; set; } = "fake";

    /// <summary>When set, "transcribe" and "reset" are added here in the order they happen.</summary>
    public List<string>? Events { get; set; }

    public int Resets { get; private set; }

    public void ResetModel()
    {
        Resets++;
        Events?.Add("reset");
    }

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(Ready);

    public Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var n = Requests.Count;
        Requests.Add(request);
        Events?.Add("transcribe");
        return Task.FromResult(Respond(request, n));
    }
}
