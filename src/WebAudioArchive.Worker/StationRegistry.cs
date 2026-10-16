using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Worker;

public sealed record StationRuntime(StationConfig Config, StationHealth Health, MasterRecorder Recorder)
{
    public StationCoverage Coverage { get; } = new();
}

public sealed class StationRegistry(string instanceId, IReadOnlyList<StationRuntime> stations)
{
    public string InstanceId { get; } = instanceId;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<StationRuntime> Stations { get; } = stations;
}
