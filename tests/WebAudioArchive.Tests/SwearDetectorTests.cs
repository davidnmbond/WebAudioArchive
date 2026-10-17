using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class SwearDetectorTests
{
    static readonly SwearList List = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json")).Swears;
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly Watershed Uk = Watershed.From(new SwearOptions(), London);

    [Theory]
    [InlineData("Shit!", SwearTiers.Strong, false)]
    [InlineData("FUCKED", SwearTiers.Severe, false)]
    [InlineData("fuckin'", SwearTiers.Severe, false)]
    [InlineData("Motherfuckers,", SwearTiers.Severe, false)]
    [InlineData("bull-shit", SwearTiers.Strong, false)]
    [InlineData("bitches", SwearTiers.Strong, false)]
    [InlineData("pissed", SwearTiers.Mild, false)]
    [InlineData("Bloody", SwearTiers.Mild, false)]
    [InlineData("f***ing", SwearTiers.Severe, true)]
    [InlineData("f*ck", SwearTiers.Severe, true)]
    [InlineData("s***", SwearTiers.Strong, true)]
    public void Inflections_punctuation_and_masks_match(string word, string tier, bool masked)
    {
        var m = List.Match(word);
        Assert.NotNull(m);
        Assert.Equal(tier, m.Tier);
        Assert.Equal(masked, m.Masked);
        Assert.True(masked ? m.Certainty < 1 : m.Certainty == 1);
    }

    [Theory]
    [InlineData("Scunthorpe")]
    [InlineData("Dickens")]
    [InlineData("assess")]
    [InlineData("cocktail")]
    [InlineData("shitake")]
    [InlineData("knobs")]
    [InlineData("pricked")]
    [InlineData("class")]
    [InlineData("hello")]
    [InlineData("****")]
    [InlineData("...")]
    [InlineData("")]
    public void Near_misses_do_not_match(string word)
    {
        Assert.Null(List.Match(word));
    }

    [Fact]
    public void Hits_carry_tier_time_confidence_and_context_and_suppressed_segments_are_ignored()
    {
        var start = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
        var t = TranscriptFixtures.OfSegments(start,
            ("Well that was shit, honestly it was", null),
            ("fuck fuck fuck fuck", "repetition"),
            ("a lovely clean sentence", null));

        var hit = Assert.Single(SwearDetector.Detect(t, List));

        Assert.Equal(0, hit.Segment);
        Assert.Equal(3, hit.Word);
        Assert.Equal("shit,", hit.Text);
        Assert.Equal("shit", hit.Form);
        Assert.Equal(SwearTiers.Strong, hit.Tier);
        Assert.Equal(1.5, hit.Start);
        Assert.Equal(0.9, hit.Confidence, 3);
        Assert.Equal("Well that was shit, honestly it was", hit.Context);
    }

    [Theory]
    [InlineData("2026-10-24T19:59:00Z", true)]   // 20:59 BST
    [InlineData("2026-10-24T20:00:00Z", false)]  // 21:00 BST, the watershed starts
    [InlineData("2026-10-24T04:29:00Z", false)]  // 05:29 BST
    [InlineData("2026-10-24T04:30:00Z", true)]   // 05:30 BST, the watershed ends
    [InlineData("2026-10-24T23:30:00Z", false)]  // 00:30 BST on the 25th, a different local day
    [InlineData("2026-10-25T00:30:00Z", false)]  // 01:30 BST, the first 01:30 of the change night
    [InlineData("2026-10-25T01:30:00Z", false)]  // 01:30 GMT, the second
    [InlineData("2026-10-25T05:30:00Z", true)]   // 05:30 GMT
    [InlineData("2026-10-26T20:30:00Z", true)]   // 20:30 GMT, which in summer would have been after the watershed
    [InlineData("2026-10-26T21:00:00Z", false)]  // 21:00 GMT
    [InlineData("2027-03-28T00:30:00Z", false)]  // 00:30 GMT before the spring change
    [InlineData("2027-03-28T04:30:00Z", true)]   // 05:30 BST after it
    public void Watershed_follows_uk_local_time_across_the_clock_change(string utc, bool before)
    {
        Assert.Equal(before, Uk.IsBeforeWatershed(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Watershed_start_in_utc_moves_with_the_clocks()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), Uk.StartUtcOn(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 21, 0, 0, TimeSpan.Zero), Uk.StartUtcOn(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(SwearTiers.Mild, false, Severities.Low)]
    [InlineData(SwearTiers.Mild, true, Severities.Medium)]
    [InlineData(SwearTiers.Strong, false, Severities.Medium)]
    [InlineData(SwearTiers.Strong, true, Severities.High)]
    [InlineData(SwearTiers.Severe, false, Severities.High)]
    [InlineData(SwearTiers.Severe, true, Severities.High)]
    public void Severity_rises_before_the_watershed(string tier, bool before, string severity)
    {
        Assert.Equal(severity, SwearDetector.Severity(tier, before));
    }

    [Theory]
    [InlineData("Fuckin'", "fuckin")]
    [InlineData("bull-shit", "bullshit")]
    [InlineData("F***!", "f***")]
    [InlineData("Café", "caf")]
    public void Normalising_keeps_letters_digits_and_stars(string raw, string expected)
    {
        Assert.Equal(expected, WordNormaliser.Normalise(raw));
    }
}
