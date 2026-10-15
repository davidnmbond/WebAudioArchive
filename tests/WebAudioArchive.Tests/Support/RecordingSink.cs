using System.Collections.Concurrent;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Tests.Support;

public sealed class RecordingSink : IFrameSink
{
    public ConcurrentQueue<byte[]> Frames { get; } = new();
    public ConcurrentQueue<string> Titles { get; } = new();
    public ConcurrentQueue<GapRecord> Gaps { get; } = new();
    public int Connections;

    public void OnConnected(StreamInfo info, DateTimeOffset utc) => Interlocked.Increment(ref Connections);
    public void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc) => Frames.Enqueue(frame.ToArray());
    public void OnTitle(string title, DateTimeOffset utc) => Titles.Enqueue(title);
    public void OnGap(GapRecord gap) => Gaps.Enqueue(gap);
}
