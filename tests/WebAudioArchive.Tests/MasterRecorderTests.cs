using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class MasterRecorderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-tests-" + Guid.NewGuid().ToString("N"));
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    MasterRecorder NewRecorder() => new(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance);

    static void Write(MasterRecorder r, int seq, DateTimeOffset at)
    {
        var frame = Mp3TestData.Frame(seq);
        Mp3FrameHeader.TryParse(frame, out var header);
        r.OnFrame(frame, in header, at);
    }

    string MasterDir => Path.Combine(_root, "burst", "master");

    IEnumerable<string> Files(string pattern) =>
        Directory.Exists(MasterDir)
            ? Directory.EnumerateFiles(MasterDir, pattern, SearchOption.AllDirectories).Order()
            : [];

    [Fact]
    public void Writes_a_part_file_then_finalises_with_a_sidecar()
    {
        var t = _clock.UtcNow;
        var r = NewRecorder();
        Write(r, 0, t);
        Write(r, 1, t.AddSeconds(1));

        var part = Assert.Single(Files("*.part"));
        Assert.EndsWith(Path.Combine("2026", "10-15", "103000.mp3.part"), part);

        r.Dispose();

        Assert.Empty(Files("*.part"));
        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(2), File.ReadAllBytes(mp3));

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal("burst", meta.Station);
        Assert.Equal("test-instance", meta.Instance);
        Assert.Equal(2, meta.Frames);
        Assert.Equal(Mp3TestData.Frames(2).Length, meta.Bytes);
        Assert.Equal("shutdown", meta.EndedBy);
        Assert.Equal(t, meta.StartUtc);
        Assert.Equal(t.AddSeconds(1), meta.EndUtc);
        Assert.Equal(2 * 1152.0 / 44100, meta.AudioSeconds, 6);
        Assert.Equal(new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), meta.Format);
    }

    [Fact]
    public void Rolls_at_the_hour_and_segments_concatenate_to_the_input()
    {
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 15, 10, 59, 59, 900, TimeSpan.Zero));
        Write(r, 1, new DateTimeOffset(2026, 10, 15, 11, 0, 0, 100, TimeSpan.Zero));
        Write(r, 2, new DateTimeOffset(2026, 10, 15, 11, 0, 1, 0, TimeSpan.Zero));
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith("105959.mp3", mp3s[0]);
        Assert.EndsWith("110000.mp3", mp3s[1]);
        Assert.Equal(Mp3TestData.Frames(3), mp3s.SelectMany(File.ReadAllBytes).ToArray());
        Assert.Equal("hour-boundary", SegmentMetaStore.Read(Path.ChangeExtension(mp3s[0], ".json"))!.EndedBy);
        Assert.Equal("shutdown", SegmentMetaStore.Read(Path.ChangeExtension(mp3s[1], ".json"))!.EndedBy);
    }

    [Fact]
    public void Rolls_at_utc_midnight_into_new_day_folder()
    {
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 15, 23, 59, 59, TimeSpan.Zero));
        Write(r, 1, new DateTimeOffset(2026, 10, 16, 0, 0, 1, TimeSpan.Zero));
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith(Path.Combine("10-15", "235959.mp3"), mp3s[0]);
        Assert.EndsWith(Path.Combine("10-16", "000001.mp3"), mp3s[1]);
    }

    [Fact]
    public void Clock_back_hour_does_not_collide()
    {
        // 25 Oct 2026: the UK clocks go back, so local 01:30 happens twice. UTC names stay distinct.
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)); // 01:30 BST
        Write(r, 1, new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)); // 01:30 GMT
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith("003000.mp3", mp3s[0]);
        Assert.EndsWith("013000.mp3", mp3s[1]);
    }

    [Fact]
    public void Restart_in_same_hour_makes_a_new_segment_and_in_same_second_a_distinct_file()
    {
        var t = _clock.UtcNow;
        var first = NewRecorder();
        Write(first, 0, t);
        first.Dispose();

        var second = NewRecorder();
        Write(second, 1, t.AddMinutes(15));
        second.Dispose();

        var third = NewRecorder();
        Write(third, 2, t); // exactly the first segment's start second
        third.Dispose();

        var names = Files("*.mp3").Select(Path.GetFileName).ToList();
        Assert.Equal(["103000-1.mp3", "103000.mp3", "104500.mp3"], names);
    }

    [Fact]
    public void Gap_and_titles_are_attached_to_the_segment_that_follows()
    {
        var t = _clock.UtcNow;
        var r = NewRecorder();
        r.OnGap(new GapRecord(t.AddSeconds(-30), t, "dropped"));
        Write(r, 0, t);
        r.OnTitle("Artist - Song", t.AddSeconds(1));
        Write(r, 1, t.AddSeconds(1));
        r.Dispose();

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(Files("*.mp3").Single(), ".json"))!;
        var gap = Assert.Single(meta.Gaps);
        Assert.Equal(30, gap.Seconds, 3);
        Assert.Equal("dropped", gap.Reason);
        var title = Assert.Single(meta.Titles);
        Assert.Equal("Artist - Song", title.Title);
        Assert.Equal(Mp3TestData.Frame(0).Length, title.AtFileByte);
    }

    [Fact]
    public void Crash_part_file_is_recovered_and_truncated_to_whole_frames()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "103045.mp3.part");
        File.WriteAllBytes(part, Mp3TestData.Frames(10).Concat(Mp3TestData.Frame(10).Take(100)).ToArray());

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        Assert.Empty(Files("*.part"));
        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(10), File.ReadAllBytes(mp3));
        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal("crash", meta.EndedBy);
        Assert.Equal(10, meta.Frames);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 30, 45, TimeSpan.Zero), meta.StartUtc);
    }

    [Fact]
    public void Empty_crash_part_file_is_removed()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "103045.mp3.part"), []);

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        Assert.Empty(Files("*.part"));
        Assert.Empty(Files("*.mp3"));
    }

    [Fact]
    public void Frames_written_counts_frames()
    {
        var r = NewRecorder();
        Write(r, 0, _clock.UtcNow);
        Write(r, 1, _clock.UtcNow);
        Assert.Equal(2, r.FramesWritten);
        r.Dispose();
    }

    [Fact]
    public void Failed_write_is_truncated_to_whole_frames_and_later_frames_append_cleanly()
    {
        FaultyStream? faulty = null;
        var r = new MasterRecorder(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance)
        {
            OpenPartStream = p => faulty = new FaultyStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024)),
        };
        var t = _clock.UtcNow;
        Write(r, 0, t);
        faulty!.FailNextWrite = true;
        Assert.Throws<IOException>(() => Write(r, 1, t.AddSeconds(1)));
        Write(r, 2, t.AddSeconds(2));
        r.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frame(0).Concat(Mp3TestData.Frame(2)).ToArray(), File.ReadAllBytes(mp3));
        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal(2, meta.Frames);
        Assert.Equal(Mp3TestData.Frame(0).Length + Mp3TestData.Frame(2).Length, meta.Bytes);
        Assert.Equal(2, r.FramesWritten);
    }

    [Fact]
    public void Crash_recovery_keeps_the_part_files_last_write_time_as_the_end()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "103045.mp3.part");
        File.WriteAllBytes(part, Mp3TestData.Frames(10).Concat(Mp3TestData.Frame(10).Take(100)).ToArray());
        var lastWrite = new DateTime(2026, 10, 15, 10, 41, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(part, lastWrite);

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(Assert.Single(Files("*.mp3")), ".json"))!;
        Assert.Equal(new DateTimeOffset(lastWrite, TimeSpan.Zero), meta.EndUtc);
    }

    [Fact]
    public void One_bad_part_file_does_not_stop_recovery_of_the_others()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "oddname.mp3.part"), Mp3TestData.Frames(3));
        File.WriteAllBytes(Path.Combine(dir, "103045.mp3.part"), Mp3TestData.Frames(4));

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.EndsWith("103045.mp3", mp3);
        Assert.Equal(Mp3TestData.Frames(4), File.ReadAllBytes(mp3));
        Assert.Equal("oddname.mp3.part", Path.GetFileName(Assert.Single(Files("*.part"))));
    }

    [Fact]
    public void Failed_close_leaves_a_part_file_that_the_next_start_recovers()
    {
        var r = new MasterRecorder(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance)
        {
            OpenPartStream = p => new FaultyStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024)) { ThrowOnDispose = true },
        };
        Write(r, 0, _clock.UtcNow);
        Write(r, 1, _clock.UtcNow.AddSeconds(1));
        r.Dispose(); // must not throw

        Assert.Empty(Files("*.mp3"));
        Assert.Single(Files("*.part"));

        var next = NewRecorder();
        next.RecoverCrashedSegments();
        next.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(2), File.ReadAllBytes(mp3));
        Assert.Equal("crash", SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!.EndedBy);
    }

    [Fact]
    public void Failed_rename_does_not_throw_and_keeps_the_part_file()
    {
        var r = NewRecorder();
        Write(r, 0, _clock.UtcNow);
        var final = Path.Combine(MasterDir, "2026", "10-15", "103000.mp3");
        File.WriteAllBytes(final, [1]); // makes the rename fail
        r.Dispose(); // must not throw

        Assert.Single(Files("*.part"));
        Assert.Empty(Files("*.json"));
    }

    sealed class FaultyStream(Stream inner) : Stream
    {
        public bool FailNextWrite { get; set; }
        public bool ThrowOnDispose { get; init; }

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                inner.Write(buffer[..(buffer.Length / 2)]); // a torn write
                throw new IOException("disk full");
            }

            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
            if (disposing && ThrowOnDispose) throw new IOException("close failed");
        }
    }
}
