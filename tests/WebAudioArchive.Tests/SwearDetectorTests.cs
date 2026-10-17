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

    [Theory]
    [InlineData("f***", 0.5)]
    [InlineData("f*ck", 0.5)]
    [InlineData("f***ing", 0.5)]
    [InlineData("s***", 0.3)]
    [InlineData("a***", 0.5)]
    [InlineData("t***", 0.5)]
    public void Masked_certainty_is_exact(string word, double certainty)
    {
        var m = List.Match(word);
        Assert.NotNull(m);
        Assert.True(m.Masked);
        Assert.Equal(certainty, m.Certainty);
    }

    [Theory]
    [InlineData("fucking-hell", SwearTiers.Severe, "fucking")]
    [InlineData("shit-hot", SwearTiers.Strong, "shit")]
    [InlineData("arse-licking", SwearTiers.Mild, "arse")]
    [InlineData("Bloody-hell,", SwearTiers.Mild, "bloody")]
    [InlineData("well-fucking-done", SwearTiers.Severe, "fucking")]
    [InlineData("bloody-fucking", SwearTiers.Severe, "fucking")]
    [InlineData("f***ing-hell", SwearTiers.Severe, "f***ing")]
    public void Hyphenated_compounds_match_on_their_highest_part(string word, string tier, string form)
    {
        var m = List.Match(word);
        Assert.NotNull(m);
        Assert.Equal(tier, m.Tier);
        Assert.Equal(form, m.Form);
    }

    [Theory]
    [InlineData("well-known")]
    [InlineData("ice-cold")]
    [InlineData("self-assessed")]
    [InlineData("Scunthorpe-based")]
    [InlineData("pricked-up")]
    [InlineData("-")]
    [InlineData("--")]
    public void Hyphenated_words_without_a_listed_part_do_not_match(string word)
    {
        Assert.Null(List.Match(word));
    }

    [Fact]
    public void A_hyphenated_swear_is_reported_once()
    {
        var t = TranscriptFixtures.Of(new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero), "that was fucking-hell loud");

        var hit = Assert.Single(SwearDetector.Detect(t, List));

        Assert.Equal("fucking-hell", hit.Text);
        Assert.Equal(2, hit.Word);
    }

    const string SyntheticJson = """{"version":"t1","suffixes":["s"],"tiers":{"strong":["knob"]},"allow":["knobs"]}""";

    [Fact]
    public void The_allow_list_is_the_only_thing_that_stops_a_listed_form()
    {
        var withAllow = SwearList.Parse(SyntheticJson, "test");
        var withoutAllow = SwearList.Parse(SyntheticJson.Replace("\"knobs\"", ""), "test");

        Assert.Null(withAllow.Match("knobs"));
        Assert.Null(withAllow.Match("Knobs!"));
        Assert.Null(withAllow.Match("k***s"));
        Assert.Null(withAllow.Match("knobs-up"));
        Assert.Equal("knob", withAllow.Match("knob")?.Form);

        Assert.Equal(SwearTiers.Strong, withoutAllow.Match("knobs")?.Tier);
        Assert.True(withoutAllow.Match("k***s")?.Masked);
        Assert.NotNull(withoutAllow.Match("knobs-up"));
    }

    [Fact]
    public void A_nan_word_probability_gives_zero_confidence_not_nan()
    {
        var t = TranscriptFixtures.Of(new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero), "oh shit");
        var seg = t.Segments[0];
        t = t with { Segments = [seg with { Words = seg.Words.Select(w => w with { Probability = double.NaN }).ToList() }] };

        var hit = Assert.Single(SwearDetector.Detect(t, List));

        Assert.Equal(0, hit.Confidence);
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

    [Fact]
    public void A_masked_word_spanning_tiers_is_scored_by_its_lowest_candidate_tier()
    {
        var m = List.Match("s***");
        Assert.NotNull(m);
        Assert.Equal(SwearTiers.Strong, m.Tier);
        Assert.Equal(SwearTiers.Mild, m.SeverityTier);
        Assert.Equal(new[] { SwearTiers.Mild, SwearTiers.Strong }, m.CandidateTiers);
        Assert.Equal(Severities.Low, SwearDetector.Severity(m.SeverityTier, false));
        Assert.Equal(Severities.Medium, SwearDetector.Severity(m.SeverityTier, true));

        var f = List.Match("f***");
        Assert.NotNull(f);
        Assert.Equal(SwearTiers.Severe, f.SeverityTier);
        Assert.Equal(new[] { SwearTiers.Severe }, f.CandidateTiers);

        var plain = List.Match("shit");
        Assert.NotNull(plain);
        Assert.Equal(SwearTiers.Strong, plain.SeverityTier);

        var t = TranscriptFixtures.Of(new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero), "well s*** happens");
        var hit = Assert.Single(SwearDetector.Detect(t, List));
        Assert.Equal(SwearTiers.Mild, hit.SeverityTier);
        Assert.Equal(new[] { SwearTiers.Mild, SwearTiers.Strong }, hit.CandidateTiers);
        Assert.Equal(0.27, hit.Confidence, 3);
    }

    [Fact]
    public void Suppressed_swearing_is_reported_once_per_run_at_low_confidence_and_not_counted_normally()
    {
        var start = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
        var t = TranscriptFixtures.OfSegments(start,
            ("well that was shit honestly", null),
            ("fuck fuck fuck fuck", "repetition"),
            ("fuck fuck", "repetition"),
            ("a clean line", null),
            ("bugger bugger bugger", "repetition"),
            ("nothing rude here", "repetition"));

        var normal = Assert.Single(SwearDetector.Detect(t, List));
        Assert.Equal("shit", normal.Form);
        Assert.Null(normal.SuppressedReason);
        Assert.Equal(1, normal.RunLength);

        var runs = SwearDetector.DetectSuppressed(t, List);

        Assert.Equal(2, runs.Count);
        var first = runs[0];
        Assert.Equal("repetition", first.SuppressedReason);
        Assert.Equal("fuck", first.Form);
        Assert.Equal(SwearTiers.Severe, first.Tier);
        Assert.Equal(6, first.RunLength);
        Assert.Equal(1, first.Segment);
        Assert.Equal(0, first.Word);
        Assert.Equal("fuck", first.Text);
        Assert.Equal(10.0, first.Start);
        Assert.Equal(20.9, first.End, 6);
        Assert.Equal(0.3, first.Confidence);
        Assert.False(first.Masked);

        var second = runs[1];
        Assert.Equal("bugger", second.Form);
        Assert.Equal(SwearTiers.Mild, second.Tier);
        Assert.Equal(3, second.RunLength);
        Assert.Equal(4, second.Segment);
        Assert.Equal(40.0, second.Start);
        Assert.Equal(41.4, second.End, 6);
        Assert.Equal(0.3, second.Confidence);
    }

    [Fact]
    public void Suppressed_runs_with_different_reasons_are_separate_and_the_highest_tier_names_the_run()
    {
        var t = TranscriptFixtures.OfSegments(new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero),
            ("bugger shit bugger", "repetition"),
            ("fuck", "hallucination"));

        var runs = SwearDetector.DetectSuppressed(t, List);

        Assert.Equal(2, runs.Count);
        Assert.Equal("shit", runs[0].Form);
        Assert.Equal(3, runs[0].RunLength);
        Assert.Equal("hallucination", runs[1].SuppressedReason);
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
