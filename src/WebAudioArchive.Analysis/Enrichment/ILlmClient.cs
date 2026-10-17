using System.Text.Json;

namespace WebAudioArchive.Analysis.Enrichment;

public sealed record LlmRequest(string Model, string System, string User, JsonElement Schema, double Temperature, int Seed, int NumCtx, bool DisableThinking);

public sealed record LlmReply(string Content, int PromptTokens, int OutputTokens, double Seconds);

/// <summary>A local model: its name, the digest of the weights in use (recorded with every result) and whether it can think.</summary>
public sealed record LlmModel(string Name, string? Digest, bool Thinking);

/// <summary>The local LLM, behind an interface so tests use a fake and the runtime can change. Unreachable means try later.</summary>
public interface ILlmClient
{
    /// <summary>The model if it is installed, null if it is not.</summary>
    Task<LlmModel?> DescribeAsync(string model, CancellationToken ct);

    Task<LlmReply> ChatAsync(LlmRequest request, CancellationToken ct);
}

public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
