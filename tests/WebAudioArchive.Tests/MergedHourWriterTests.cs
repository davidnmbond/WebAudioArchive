using System.Security.Cryptography;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>Issue #104: a merged hour is written as a .part, checked against its plan and its sources, and only then published with a sidecar.</summary>
public sealed class MergedHourWriterTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-hourwrite-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Epoch = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Root(string id) => Path.Combine(_root, id);

    string Merged => Path.Combine(_root, "merged", "burst", "master");

    string Mp3 => Path.Combine(Merged, "2026", "10-15", "100000.mp3");

    IReadOnlyList<SourceSegment> Scan(string id) => SourceSegment.Scan(Root(id), "burst", id);

    static int SeqAt(double second) => SegmentFiles.FramesFor((T0.AddSeconds(second) - Epoch).TotalSeconds);

    void Hours(string id, int hours = 3, (int From, int To)? silence = null, params SegmentGap[] gaps)
    {
        for (var h = -1; h < hours - 1; h++)
        {
            var start = T0.AddHours(h);
            var end = start.AddHours(1);
            SegmentFiles.Live(Root(id), "burst", Epoch, start, end, [.. gaps.Where(g => g.StartUtc < end && g.EndUtc > start)], silence: silence);
        }
    }

    void Skew(string id, double seconds)
    {
        foreach (var json in Directory.EnumerateFiles(Root(id), "*.json", SearchOption.AllDirectories))
        {
            var meta = SegmentMetaStore.Read(json)!;
            SegmentMetaStore.Write(json, meta with { StartUtc = meta.StartUtc.AddSeconds(seconds), EndUtc = meta.EndUtc.AddSeconds(seconds) });
        }
    }

    static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    MergedHourPlan PlanMixed()
    {
        var gap = new SegmentGap(T0.AddSeconds(200), T0.AddSeconds(230), 30, "end-of-stream");
        Hours("a", gaps: gap);
        Hours("b");
        var plan = HourMerger.Plan(T0, Scan("a"), Scan("b"), []);
        Assert.NotNull(plan);
        return plan;
    }

    [Fact]
    public void A_written_hour_is_whole_frames_matching_the_plan_with_a_sidecar_naming_every_source_and_checksum()
    {
        var plan = PlanMixed();

        var meta = MergedHourWriter.Write(plan, Merged, "burst");

        Assert.True(File.Exists(Mp3));
        Assert.False(File.Exists(Mp3 + ".part"));
        var read = SegmentMetaStore.Read(Path.ChangeExtension(Mp3, ".json"))!;
        Assert.Equal(meta.Merge!.Sha256, read.Merge!.Sha256);
        using (var fs = File.OpenRead(Mp3))
        {
            var frames = FrameIndex.Read(fs, 0, fs.Length);
            Assert.Equal(plan.FrameHashes, frames.Select(f => f.Hash));
            Assert.Equal(fs.Length, frames.Sum(f => (long)f.Length));
            Assert.Equal(frames.Count, read.Frames);
        }

        Assert.Equal("merged", read.Instance);
        Assert.Equal("burst", read.Station);
        Assert.Equal("100000.mp3", read.File);
        Assert.Equal("hour-boundary", read.EndedBy);
        Assert.Equal(new FileInfo(Mp3).Length, read.Bytes);
        Assert.Equal(plan.Pieces[0].FromUtc, read.StartUtc);
        Assert.Equal(plan.Pieces[^1].ToUtc, read.EndUtc);
        Assert.Equal(plan.Pieces[0].Source.Meta.Format, read.Format);
        Assert.InRange(read.AudioSeconds, 3590, 3610);
        Assert.Equal(plan.Missing.Select(m => (m.StartUtc, m.EndUtc, m.Reason)), read.Gaps.Select(m => (m.StartUtc, m.EndUtc, m.Reason)));
        var merge = read.Merge!;
        Assert.Equal(Sha(Mp3), merge.Sha256);
        Assert.Equal(
            plan.Pieces.Select(p => (p.Recorder, p.Source.RelativeFile, p.FirstFrame, p.Frames)),
            merge.Sources.Select(s => (s.Recorder, s.File, s.FirstFrame, s.FrameCount)));
        Assert.Equal(plan.Pieces.Where(p => p.Recorder == "a").Sum(p => p.Frames), merge.FramesFromA);
        Assert.Equal(plan.Pieces.Where(p => p.Recorder == "b").Sum(p => p.Frames), merge.FramesFromB);
        Assert.Equal(read.Frames, merge.FramesFromA + merge.FramesFromB);
        Assert.Contains(plan.Pieces, p => p.Recorder == "b");
        var distinct = plan.Pieces.Select(p => (p.Recorder, p.Source)).Distinct().ToList();
        Assert.Equal(distinct.Count, merge.Inputs.Count);
        foreach (var input in merge.Inputs)
        {
            var src = distinct.Single(d => d.Recorder == input.Recorder && d.Source.RelativeFile == input.File).Source;
            Assert.Equal(Sha(src.AudioPath), input.Sha256);
            Assert.Equal(new FileInfo(src.AudioPath).Length, input.Bytes);
        }
    }

    [Fact]
    public void A_source_changed_after_planning_fails_verification_and_leaves_no_file()
    {
        var plan = PlanMixed();
        File.AppendAllBytes(plan.Pieces[0].Source.AudioPath, [1, 2, 3, 4]);

        var ex = Assert.Throws<MergeVerificationException>(() => MergedHourWriter.Write(plan, Merged, "burst"));

        Assert.Contains("source", ex.Message);
        Assert.False(File.Exists(Mp3));
        Assert.False(File.Exists(Mp3 + ".part"));
        Assert.False(File.Exists(Path.ChangeExtension(Mp3, ".json")));
    }

    [Fact]
    public void A_leftover_part_from_an_interrupted_run_is_replaced()
    {
        var plan = PlanMixed();
        Directory.CreateDirectory(Path.GetDirectoryName(Mp3)!);
        File.WriteAllBytes(Mp3 + ".part", new byte[500_000]);

        MergedHourWriter.Write(plan, Merged, "burst");

        Assert.False(File.Exists(Mp3 + ".part"));
        using var fs = File.OpenRead(Mp3);
        Assert.Equal(plan.FrameHashes, FrameIndex.Read(fs, 0, fs.Length).Select(f => f.Hash));
    }

    [Fact]
    public void Tail_is_the_trailing_run_of_identical_frames_and_the_frames_before_it()
    {
        var silence = (SeqAt(3590), SeqAt(3700));
        Hours("a", silence: silence);
        var plan = HourMerger.Plan(T0, Scan("a"), [], [])!;
        MergedHourWriter.Write(plan, Merged, "burst");

        var tail = MergedHourWriter.Tail(Mp3);

        var run = plan.FrameHashes.Reverse().TakeWhile(h => h == plan.FrameHashes[^1]).Count();
        Assert.True(run > 8);
        Assert.Equal(run + 8, tail.Count);
        Assert.Equal(plan.FrameHashes.TakeLast(run + 8), tail);
        Assert.NotEqual(tail[0], plan.FrameHashes[^1]);
    }

    [Fact]
    public void Hours_joined_inside_a_long_silence_repeat_and_drop_nothing_with_the_full_tail()
    {
        // 110 s of silence straddling 11:00, so the boundary falls well inside a run of identical frames. The first hour
        // is merged from B alone (clock 0.4 s ahead, so it ends early by A's clock), the second from A and B.
        var silence = (SeqAt(3590), SeqAt(3700));
        Hours("a", hours: 4, silence: silence);
        Hours("b", hours: 4, silence: silence);
        Skew("b", 0.4);

        var first = HourMerger.Plan(T0, [], Scan("b"), [])!;
        MergedHourWriter.Write(first, Merged, "burst");
        var second = HourMerger.Plan(T0.AddHours(1), Scan("a"), Scan("b"), MergedHourWriter.Tail(Mp3))!;

        Assert.Equal("a", second.Pieces[0].Recorder);
        var one = SegmentFiles.Seqs(File.ReadAllBytes(Mp3));
        var two = SegmentFiles.Seqs([.. second.Pieces.SelectMany(p => File.ReadAllBytes(p.Source.AudioPath)[(int)p.StartByte..(int)p.EndByte])]);
        Assert.Equal(SegmentFiles.SilenceSeq, one[^1]);
        Assert.Equal(SegmentFiles.SilenceSeq, two[0]);
        var joined = one.Concat(two).ToList();
        var expected = Enumerable.Range(one[0], joined.Count)
            .Select(n => n >= silence.Item1 && n < silence.Item2 ? SegmentFiles.SilenceSeq : n);
        Assert.Equal(expected, joined);
    }
}
