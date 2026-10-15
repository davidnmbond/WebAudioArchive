namespace WebAudioArchive.Core.Recording;

public sealed record SinkError(string Message, DateTimeOffset Utc);

public sealed class StationHealth
{
    long _lastByteTicks;
    long _lastFrameWrittenTicks;
    int _reconnects;
    volatile bool _connected;
    volatile SinkError? _lastError;

    public bool Connected
    {
        get => _connected;
        set => _connected = value;
    }

    public DateTimeOffset? LastByteUtc => ToTime(Interlocked.Read(ref _lastByteTicks));

    /// <summary>When a frame was last accepted by the recorder without error. This, not bytes received, is what health is based on.</summary>
    public DateTimeOffset? LastFrameWrittenUtc => ToTime(Interlocked.Read(ref _lastFrameWrittenTicks));

    /// <summary>The most recent recorder failure, cleared when a frame is next written successfully.</summary>
    public SinkError? LastError => _lastError;

    public int Reconnects => Volatile.Read(ref _reconnects);

    public void NoteByte(DateTimeOffset utc) => Interlocked.Exchange(ref _lastByteTicks, utc.UtcTicks);

    public void NoteFrameWritten(DateTimeOffset utc)
    {
        Interlocked.Exchange(ref _lastFrameWrittenTicks, utc.UtcTicks);
        _lastError = null;
    }

    public void NoteSinkError(string message, DateTimeOffset utc) => _lastError = new SinkError(message, utc);

    public void NoteReconnect() => Interlocked.Increment(ref _reconnects);

    static DateTimeOffset? ToTime(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}

/// <summary>The /healthz verdict, kept here so it can be tested without a host.</summary>
public static class HealthRules
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(120);

    /// <summary>True when an enabled station, after the grace period, has written no frame or none for too long.</summary>
    public static bool IsUnhealthy(bool enabled, TimeSpan uptime, DateTimeOffset? lastFrameWrittenUtc, DateTimeOffset now) =>
        enabled && uptime >= GracePeriod && (lastFrameWrittenUtc is null || now - lastFrameWrittenUtc.Value > MaxSilence);
}
