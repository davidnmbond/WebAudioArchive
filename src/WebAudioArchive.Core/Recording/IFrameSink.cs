using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

public sealed record GapRecord(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

/// <summary>Receives what one station's stream produces. Calls come from one thread, in order.</summary>
public interface IFrameSink
{
    void OnConnected(StreamInfo info, DateTimeOffset utc);

    /// <summary>The span is valid only during the call.</summary>
    void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc);

    void OnTitle(string title, DateTimeOffset utc);

    /// <summary>Called when audio resumes after a break, just before the first frame that follows it.</summary>
    void OnGap(GapRecord gap);
}
