using WebAudioArchive.Analysis.Enrichment;

namespace WebAudioArchive.Tests.Support;

/// <summary>A deterministic LLM for tests: Respond gets each request and its call number and returns the reply text.</summary>
public sealed class FakeLlm : ILlmClient
{
    public Func<LlmRequest, int, string> Respond { get; set; } = (_, _) => "{}";

    public List<LlmRequest> Requests { get; } = [];

    public bool Down { get; set; }

    public LlmModel? Model { get; set; } = new("qwen3.6:latest", "07d35212591fc27746f0a317c975a6d68754fb38e9053d82e25f06057af28522", true);

    public Task<LlmModel?> DescribeAsync(string model, CancellationToken ct) =>
        Down ? throw new LlmUnavailableException("Connection refused (host.docker.internal:11434)") : Task.FromResult(Model);

    public Task<LlmReply> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        if (Down) throw new LlmUnavailableException("Connection refused (host.docker.internal:11434)");
        var n = Requests.Count;
        Requests.Add(request);
        return Task.FromResult(new LlmReply(Respond(request, n), 1000, 200, 1.5));
    }
}
