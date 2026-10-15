using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests.Support;

public sealed class ManualClock(DateTimeOffset start) : IClock
{
    long _ticks = start.UtcTicks;
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public void Set(DateTimeOffset to) => Interlocked.Exchange(ref _ticks, to.UtcTicks);
}
