using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A transcriber for tests: Respond gets each request and its call number (0 based) and returns the segments, or throws
/// to simulate whisper being down (TranscriberUnavailableException), a bad input, or a crash.
/// </summary>
public sealed class FakeTranscriber : ITranscriber
{
    public Func<TranscriptionRequest, int, IReadOnlyList<RawSegment>> Respond { get; set; } = (_, _) => [];

    public List<TranscriptionRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public bool Ready { get; set; } = true;

    public string Engine => "fake";

    public string Model { get; set; } = "fake-model";

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(Ready);

    public Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var n = Requests.Count;
        Requests.Add(request);
        return Task.FromResult(Respond(request, n));
    }
}
