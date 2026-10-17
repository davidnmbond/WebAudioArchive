using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class FillerDetectorTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    static readonly FillerOptions Defaults = new();

    [Fact]
    public void Common_fillers_are_counted_with_their_times()
    {
        var r = FillerDetector.Detect(TranscriptFixtures.Of(Start, "Um, so, er, we're on, uh, Burst."), Defaults);

        Assert.Equal(new[] { "um", "er", "uh" }, r.Hits.Select(h => h.Canonical));
        Assert.Equal(1.0, r.Hits[1].Start);
        Assert.Equal(0.72, r.Hits[0].Confidence, 3);
    }

    [Theory]
    [InlineData("Ummm", "um")]
    [InlineData("uhh,", "uh")]
    [InlineData("Errr", "er")]
    [InlineData("Erm.", "erm")]
    [InlineData("Ahh", "ah")]
    [InlineData("Hmm?", "hmm")]
    [InlineData("hmmmm", "hmm")]
    public void Drawn_out_spellings_are_one_filler(string word, string canonical)
    {
        Assert.Equal(canonical, Assert.Single(FillerDetector.Detect(TranscriptFixtures.Of(Start, word), Defaults).Hits).Canonical);
    }

    [Theory]
    [InlineData("I like it like that")]
    [InlineData("umbrella error ahead hmmph")]
    [InlineData("the summer era")]
    public void Like_and_lookalike_words_are_not_fillers(string line)
    {
        Assert.Empty(FillerDetector.Detect(TranscriptFixtures.Of(Start, line), Defaults).Hits);
    }

    [Fact]
    public void Suppressed_segments_are_not_counted_and_do_not_add_speech_time()
    {
        var t = TranscriptFixtures.OfSegments(Start, ("um um um um", "repetition"), ("so um yes", null));

        var r = FillerDetector.Detect(t, Defaults);

        Assert.Single(r.Hits);
        Assert.Equal(1.5, r.SpeechSeconds, 6);
    }

    [Fact]
    public void You_know_is_counted_only_when_configured_and_only_as_a_filler()
    {
        var t = TranscriptFixtures.Of(Start, "it was, you know, brilliant", "do you know the way");

        Assert.Empty(FillerDetector.Detect(t, Defaults).Hits);
        var hit = Assert.Single(FillerDetector.Detect(t, Defaults with { CountYouKnow = true }).Hits);
        Assert.Equal("you know", hit.Canonical);
    }

    [Fact]
    public void You_know_after_a_comma_spans_both_words_and_takes_the_lower_probability()
    {
        var r = FillerDetector.Detect(TranscriptFixtures.Of(Start, "well, you know what"), Defaults with { CountYouKnow = true });

        var hit = Assert.Single(r.Hits);
        Assert.Equal("you know", hit.Canonical);
        Assert.Equal("you know", hit.Text);
        Assert.Equal(0.5, hit.Start);
        Assert.Equal(1.4, hit.End, 6);
        Assert.Equal(0.72, hit.Confidence, 3);
    }

    [Fact]
    public void Only_configured_words_count()
    {
        var r = FillerDetector.Detect(TranscriptFixtures.Of(Start, "um ah hmm"), Defaults with { Words = ["um"] });
        Assert.Equal("um", Assert.Single(r.Hits).Canonical);
    }
}
