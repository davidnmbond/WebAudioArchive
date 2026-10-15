using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Worker;

public sealed class StationRecorderService(StationRegistry registry, ILoggerFactory loggers, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = registry.Stations.Where(s => s.Config.Enabled).Select(s => RunStationAsync(s, stoppingToken)).ToList();
        await Task.WhenAll(tasks);
    }

    async Task RunStationAsync(StationRuntime station, CancellationToken ct)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WebAudioArchive)");

        try
        {
            station.Recorder.RecoverCrashedSegments();
            var resolver = new StreamResolver(http, loggers.CreateLogger<StreamResolver>());
            var source = new HttpStreamSource(http, resolver, new Uri(station.Config.ListenUrl), loggers.CreateLogger<HttpStreamSource>());
            var session = new StationSession(source, station.Recorder, clock, station.Health, loggers.CreateLogger<StationSession>());
            await session.RunAsync(ct);
        }
        finally
        {
            station.Recorder.Dispose();
            http.Dispose();
        }
    }
}
