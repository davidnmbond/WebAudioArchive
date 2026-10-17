using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class DeadAirDetectorTests
{
    static readonly DeadAirOptions Defaults = new();

    static IReadOnlyList<DeadAirEvent> Detect(float[] mono, DeadAirOptions? o = null)
    {
        var fx = new FeatureExtractor(Signals.Rate, 1);
        fx.Push(mono);
        return DeadAirDetector.Detect(fx.Complete(), o ?? Defaults);
    }

    [Fact]
    public void Speech_with_pauses_and_breaths_is_not_dead_air()
    {
        Assert.Empty(Detect(Signals.SpeechLike(60, seed: 3)));
    }

    [Fact]
    public void Six_seconds_of_silence_between_tones_is_one_event_at_the_right_time()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(3), Signals.Silence(6), Signals.Sine(3))));
        Assert.Equal(3.0, e.StartSeconds, 2);
        Assert.Equal(9.0, e.EndSeconds, 2);
        Assert.Equal(-120, e.QuietestDbfs);
        Assert.Equal(Severities.Medium, DeadAirDetector.Severity(e, Defaults));
    }

    [Theory]
    [InlineData(4.0, 0)]
    [InlineData(5.0, 1)]
    public void The_minimum_length_is_inclusive(double silence, int expected)
    {
        Assert.Equal(expected, Detect(Signals.Concat(Signals.Sine(3), Signals.Silence(silence), Signals.Sine(3))).Count);
    }

    [Fact]
    public void Quiet_room_tone_is_not_dead_air_but_low_hiss_is()
    {
        Assert.Empty(Detect(Signals.Concat(Signals.Sine(1), Signals.Noise(10, -45, 1), Signals.Sine(1))));
        Assert.Single(Detect(Signals.Concat(Signals.Sine(1), Signals.Noise(10, -60, 1), Signals.Sine(1))));
    }

    [Fact]
    public void Silence_running_to_the_end_of_the_segment_is_reported()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(2), Signals.Silence(7))));
        Assert.Equal(9.0, e.EndSeconds, 2);
    }

    [Fact]
    public void Long_silence_is_high_severity()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(1), Signals.Silence(40), Signals.Sine(1))));
        Assert.Equal(Severities.High, DeadAirDetector.Severity(e, Defaults));
    }

    [Fact]
    public void Silence_at_the_start_of_the_segment_is_reported_from_zero()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Silence(6), Signals.Sine(3))));
        Assert.Equal(0.0, e.StartSeconds, 2);
        Assert.Equal(6.0, e.EndSeconds, 2);
    }

    [Fact]
    public void Quiet_music_just_under_the_minimum_length_is_not_dead_air()
    {
        // 4.9 s at -55 dBFS is below the -50 threshold but shorter than the 5 s minimum.
        Assert.Empty(Detect(Signals.Concat(Signals.Sine(2), Signals.Noise(4.9, -55, 2), Signals.Sine(2))));
    }

    [Fact]
    public void The_threshold_comes_from_config()
    {
        var quiet = Signals.Concat(Signals.Sine(1), Signals.Noise(10, -45, 1), Signals.Sine(1));
        Assert.Single(Detect(quiet, Defaults with { ThresholdDbfs = -40 }));
    }
}
