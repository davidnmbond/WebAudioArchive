using Microsoft.AspNetCore.Http;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>
/// The manual live contract check (--check-whisper, docs/analysis.md) is tested here against a stub server, so the check
/// itself is proven in CI; running it against the real server on pdl-rune-xx:8080 is an owner action (Task 23) and is never
/// part of the test run.
/// </summary>
public sealed class WhisperContractCheckTests
{
    static WhisperServerTranscriber Client(Uri url) =>
        new(WhisperServerTranscriber.CreateHttpClient(TimeSpan.FromMinutes(10)),
            new WhisperServerOptions(url, "ggml-large-v3-turbo-q8_0", null, TimeSpan.FromSeconds(30), 1, null, 0), null, (_, _) => Task.CompletedTask, () => 0);

    static async Task<(int Code, string Output)> Run(StubWhisperServer server, string? wavPath = null)
    {
        var output = new StringWriter();
        var code = await WhisperContractCheck.RunAsync(Client(server.BaseUrl), wavPath, output, CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task A_server_that_answers_the_contract_passes()
    {
        await using var server = await StubWhisperServer.StartAsync();

        var (code, output) = await Run(server);

        Assert.Equal(0, code);
        Assert.Contains("PASS", output);
        Assert.Contains("1 segments", output);
        var seen = Assert.Single(server.Seen);
        Assert.Equal("chunk.wav", seen.FileName);
        Assert.True(seen.File!.Length > 600_000, "the synthetic clip is 20 s of 16 kHz mono 16-bit audio");
    }

    [Fact]
    public async Task No_segments_for_the_synthetic_tones_is_a_pass_with_a_note_but_a_failure_for_a_real_file()
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = ctx => ctx.Response.WriteAsync("""{"segments":[]}""");
        var wav = Path.Combine(Path.GetTempPath(), "waa-check-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(wav, WebAudioArchive.Analysis.Audio.WavFile.Build(16000, 1, new byte[64000]));
        try
        {
            var (tones, toneOutput) = await Run(server);
            var (real, realOutput) = await Run(server, wav);

            Assert.Equal(0, tones);
            Assert.Contains("tones", toneOutput);
            Assert.Equal(1, real);
            Assert.Contains("FAIL", realOutput);
            Assert.Contains("no segments", realOutput);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData("""{"segments":[{"text":" Hi.","start":0,"end":1,"words":[{"word":" Hi","start":0,"end":1,"probability":0.9}]}]}""", "scores")]
    [InlineData("""{"segments":[{"text":" Hi.","start":0,"end":1,"avg_logprob":-0.1,"no_speech_prob":0.1}]}""", "words")]
    [InlineData("""{"segments":[{"text":" Hi.","start":0,"end":1,"avg_logprob":-0.1,"no_speech_prob":0.1,"words":[{"word":" Hi","start":0,"end":1}]}]}""", "words")]
    public async Task A_response_missing_scores_or_word_times_fails_and_says_which(string body, string expected)
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = ctx => ctx.Response.WriteAsync(body);

        var (code, output) = await Run(server);

        Assert.Equal(1, code);
        Assert.Contains("FAIL", output);
        Assert.Contains(expected, output);
    }

    [Fact]
    public async Task A_server_that_is_loading_or_down_fails_and_names_the_reason()
    {
        await using var loading = await StubWhisperServer.StartAsync();
        loading.Health = ctx =>
        {
            ctx.Response.StatusCode = 503;
            return Task.CompletedTask;
        };
        var (loadingCode, loadingOutput) = await Run(loading);

        var down = StubWhisperServer.ClosedPort();
        var output = new StringWriter();
        var downCode = await WhisperContractCheck.RunAsync(Client(down), null, output, CancellationToken.None);

        Assert.Equal(1, loadingCode);
        Assert.Contains("503", loadingOutput);
        Assert.Equal(1, downCode);
        Assert.Contains("FAIL", output.ToString());
        Assert.Equal(0, loading.Requests);
    }

    [Fact]
    public async Task A_server_error_fails_the_check_with_the_message()
    {
        await using var server = await StubWhisperServer.StartAsync();
        server.Inference = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return ctx.Response.WriteAsync("Invalid request");
        };

        var (code, output) = await Run(server);

        Assert.Equal(1, code);
        Assert.Contains("Invalid request", output);
    }
}
