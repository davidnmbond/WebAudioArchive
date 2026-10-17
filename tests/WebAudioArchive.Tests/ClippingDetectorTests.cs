using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class ClippingDetectorTests
{
    const double OneSample = 1.0 / Signals.Rate;
    static readonly ClippingOptions Defaults = new();

    static AudioFeatures Features(float[] interleaved, int channels = 1)
    {
        var fx = new FeatureExtractor(Signals.Rate, channels);
        fx.Push(interleaved);
        return fx.Complete();
    }

    [Fact]
    public void Planted_runs_are_found_at_the_right_time_and_short_runs_are_not()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 0.5, 5), 1.5, 2);

        var result = ClippingDetector.Detect(Features(s), Defaults);

        var e = Assert.Single(result.Events);
        Assert.Equal(0.5, e.StartSeconds, OneSample * 2);
        Assert.Equal(0.5 + 5 * OneSample, e.EndSeconds, OneSample * 2);
        Assert.Equal(1, e.Runs);
        Assert.Equal(5, e.ClippedSamples);
        Assert.Equal(new[] { 0 }, e.Channels);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Threshold_is_inclusive_and_just_below_does_not_count()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 0.5, 4, 0.99f), 1.5, 4, 0.989f);
        var e = Assert.Single(ClippingDetector.Detect(Features(s), Defaults).Events);
        Assert.Equal(0.5, e.StartSeconds, OneSample * 2);
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(0.3, 2)]
    public void Runs_within_the_merge_window_become_one_event(double secondStart, int expectedEvents)
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 1.0, 3), 1.0 + secondStart, 3);
        var events = ClippingDetector.Detect(Features(s), Defaults).Events;
        Assert.Equal(expectedEvents, events.Count);
        if (expectedEvents == 1) Assert.Equal(2, events[0].Runs);
    }

    [Fact]
    public void Run_split_across_reads_counts_once()
    {
        var s = Signals.Stereo(Signals.Clip(Signals.Sine(1), 0.25, 50));
        var whole = Features(s, 2);

        var bySample = new FeatureExtractor(Signals.Rate, 2);
        foreach (var x in s) bySample.Push([x]);
        var bytes = new byte[s.Length * 4];
        Buffer.BlockCopy(s, 0, bytes, 0, bytes.Length);
        var byOddBytes = new FeatureExtractor(Signals.Rate, 2);
        for (var i = 0; i < bytes.Length; i += 3) byOddBytes.PushBytes(bytes.AsSpan(i, Math.Min(3, bytes.Length - i)));

        static IEnumerable<string> Shape(AudioFeatures f) => ClippingDetector.Detect(f, Defaults).Events
            .Select(e => $"{e.StartSeconds:R}|{e.EndSeconds:R}|{e.Runs}|{e.ClippedSamples}|{string.Join(',', e.Channels)}");

        foreach (var other in new[] { bySample.Complete(), byOddBytes.Complete() })
        {
            Assert.Equal(whole.WindowDbfsTenths, other.WindowDbfsTenths);
            Assert.Equal(whole.Clips.Select(c => c.Runs.Count), other.Clips.Select(c => c.Runs.Count));
            Assert.Equal(Shape(whole), Shape(other));
        }

        var wholeEvent = Assert.Single(ClippingDetector.Detect(whole, Defaults).Events);
        Assert.Equal(2, wholeEvent.Channels.Count);
        Assert.Equal(2, wholeEvent.Runs);
        Assert.Equal(100, wholeEvent.ClippedSamples);
    }

    [Fact]
    public void Overshoot_and_negative_full_scale_count()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(3), 0.5, 3, 1.2f), 2.0, 3, -1.0f);

        var events = ClippingDetector.Detect(Features(s), Defaults).Events;

        Assert.Equal(2, events.Count);
        Assert.Equal(1.2, events[0].Peak, 3);
        Assert.Equal(1.0, events[1].Peak, 3);
    }

    [Fact]
    public void Run_still_open_at_the_end_of_the_audio_is_kept()
    {
        var sine = Signals.Sine(1);
        var s = Signals.Clip(sine, 1.0 - 6 * OneSample, 6);

        var e = Assert.Single(ClippingDetector.Detect(Features(s), Defaults).Events);

        Assert.Equal(6, e.ClippedSamples);
        Assert.Equal(1.0, e.EndSeconds, OneSample * 2);
    }

    [Fact]
    public void Stereo_reports_the_clipped_channel()
    {
        var left = Signals.Sine(1);
        var right = Signals.Clip(Signals.Sine(1), 0.5, 10);
        var e = Assert.Single(ClippingDetector.Detect(Features(Signals.Stereo(left, right), 2), Defaults).Events);
        Assert.Equal(new[] { 1 }, e.Channels);
    }

    [Fact]
    public void Severity_follows_duration()
    {
        var shortRun = new ClipEvent(1, 1.01, 1, 400, 1, [0]);
        var longRun = new ClipEvent(1, 1.2, 3, 9000, 1, [0]);
        Assert.Equal(Severities.Low, ClippingDetector.Severity(shortRun, Defaults));
        Assert.Equal(Severities.Medium, ClippingDetector.Severity(longRun, Defaults));
    }

    [Fact]
    public void Window_levels_are_dbfs_and_silence_is_the_floor()
    {
        var f = Features(Signals.Concat(Signals.Sine(1, amp: 1.0), Signals.Silence(1)));

        Assert.Equal(40, f.WindowDbfsTenths.Count);
        Assert.Equal(-3.0, f.WindowDbfs(5), 0);
        Assert.Equal(-120, f.WindowDbfs(30));
        Assert.Equal(2.0, f.Seconds, 6);
    }

    [Fact]
    public void Window_timing_uses_the_real_window_length_at_rates_that_do_not_divide_by_twenty()
    {
        const int rate = 11025; // 0.05 s is 551.25 frames, so a window is 551 frames
        var s = Signals.Concat(Signals.Sine(600, rate: rate), Signals.Silence(6, rate), Signals.Sine(2, rate: rate));
        var fx = new FeatureExtractor(rate, 1);
        fx.Push(s);
        var f = fx.Complete();

        Assert.Equal(551.0 / rate, f.WindowSeconds, 12);

        var e = Assert.Single(DeadAirDetector.Detect(f, new DeadAirOptions()));
        Assert.Equal(600.0, e.StartSeconds, 0.1);
        Assert.Equal(606.0, e.EndSeconds, 0.1);
    }

    [Fact]
    public void Too_many_runs_marks_the_level_truncated_but_keeps_counting()
    {
        var n = ClipLevels.MaxRunsPerLevel + 1;
        var s = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            s[3 * i] = 1f;
            s[3 * i + 1] = 1f;
        }

        var f = Features(s);
        var level = f.Clips.Single(c => c.Level == 0.99);

        Assert.True(level.Truncated);
        Assert.Equal(n, level.Count);
        Assert.Equal(ClipLevels.MaxRunsPerLevel, level.Runs.Count);
        Assert.True(ClippingDetector.Detect(f, Defaults with { MinRunSamples = 2 }).Truncated);
    }
}
