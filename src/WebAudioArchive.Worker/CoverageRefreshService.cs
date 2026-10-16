using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Worker;

/// <summary>Works out each station's coverage figures at start and then once a minute, so /healthz never reads the disk.</summary>
public sealed class CoverageRefreshService(StationRegistry registry, IClock clock, ILogger<CoverageRefreshService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            foreach (var s in registry.Stations)
            {
                try
                {
                    await Task.Run(() => s.Coverage.Refresh(s.Recorder.MasterDir, clock.UtcNow), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Coverage refresh failed for {Station}", s.Config.Id);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
