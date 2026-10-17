using WebAudioArchive.Analysis;

namespace WebAudioArchive.Analysis.Service;

/// <summary>A public type in this assembly, for WebApplicationFactory in the tests.</summary>
public sealed class AnalysisEntryPoint;

/// <summary>
/// Sweeps scratch left by a crash, then runs a pass every 60 s until stopped. A failed pass is logged, shown in /healthz and
/// retried; it never stops the service. On a stop the token is cancelled: the transcriber, the decoder and the Ollama client all
/// take it, so the job in hand is abandoned within seconds and left waiting (no attempt used). Run the container with a
/// stop grace period of at least 60 s (docker compose stop_grace_period), otherwise Docker kills it and the attempt counts.
/// </summary>
public sealed class AnalysisWorker(AnalysisRuntime runtime, ServiceLock serviceLock, ILogger<AnalysisWorker> log) : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(60);

    /// <summary>The host waits this long for the worker to stop; it is shorter than the 60 s the container is given.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // The lock file shows the maintenance commands that the service is running; refreshed during a long pass too.
        using var heartbeat = new Timer(_ => serviceLock.Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        try
        {
            var swept = runtime.Scratch.SweepStale();
            if (swept > 0) log.LogInformation("Removed {Count} leftover scratch entries", swept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not sweep scratch at start-up");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runtime.Runner.PassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analysis pass failed; trying again in {Seconds} s", PassInterval.TotalSeconds);
                runtime.Metrics.PassFailed($"{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(PassInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
