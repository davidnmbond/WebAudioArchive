using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis;

public sealed record AnalysisRuntime(AnalysisRunner Runner, AnalysisMetrics Metrics, ScratchSpace Scratch, SegmentDecoder Decoder, ITranscriber Transcriber);

/// <summary>
/// Builds the service from its settings and config: whisper in process (Whisper.net, D18), Ollama over HTTP (no redirects,
/// timeout from WAA_OLLAMA_TIMEOUT_MINUTES), the saved schedule, the 5 minute show margin. Refuses to build (with an
/// <see cref="AnalysisSettingsException"/> that names the setting) when the scratch folder would contain a recorder's data or
/// the analysis output, or when the output folder is inside a recorder's data: scratch is swept and the archive must never
/// be written to.
/// </summary>
public static class AnalysisComposition
{
    public static readonly TimeSpan ShowMargin = TimeSpan.FromMinutes(5);

    public static AnalysisRuntime Build(AnalysisSettings s, LoadedConfig c, ILoggerFactory loggers, IClock clock)
    {
        var log = loggers.CreateLogger("WebAudioArchive.Analysis");
        foreach (var r in s.Recorders)
        {
            if (ScratchSpace.IsSameOrInside(Path.GetFullPath(s.OutputDir), Path.GetFullPath(r.Root)))
                throw new AnalysisSettingsException($"WAA_ANALYSIS_OUT: '{s.OutputDir}' is inside the data of recorder '{r.Id}' ({r.Root}); analysis output must never be written into a recorder's folder.");
        }

        var paths = new AnalysisPaths(s.OutputDir, s.Station);
        ScratchSpace scratch;
        try
        {
            scratch = new ScratchSpace(s.ScratchDir, s.ScratchMaxBytes, [.. s.Recorders.Select(r => r.Root), s.OutputDir]);
        }
        catch (ArgumentException ex)
        {
            throw new AnalysisSettingsException($"WAA_SCRATCH: {ex.Message}");
        }

        var decoder = new SegmentDecoder();
        ITranscriber transcriber = s.WhisperServer is { } server
            ? new WhisperServerTranscriber(WhisperServerTranscriber.CreateHttpClient(server.Timeout), server, loggers.CreateLogger("WebAudioArchive.Analysis.Whisper"))
            : new WhisperNetTranscriber(s.WhisperModelPath, s.WhisperDevice, s.WhisperThreads, s.WhisperDtw);
        var disk = new DiskGuard(s.OutputDir, s.MinFreeBytes);
        var schedule = new ScheduleProvider(s.ScheduleFile, s.StationsConfigPath, s.Station, log);
        var watershed = Watershed.From(c.Config.Swearing, schedule.Zone);
        var pipeline = new SegmentPipeline(paths, c, scratch, decoder, transcriber, disk, watershed, schedule.TitleAt, clock, log, s.Retranscribe);
        var shows = new ShowProcessor(paths, schedule, s.Preference, ShowMargin, clock, log);
        var enricher = s.EnrichmentEnabled
            ? new Enricher(paths, new OllamaClient(OllamaClient.CreateHttpClient(s.OllamaTimeout), s.OllamaUrl, s.OllamaKeepAlive),
                EnrichmentPrompt.Load(c.ConfigDir, c.Config.Enrichment), c.Config.Enrichment, s.OllamaModel, s.Preference, clock, log)
            : null;
        var metrics = new AnalysisMetrics(clock.UtcNow);
        var runner = new AnalysisRunner(new SegmentDiscovery(s.Recorders, s.Station, log), pipeline, shows, enricher, transcriber, disk, scratch,
            metrics, s.Preference, s.Since, clock, log);
        return new AnalysisRuntime(runner, metrics, scratch, decoder, transcriber);
    }
}
