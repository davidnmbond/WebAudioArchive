using System.Reflection;
using Whisper.net;
using Whisper.net.LibraryLoader;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// whisper.cpp in this process through Whisper.net (decision D18). The model is loaded once, on the first readiness
/// check, onto the GPU (CUDA runtime) or the CPU; one chunk is transcribed at a time (the service has one work lane).
/// Each chunk is decoded without the previous window's text as context (it feeds repetition loops over music) and with
/// the priming prompt carried into every 30 s window, so fillers are kept throughout the chunk. A missing model or a
/// native library that does not load means "try later" (the backlog and lag show it); an error on one chunk fails
/// that chunk.
/// </summary>
public sealed class WhisperNetTranscriber(string modelPath, WhisperDevice device, int threads, bool dtw) : ITranscriber, IDisposable
{
    static readonly string LibraryVersion =
        (typeof(WhisperFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    readonly SemaphoreSlim _gate = new(1, 1);
    WhisperFactory? _factory;

    public string Engine => RuntimeOptions.LoadedLibrary is { } lib
        ? $"whisper.net {LibraryVersion} ({lib.ToString().ToLowerInvariant()})"
        : $"whisper.net {LibraryVersion}";

    public string Model => Path.GetFileNameWithoutExtension(modelPath);

    /// <summary>The Whisper.net package version, the model name and the model file's size (or "missing"): the same before and after the model loads and on the GPU or the CPU.</summary>
    public string Identity
    {
        get
        {
            var file = new FileInfo(modelPath);
            return $"whisper.net {LibraryVersion}|{Model}|{(file.Exists ? file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing")}";
        }
    }

    static readonly string[] TransientMarkers = ["out of memory", "cudamalloc", "cuda error", "cublas", "failed to allocate", "ggml_cuda", "alloc_buffer"];

    /// <summary>Memory and GPU errors are transient (retried later, not counted against the segment); anything else fails this chunk.</summary>
    public static Exception Classify(Exception ex)
    {
        var text = ex.GetType().Name + ": " + ex.Message;
        if (ex is OutOfMemoryException || TransientMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return new TransientTranscriptionException($"whisper ran out of memory or hit a GPU error: {text}", ex);
        return new TranscriptionFailedException($"whisper could not transcribe this chunk: {text}");
    }

    /// <summary>Why the last readiness check failed, for the log; null when ready.</summary>
    public string? LastError { get; private set; }

    /// <summary>Native runtimes to try, in order. Auto tries CUDA and falls back to the CPU when no GPU or CUDA library is there.</summary>
    public static IReadOnlyList<RuntimeLibrary> RuntimeOrder(WhisperDevice d) => d switch
    {
        WhisperDevice.Cpu => [RuntimeLibrary.Cpu],
        WhisperDevice.Cuda => [RuntimeLibrary.Cuda],
        _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu],
    };

    /// <summary>The DTW alignment heads for a model file name, or null when the model has no preset here.</summary>
    public static WhisperAlignmentHeadsPreset? HeadsFor(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("large-v3-turbo", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.LargeV3Turbo;
        if (m.Contains("small.en", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.SmallEn;
        if (m.Contains("base.en", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.BaseEn;
        return null;
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            await FactoryAsync(ct);
            LastError = null;
            return true;
        }
        catch (TranscriberUnavailableException ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var factory = await FactoryAsync(ct);
        var builder = factory.CreateBuilder()
            .WithLanguage(request.Language)
            .WithThreads(threads)
            .WithTokenTimestamps()
            .WithProbabilities()
            .WithTemperature(0f)
            .WithTemperatureInc(0.2f)
            .WithNoContext();
        if (request.Prompt.Length > 0) builder = builder.WithPrompt(request.Prompt).WithCarryInitialPrompt(true);

        var segments = new List<RawSegment>();
        try
        {
            await using var processor = builder.Build();
            await foreach (var s in processor.ProcessAsync(new MemoryStream(request.Wav, writable: false), ct))
                segments.Add(WhisperResponse.FromSegment(s));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Classify(ex);
        }

        return segments;
    }

    async Task<WhisperFactory> FactoryAsync(CancellationToken ct)
    {
        if (_factory is { } ready) return ready;
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is { } loaded) return loaded;
            if (!File.Exists(modelPath))
                throw new TranscriberUnavailableException($"whisper model {modelPath} was not found; download it into models/ (docs/analysis.md) or set WAA_WHISPER_MODEL_PATH");
            var options = new WhisperFactoryOptions { UseGpu = device != WhisperDevice.Cpu, UseFlashAttention = !dtw, UseDtwTimeStamps = dtw };
            if (dtw)
                options.HeadsPreset = HeadsFor(Model) ?? throw new TranscriberUnavailableException($"WAA_WHISPER_DTW is on but {Model} has no DTW preset; use large-v3-turbo, small.en or base.en, or turn DTW off");

            RuntimeOptions.RuntimeLibraryOrder = [.. RuntimeOrder(device)];
            WhisperFactory? factory = null;
            try
            {
                factory = WhisperFactory.FromPath(modelPath, options);
                // Building a processor loads the native library and the model now, not in the middle of a job.
                await using (factory.CreateBuilder().WithLanguage("en").Build())
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                factory?.Dispose();
                throw new TranscriberUnavailableException($"whisper could not load {modelPath} ({device}): {ex.Message}", ex);
            }

            _factory = factory;
            return factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
