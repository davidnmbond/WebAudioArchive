using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests;

public class TranscriptionTests
{
    static WhisperResponse.Token Token(string text, double start, double end, double dtwCentiseconds, double p) => new(text, start, end, dtwCentiseconds, p);

    [Fact]
    public void Tokens_merge_into_words_with_dtw_start_punctuation_and_lowest_probability()
    {
        var words = WhisperResponse.MergeTokens([
            Token("[_BEG_]", 0, 0, -1, 0.8),
            Token(" Um", 0.10, 0.30, 12, 0.80),
            Token(",", 0.30, 0.32, -1, 0.95),
            Token(" wel", 0.50, 0.70, -1, 0.90),
            Token("come", 0.70, 0.90, -1, 0.70),
            Token(" to", 1.00, 1.10, -1, 0.99),
            Token(" Burst.", 1.20, 1.60, -1, 0.97),
            Token("[_TT_120]", 1.60, 1.60, -1, 0.1)]);

        Assert.Equal(new[] { "Um,", "welcome", "to", "Burst." }, words.Select(w => w.Text));
        Assert.Equal(0.12, words[0].Start, 6);
        Assert.Equal(0.32, words[0].End, 6);
        Assert.Equal(0.70, words[1].Probability, 6);
    }

    [Fact]
    public void Special_tokens_are_dropped_and_shift_moves_every_time()
    {
        var words = WhisperResponse.MergeTokens([
            new(" [_BEG_]", 0, 0, -1, 1), new(" Hi", 0.1, 0.2, -1, 0.9), new("<|endoftext|>", 0.2, 0.2, -1, 1)]);
        Assert.Equal("Hi", Assert.Single(words).Text);

        var shifted = new RawSegment(1, 2, "Hi", 0, 0, words).Shift(300);
        Assert.Equal(301, shifted.Start);
        Assert.Equal(300.1, shifted.Words[0].Start, 6);
    }

    [Fact]
    public void Known_hallucinations_annotations_and_repeats_are_suppressed()
    {
        var o = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json")).Config.Transcription;
        (string, double, double)[] segments =
        [
            ("[BLANK_AUDIO]", 0.1, -0.2),
            ("(music)", 0.1, -0.2),
            ("Thank you for watching!", 0.1, -0.2),
            ("Hello and welcome", 0.9, -1.5),
            ("Hello and welcome", 0.1, -0.2),
            ("Ooh baby", 0.1, -0.3),
            ("Ooh, baby!", 0.1, -0.3),
            ("ooh baby", 0.1, -0.3),
            ("Thank you.", 0.1, -0.2),
        ];

        var reasons = TranscriptFilter.Reasons(segments, o);

        Assert.Equal(new string?[]
        {
            TranscriptFilter.NonSpeech, TranscriptFilter.NonSpeech, TranscriptFilter.KnownHallucination, TranscriptFilter.NoSpeech,
            null, TranscriptFilter.Repetition, TranscriptFilter.Repetition, TranscriptFilter.Repetition, null,
        }, reasons);
    }

    static AudioFeatures Levels(double seconds, params (double At, short Tenths)[] quiet)
    {
        var n = (int)Math.Round(seconds / 0.05);
        var tenths = Enumerable.Repeat((short)-200, n).ToArray();
        foreach (var (at, value) in quiet) tenths[(int)Math.Round(at / 0.05)] = value;
        return new AudioFeatures(AudioFeatures.CurrentSchema, 44100, 1, (long)(seconds * 44100), 0.05, tenths, []);
    }

    [Fact]
    public void Chunks_end_at_the_quietest_moment_near_the_nominal_boundary()
    {
        var chunks = ChunkPlanner.Plan(Levels(20, (9.3, -600)), 10, 2);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(9.3, chunks[0].End, 6);
        Assert.Equal(9.3, chunks[1].Start, 6);
        Assert.Equal(20, chunks[1].End, 6);
    }

    [Fact]
    public void Even_audio_is_cut_at_the_nominal_boundaries_and_short_audio_is_one_chunk()
    {
        var chunks = ChunkPlanner.Plan(Levels(35), 10, 2);
        Assert.Equal(new[] { 0.0, 10, 20, 30 }, chunks.Select(c => Math.Round(c.Start, 6)));
        Assert.Equal(35, chunks[^1].End, 6);
        Assert.Equal(new[] { 0, 1, 2, 3 }, chunks.Select(c => c.Index));

        Assert.Single(ChunkPlanner.Plan(Levels(5), 10, 2));
        Assert.Empty(ChunkPlanner.Plan(Levels(0), 10, 2));
    }

    [Fact]
    public void The_prompt_primes_fillers_and_names_without_repeats_and_is_bounded()
    {
        var o = new TranscriptionOptions { VerbatimPrompt = "Um, er, right.", Glossary = ["Burst Radio", "Backseat Aux"] };

        Assert.Equal("Um, er, right. Burst Radio, Backseat Aux.", PromptBuilder.Build(o, "Backseat Aux"));
        Assert.Equal("Um, er, right. Burst Radio, Backseat Aux, Breakfast with Mackenzie.", PromptBuilder.Build(o, "Breakfast with Mackenzie"));
        Assert.Equal("Um, er, right.", PromptBuilder.Build(o with { Glossary = [] }, null));
        Assert.Equal(PromptBuilder.MaxChars, PromptBuilder.Build(o, new string('x', 2000)).Length);
    }
}
