namespace WebAudioArchive.Core.Recording;

public sealed class StationHealth
{
    long _lastByteTicks;
    int _reconnects;
    volatile bool _connected;

    public bool Connected
    {
        get => _connected;
        set => _connected = value;
    }

    public DateTimeOffset? LastByteUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastByteTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    public int Reconnects => Volatile.Read(ref _reconnects);

    public void NoteByte(DateTimeOffset utc) => Interlocked.Exchange(ref _lastByteTicks, utc.UtcTicks);

    public void NoteReconnect() => Interlocked.Increment(ref _reconnects);
}
