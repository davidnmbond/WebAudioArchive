namespace WebAudioArchive.Core.Analysis;

public static class EnrichmentStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>A derived claim with its verbatim evidence quote and where the quote was said (UTC).</summary>
public sealed record DerivedClaim(string Value, string? Detail, double Confidence, string Quote, DateTimeOffset? QuoteUtc);

public sealed record DerivedSong(string Title, string? Artist, double Confidence, string Quote, DateTimeOffset? QuoteUtc);

/// <summary>
/// What the local LLM said about a show (spec 3.7), every claim backed by a quote found in the transcript. Schedule
/// facts stay authoritative; this is "derived" and can be regenerated. Derived hosts never grant access to anything:
/// they are suggestions for a manager.
/// </summary>
public sealed record EnrichmentDerived(
    string Model,
    string? ModelDigest,
    string PromptVersion,
    DateTimeOffset CreatedUtc,
    string TranscriptFingerprint,
    IReadOnlyList<DerivedClaim> Hosts,
    IReadOnlyList<DerivedClaim> Guests,
    IReadOnlyList<DerivedClaim> Topics,
    IReadOnlyList<DerivedSong> Songs,
    IReadOnlyList<DerivedClaim> Artists,
    IReadOnlyList<DerivedClaim> Locations,
    IReadOnlyList<DerivedClaim> CallsToAction,
    string? Language,
    string? Summary,
    int Dropped,
    IReadOnlyList<string> DropReasons);

/// <summary>enrichment.json beside show-analysis.json. Pending while Ollama is unreachable (Unavailable counts those tries) or after an invalid answer.</summary>
public sealed record ShowEnrichment(
    int Schema,
    string ShowId,
    string Status,
    int Attempts,
    int Unavailable,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    DateTimeOffset UpdatedUtc,
    EnrichmentDerived? Derived,
    string? Basis = null)
{
    public const int CurrentSchema = 1;
}
