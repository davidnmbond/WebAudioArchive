using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

/// <summary>
/// Writes a station's frames as gapless hourly segments aligned to UTC hours.
/// Layout: &lt;dataRoot&gt;/&lt;station&gt;/master/yyyy/MM-dd/HHmmss.mp3 with HHmmss.json beside it.
/// </summary>
public sealed class MasterRecorder(string dataRoot, string stationId, string instanceId, IClock clock, ILogger<MasterRecorder> log)
    : IFrameSink, IDisposable
{
    static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(10);

    readonly object _lock = new();
    readonly List<GapRecord> _pendingGaps = [];
    Segment? _segment;
    StreamInfo? _info;
    long _framesWritten;

    public string MasterDir { get; } = Path.Combine(dataRoot, stationId, "master");

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public DateTimeOffset? CurrentSegmentStartUtc
    {
        get
        {
            lock (_lock) return _segment?.StartUtc;
        }
    }

    public void OnConnected(StreamInfo info, DateTimeOffset utc)
    {
        lock (_lock) _info = info;
    }

    public void OnGap(GapRecord gap)
    {
        lock (_lock) _pendingGaps.Add(gap);
    }

    public void OnTitle(string title, DateTimeOffset utc)
    {
        lock (_lock) _segment?.Titles.Add(new SegmentTitle(title, _segment.Bytes, utc));
    }

    public void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc)
    {
        lock (_lock)
        {
            var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            if (_segment is not null && _segment.HourStart != hour) CloseSegment("hour-boundary");
            _segment ??= OpenSegment(utc, hour, header);

            if (_pendingGaps.Count > 0)
            {
                _segment.Gaps.AddRange(_pendingGaps);
                _pendingGaps.Clear();
            }

            _segment.Write(frame, header, utc, clock.UtcNow);
            Interlocked.Increment(ref _framesWritten);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_segment is not null) CloseSegment("shutdown");
        }
    }

    /// <summary>Finish any .part file left by a crash: cut it back to whole frames, rename it and write its sidecar.</summary>
    public void RecoverCrashedSegments()
    {
        if (!Directory.Exists(MasterDir)) return;
        foreach (var part in Directory.EnumerateFiles(MasterDir, "*.mp3.part", SearchOption.AllDirectories).ToList())
        {
            var bytes = File.ReadAllBytes(part);
            long good = 0;
            var frames = 0;
            double seconds = 0;
            Mp3FrameHeader? first = null;
            var splitter = new Mp3FrameSplitter();
            FrameHandler handler = (ReadOnlySpan<byte> f, in Mp3FrameHeader h) =>
            {
                good += f.Length;
                frames++;
                seconds += h.FrameSeconds;
                first ??= h;
            };
            splitter.Push(bytes, handler);
            splitter.Complete(handler);

            if (frames == 0)
            {
                File.Delete(part);
                log.LogWarning("Removed empty crashed segment {Part}", part);
                continue;
            }

            using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write)) fs.SetLength(good);

            var final = part[..^".part".Length];
            File.Move(part, final, overwrite: false);

            var start = StartFromPath(final);
            var end = new DateTimeOffset(File.GetLastWriteTimeUtc(final), TimeSpan.Zero);
            var h0 = first!.Value;
            SegmentMetaStore.Write(Path.ChangeExtension(final, ".json"), new SegmentMeta(
                1, instanceId, stationId, Path.GetFileName(final), start, end < start ? start : end, good, frames, seconds,
                new SegmentFormat(h0.Codec, h0.BitrateKbps, h0.SampleRateHz, h0.Channels), null, [], [], "crash"));
            log.LogWarning("Recovered crashed segment {File}: {Frames} frames", final, frames);
        }
    }

    static DateTimeOffset StartFromPath(string mp3Path)
    {
        var name = Path.GetFileName(mp3Path); // 103045.mp3 or 103045-1.mp3
        var dayDir = Path.GetFileName(Path.GetDirectoryName(mp3Path))!; // MM-dd
        var yearDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mp3Path)))!;
        return new DateTimeOffset(
            int.Parse(yearDir), int.Parse(dayDir[..2]), int.Parse(dayDir[3..5]),
            int.Parse(name[..2]), int.Parse(name[2..4]), int.Parse(name[4..6]), TimeSpan.Zero);
    }

    Segment OpenSegment(DateTimeOffset utc, DateTimeOffset hour, Mp3FrameHeader header)
    {
        var dir = Path.Combine(MasterDir, utc.ToString("yyyy"), utc.ToString("MM-dd"));
        Directory.CreateDirectory(dir);
        var stem = utc.ToString("HHmmss");
        var path = Path.Combine(dir, stem + ".mp3");
        for (var n = 1; File.Exists(path) || File.Exists(path + ".part"); n++) path = Path.Combine(dir, $"{stem}-{n}.mp3");
        log.LogInformation("Opening segment {Path}", path);
        return new Segment(path, hour, utc, header, clock.UtcNow);
    }

    void CloseSegment(string endedBy)
    {
        var s = _segment!;
        _segment = null;
        s.Dispose();
        File.Move(s.PartPath, s.FinalPath, overwrite: false);

        var h = s.FirstHeader;
        var stream = _info is null ? null : new SegmentStream(
            _info.Url.ToString(), _info.HowFound,
            _info.IcyHeaders.GetValueOrDefault("icy-name"), _info.IcyHeaders.GetValueOrDefault("icy-genre"), _info.IcyMetaInt);
        var gaps = s.Gaps.Select(g => new SegmentGap(g.StartUtc, g.EndUtc, (g.EndUtc - g.StartUtc).TotalSeconds, g.Reason)).ToList();
        SegmentMetaStore.Write(Path.ChangeExtension(s.FinalPath, ".json"), new SegmentMeta(
            1, instanceId, stationId, Path.GetFileName(s.FinalPath), s.StartUtc, s.LastFrameUtc, s.Bytes, s.Frames, s.Seconds,
            new SegmentFormat(h.Codec, h.BitrateKbps, h.SampleRateHz, h.Channels), stream, s.Titles, gaps, endedBy));
        log.LogInformation("Closed segment {Path}: {Frames} frames, {Bytes} bytes, ended by {EndedBy}", s.FinalPath, s.Frames, s.Bytes, endedBy);
    }

    sealed class Segment : IDisposable
    {
        readonly FileStream _fs;
        DateTimeOffset _lastFlush;

        public Segment(string finalPath, DateTimeOffset hourStart, DateTimeOffset startUtc, Mp3FrameHeader first, DateTimeOffset wallNow)
        {
            FinalPath = finalPath;
            PartPath = finalPath + ".part";
            HourStart = hourStart;
            StartUtc = startUtc;
            LastFrameUtc = startUtc;
            FirstHeader = first;
            _lastFlush = wallNow;
            _fs = new FileStream(PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        }

        public string FinalPath { get; }
        public string PartPath { get; }
        public DateTimeOffset HourStart { get; }
        public DateTimeOffset StartUtc { get; }
        public DateTimeOffset LastFrameUtc { get; private set; }
        public Mp3FrameHeader FirstHeader { get; }
        public long Bytes { get; private set; }
        public int Frames { get; private set; }
        public double Seconds { get; private set; }
        public List<SegmentTitle> Titles { get; } = [];
        public List<GapRecord> Gaps { get; } = [];

        public void Write(ReadOnlySpan<byte> frame, Mp3FrameHeader header, DateTimeOffset utc, DateTimeOffset wallNow)
        {
            _fs.Write(frame);
            Bytes += frame.Length;
            Frames++;
            Seconds += header.FrameSeconds;
            LastFrameUtc = utc;
            if (wallNow - _lastFlush >= FlushEvery)
            {
                _fs.Flush(flushToDisk: false);
                _lastFlush = wallNow;
            }
        }

        public void Dispose() => _fs.Dispose();
    }
}
