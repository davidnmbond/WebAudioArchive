using System.Text;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// Enriches a complete show (spec 3.7): the transcript of its window (show plus margins) goes to the local LLM in
/// parts that fit the context, each answer is validated against its part, and the claims are merged. Ollama
/// unreachable or the model missing: "pending", retried with backoff (not counted as an attempt). An invalid answer:
/// retried up to 3 times, then "failed" with the raw reply kept. Runs again only when the transcript, the prompt or
/// the model changes.
/// </summary>
public sealed class Enricher(AnalysisPaths paths, ILlmClient llm, EnrichmentPrompt prompt, EnrichmentOptions options, string model,
    IReadOnlyList<string> preference, IClock clock, ILogger log)
{
    public const int MaxInvalidAttempts = 3;

    public bool IsDue(ShowAnalysis show)
    {
        // Only finished shows with analysed speech: a scheduled show with no recording has nothing to enrich.
        if (!show.Complete || show.Sources.Count == 0) return false;
        var lines = Lines(show);
        if (lines.Count == 0) return false;
        var existing = Read(show);
        if (existing is null) return true;
        var fingerprint = Fingerprint(lines);
        var current = existing.Derived is { } d && d.TranscriptFingerprint == fingerprint && d.PromptVersion == prompt.Version && d.Model == model;
        if (existing.Status == EnrichmentStatus.Done) return !current;
        // Pending or failed for other input (transcript, prompt or model changed): try again now with a fresh attempt count.
        if (existing.Basis != Basis(fingerprint)) return true;
        if (existing.Status == EnrichmentStatus.Failed && existing.Attempts >= MaxInvalidAttempts) return false;
        return existing.NextAttemptUtc is null || existing.NextAttemptUtc <= clock.UtcNow;
    }

    public async Task<ShowEnrichment> EnrichAsync(ShowAnalysis show, CancellationToken ct)
    {
        var existing = Read(show);
        if (!IsDue(show) && existing is not null) return existing;

        var now = clock.UtcNow;
        var lines = Lines(show);
        var fingerprint = Fingerprint(lines);
        var basis = Basis(fingerprint);
        // Invalid-answer attempts count only against the same input and reset on success or any change of input.
        var priorAttempts = existing is not null && existing.Status != EnrichmentStatus.Done && existing.Basis == basis ? existing.Attempts : 0;
        ShowEnrichment result;
        string? raw = null;
        try
        {
            var info = await llm.DescribeAsync(model, ct)
                ?? throw new LlmUnavailableException($"Ollama has no model '{model}'; on the host run: ollama pull {model}");
            var parts = Split(lines, options.MaxTranscriptChars);
            var merged = new List<EnrichmentParts>();
            for (var i = 0; i < parts.Count; i++)
            {
                var user = prompt.User(show.Station, show.Title, show.StartUtc, show.EndUtc, i + 1, parts.Count, parts[i]);
                var reply = await llm.ChatAsync(new LlmRequest(model, prompt.System, user, prompt.Schema, options.Temperature, options.Seed, options.NumCtx, info.Thinking, options.MaxOutputTokens), ct);
                raw = reply.Content;
                merged.Add(EnrichmentValidator.Validate(reply.Content, parts[i], options.MaxItems, [show.Title, show.Station]));
                log.LogInformation("Enriched {Show} part {Part}/{Parts} in {Seconds:0.0} s ({Prompt} prompt and {Output} output tokens)",
                    show.ShowId, i + 1, parts.Count, reply.Seconds, reply.PromptTokens, reply.OutputTokens);
            }

            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, EnrichmentStatus.Done, 0, 0,
                null, null, now, Merge(merged, info, fingerprint, now), basis);
        }
        catch (LlmUnavailableException ex)
        {
            var unavailable = (existing?.Unavailable ?? 0) + 1;
            var wait = TimeSpan.FromMinutes(Math.Min(120, 5 * Math.Pow(2, unavailable - 1)));
            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, EnrichmentStatus.Pending, priorAttempts, unavailable,
                now + wait, ex.Message, now, existing?.Derived, basis);
            log.LogInformation("Enrichment of {Show} pending: {Error}", show.ShowId, ex.Message);
        }
        catch (EnrichmentInvalidException ex)
        {
            var attempts = priorAttempts + 1;
            var failed = attempts >= MaxInvalidAttempts;
            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, failed ? EnrichmentStatus.Failed : EnrichmentStatus.Pending, attempts,
                existing?.Unavailable ?? 0, failed ? null : now + TimeSpan.FromMinutes(5 * Math.Pow(2, attempts - 1)),
                ex.Message, now, existing?.Derived, basis);
            if (raw is not null)
            {
                var dir = paths.ShowDir(show.ShowId, show.StartUtc);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "enrichment.raw.txt"), raw, ct);
            }
            log.LogWarning("Enrichment of {Show} gave an invalid answer (attempt {Attempt}): {Error}", show.ShowId, attempts, ex.Message);
        }

        AnalysisJson.Write(Path.Combine(paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile), result);
        return result;
    }

    /// <summary>The stored enrichment status of a show, or null when it has none yet.</summary>
    public string? StatusOf(ShowAnalysis show) => Read(show)?.Status;

    ShowEnrichment? Read(ShowAnalysis show) =>
        AnalysisJson.TryRead<ShowEnrichment>(Path.Combine(paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile), out _);

    IReadOnlyList<TimedLine> Lines(ShowAnalysis show)
    {
        // A segment with a timeline gap has several sources with one JobId: read each transcript once.
        var docs = show.Sources
            .Select(s => s.JobId)
            .Distinct(StringComparer.Ordinal)
            .Select(job => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(paths.SegmentDir(job), AnalysisPaths.TranscriptFile), out _))
            .OfType<TranscriptDocument>()
            .ToList();
        return TranscriptSlicer.Slice(docs, show.WindowFromUtc, show.WindowToUtc, preference);
    }

    string Basis(string fingerprint) => fingerprint + "|" + prompt.Version + "|" + model;

    static string Fingerprint(IReadOnlyList<TimedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.StartUtc.UtcTicks).Append('|').Append(l.Text).Append('\n');
        return AnalysisConfigLoader.Hash(sb.ToString());
    }

    static List<IReadOnlyList<TimedLine>> Split(IReadOnlyList<TimedLine> lines, int maxChars)
    {
        var parts = new List<IReadOnlyList<TimedLine>>();
        var current = new List<TimedLine>();
        var size = 0;
        foreach (var l in lines)
        {
            if (current.Count > 0 && size + l.Text.Length + 12 > maxChars)
            {
                parts.Add(current);
                current = [];
                size = 0;
            }

            current.Add(l);
            size += l.Text.Length + 12;
        }

        if (current.Count > 0 || parts.Count == 0) parts.Add(current);
        return parts;
    }

    EnrichmentDerived Merge(IReadOnlyList<EnrichmentParts> parts, LlmModel info, string fingerprint, DateTimeOffset now)
    {
        List<DerivedClaim> Union(Func<EnrichmentParts, List<DerivedClaim>> pick) => parts
            .SelectMany(pick)
            .GroupBy(c => EnrichmentValidator.Normalise(c.Value))
            .Select(g => g.OrderByDescending(c => c.Confidence).First())
            .Take(options.MaxItems)
            .ToList();

        var songs = parts.SelectMany(p => p.Songs)
            .GroupBy(s => EnrichmentValidator.Normalise(s.Title))
            .Select(g => g.OrderByDescending(s => s.Artist is not null).ThenByDescending(s => s.Confidence).First())
            .Take(options.MaxItems)
            .ToList();
        var summaries = parts.Select(p => p.Summary).OfType<string>().Take(2).ToList();
        var language = parts.Select(p => p.Language).OfType<string>().GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        return new EnrichmentDerived(model, info.Digest, prompt.Version, now, fingerprint,
            Union(p => p.Hosts), Union(p => p.Guests), Union(p => p.Topics), songs, Union(p => p.Artists), Union(p => p.Locations), Union(p => p.CallsToAction),
            language, summaries.Count == 0 ? null : string.Join(' ', summaries), parts.Sum(p => p.Dropped), parts.SelectMany(p => p.DropReasons).ToList());
    }
}
