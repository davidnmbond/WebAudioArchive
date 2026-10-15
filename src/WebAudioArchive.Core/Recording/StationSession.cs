using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Icy;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

public sealed record SessionOptions(TimeSpan StallTimeout, TimeSpan BackoffInitial, TimeSpan BackoffMax)
{
    public SessionOptions() : this(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)) { }
}

/// <summary>Holds one always-on connection to a station, reconnecting with backoff and recording every gap.</summary>
public sealed class StationSession(
    IStreamSource source,
    IFrameSink sink,
    IClock clock,
    StationHealth health,
    ILogger<StationSession> log,
    SessionOptions? options = null)
{
    readonly SessionOptions _options = options ?? new SessionOptions();
    DateTimeOffset? _lastDataUtc;
    DateTimeOffset? _gapStart;
    string _gapReason = "";
    bool _gotData;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = _options.BackoffInitial;
        while (!ct.IsCancellationRequested)
        {
            _gotData = false;
            var reason = "disconnected";
            var sinkFailed = false;
            try
            {
                await using var opened = await source.OpenAsync(ct).ConfigureAwait(false);
                Sink(() => sink.OnConnected(opened.Info, clock.UtcNow));
                health.Connected = true;
                await PumpAsync(opened, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SinkFailureException ex)
            {
                // The recorder failed, not the stream: say so, and keep growing the backoff so a permanently
                // failing sink (unwritable disk, disk full) is not retried every second.
                sinkFailed = true;
                var text = ex.InnerException?.Message ?? ex.Message;
                reason = "recorder write failed: " + (text.Length > 180 ? text[..180] : text);
                log.LogError(ex.InnerException ?? ex, "Recorder write failed: {Reason}", reason);
                health.NoteSinkError(reason, clock.UtcNow);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                log.LogWarning("Stream connection ended: {Reason}", reason);
            }

            health.Connected = false;
            health.NoteReconnect();
            if (_lastDataUtc is { } last && _gapStart is null)
            {
                _gapStart = last;
                _gapReason = reason;
            }

            var delay = _gotData && !sinkFailed ? _options.BackoffInitial : backoff;
            backoff = _gotData && !sinkFailed ? _options.BackoffInitial : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.BackoffMax.Ticks));
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        health.Connected = false;
    }

    async Task PumpAsync(OpenedStream opened, CancellationToken ct)
    {
        var icy = new IcyDemuxer(opened.Info.IcyMetaInt);
        var splitter = new Mp3FrameSplitter();
        var buffer = new byte[16 * 1024];
        var now = clock.UtcNow;

        var sinkFailed = false;
        FrameHandler onFrame = (ReadOnlySpan<byte> frame, in Mp3FrameHeader header) =>
        {
            try
            {
                sink.OnFrame(frame, in header, now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sinkFailed = true;
                throw new SinkFailureException(ex);
            }

            health.NoteFrameWritten(now);
        };
        AudioHandler onAudio = audio => splitter.Push(audio, onFrame);
        Action<IcyTitle> onTitle = t => Sink(() => sink.OnTitle(t.Title, now));

        try
        {
            while (true)
            {
                var n = await ReadWithStallTimeoutAsync(opened.Body, buffer, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("stream ended by server");

                now = clock.UtcNow;
                if (!_gotData)
                {
                    _gotData = true;
                    if (_gapStart is { } start)
                    {
                        Sink(() => sink.OnGap(new GapRecord(start, now, _gapReason)));
                        _gapStart = null;
                    }
                }

                _lastDataUtc = now;
                health.NoteByte(now);
                icy.Push(buffer.AsSpan(0, n), onAudio, onTitle);
            }
        }
        finally
        {
            // After a recorder failure the splitter may still hold the frame that failed. Emitting it again would
            // duplicate audio, so it is dropped with the connection.
            if (!sinkFailed) splitter.Complete(onFrame);
        }
    }

    static void Sink(Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new SinkFailureException(ex);
        }
    }

    sealed class SinkFailureException(Exception inner) : Exception(inner.Message, inner);

    async Task<int> ReadWithStallTimeoutAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_options.StallTimeout);
        try
        {
            return await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"no data for {_options.StallTimeout.TotalSeconds:0.##}s");
        }
    }
}
