using Whisper.net;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests;

public class TranscriptionTests
{
    static TranscriptionRequest Request => new(WavFile.Build(16000, 1, new byte[3200]), "Um, er, Burst Radio.", "en");

    /// <summary>A Whisper.net segment as the library returns it (built without the native library). Token times are centiseconds.</summary>
    static SegmentData Segment(double start, double end, string text, float noSpeech, params WhisperToken[] tokens) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0.1f, 1f, 0.9f, noSpeech, "en", tokens);

    static WhisperToken Token(string text, long startCs, long endCs, long dtwCs, float p, float logP) =>
        new() { Text = text, Start = startCs, End = endCs, DtwTimestamp = dtwCs, Probability = p, ProbabilityLog = logP };

    [Fact]
    public void Tokens_merge_into_words_with_dtw_start_punctuation_and_lowest_probability()
    {
        var seg = WhisperResponse.FromSegment(Segment(0.0, 2.4, " Um, welcome to Burst.", 0.02f,
            Token("[_BEG_]", 0, 0, -1, 0.8f, -0.22f),
            Token(" Um", 10, 30, 12, 0.80f, -0.22f),
            Token(",", 30, 32, -1, 0.95f, -0.05f),
            Token(" wel", 50, 70, -1, 0.90f, -0.11f),
            Token("come", 70, 90, -1, 0.70f, -0.36f),
            Token(" to", 100, 110, -1, 0.99f, -0.01f),
            Token(" Burst.", 120, 160, -1, 0.97f, -0.03f),
            Token("[_TT_120]", 160, 160, -1, 0.1f, -2.3f)));

        Assert.Equal("Um, welcome to Burst.", seg.Text);
        Assert.Equal(new[] { "Um,", "welcome", "to", "Burst." }, seg.Words.Select(w => w.Text));
        Assert.Equal(0.12, seg.Words[0].Start, 6);
        Assert.Equal(0.32, seg.Words[0].End, 6);
        Assert.Equal(0.70, seg.Words[1].Probability, 6);
        Assert.Equal(0.02, seg.NoSpeechProb, 6);
        Assert.Equal(-0.13, seg.AvgLogProb, 4);
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
    public async Task A_missing_model_is_not_ready_and_transcribing_waits_instead_of_failing()
    {
        using var t = new WhisperNetTranscriber(Path.Combine(Path.GetTempPath(), "no-such-model-waa.bin"), WhisperDevice.Auto, 4, dtw: false);

        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.Contains("WAA_WHISPER_MODEL_PATH", t.LastError);
        await Assert.ThrowsAsync<TranscriberUnavailableException>(() => t.TranscribeAsync(Request, CancellationToken.None));
        Assert.Equal("no-such-model-waa", t.Model);
        Assert.StartsWith("whisper.net 1.9.1", t.Engine);
    }

    [Theory]
    [InlineData(WhisperDevice.Auto, "Cuda,Cpu")]
    [InlineData(WhisperDevice.Cpu, "Cpu")]
    [InlineData(WhisperDevice.Cuda, "Cuda")]
    public void Runtime_order_follows_the_device_setting(WhisperDevice device, string expected) =>
        Assert.Equal(expected, string.Join(',', WhisperNetTranscriber.RuntimeOrder(device)));

    [Theory]
    [InlineData("ggml-large-v3-turbo-q8_0", "LargeV3Turbo")]
    [InlineData("ggml-small.en", "SmallEn")]
    [InlineData("ggml-base.en", "BaseEn")]
    [InlineData("ggml-medium", null)]
    public void Dtw_heads_follow_the_model_file(string model, string? expected) =>
        Assert.Equal(expected, WhisperNetTranscriber.HeadsFor(model)?.ToString());

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

    [Fact]
    public async Task The_identity_is_the_package_version_model_and_size_and_does_not_change_when_the_model_fails_to_load()
    {
        var dir = Directory.CreateTempSubdirectory("waa-model-").FullName;
        try
        {
            var model = Path.Combine(dir, "ggml-test.bin");
            await File.WriteAllBytesAsync(model, new byte[1000]);
            using var t = new WhisperNetTranscriber(model, WhisperDevice.Cpu, 1, dtw: false);
            var before = t.Identity;
            var engineBefore = t.Engine;

            Assert.False(await t.IsReadyAsync(CancellationToken.None));

            Assert.Equal(before, t.Identity);
            Assert.StartsWith("whisper.net 1.9.1|ggml-test|1000", before);
            Assert.DoesNotContain("cuda", before, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cpu", before, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("whisper.net 1.9.1", engineBefore);

            await File.WriteAllBytesAsync(model, new byte[2000]);
            Assert.NotEqual(before, t.Identity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("CUDA error: out of memory")]
    [InlineData("ggml_backend_cuda_buffer_type_alloc_buffer: allocating 5000 MiB on device 0: cudaMalloc failed")]
    [InlineData("failed to allocate buffer")]
    [InlineData("cuBLAS error 3")]
    public void Out_of_memory_and_cuda_errors_are_transient(string message) =>
        Assert.IsType<TransientTranscriptionException>(WhisperNetTranscriber.Classify(new InvalidOperationException(message)));

    [Fact]
    public void An_out_of_memory_exception_is_transient_and_other_errors_fail_the_chunk()
    {
        Assert.IsType<TransientTranscriptionException>(WhisperNetTranscriber.Classify(new OutOfMemoryException()));
        var other = Assert.IsType<TranscriptionFailedException>(WhisperNetTranscriber.Classify(new InvalidOperationException("bad audio")));
        Assert.Contains("bad audio", other.Message);
    }
}
