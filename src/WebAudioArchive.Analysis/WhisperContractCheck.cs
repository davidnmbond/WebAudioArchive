using System.Globalization;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Analysis;

/// <summary>
/// --check-whisper [file.wav]: a manual contract check of the configured whisper.cpp server, never part of the test run or of
/// CI (the server is on the owner's GPU servers, Task 23). It checks <c>GET /health</c>, then sends one clip through the same
/// client the service uses and checks the answer has <c>segments</c> with <c>avg_logprob</c> and <c>no_speech_prob</c> and
/// <c>words</c> with <c>start</c>, <c>end</c> and <c>probability</c>. Without a file the clip is 20 s of generated, speech-like
/// tones (real speech is not in the repository): a server may answer no segments for tones, which passes with a note. Give a
/// real 16 kHz mono 16-bit WAV (never commit it) to require segments. Exit 0 passed, 1 failed, 2 the file is unusable.
/// </summary>
public static class WhisperContractCheck
{
    public static async Task<int> RunAsync(WhisperServerTranscriber transcriber, string? wavPath, TextWriter output, CancellationToken ct)
    {
        byte[] wav;
        if (wavPath is null)
        {
            wav = SpeechLikeTones(20);
        }
        else
        {
            if (!File.Exists(wavPath))
            {
                output.WriteLine($"whisper-check: {wavPath} not found");
                return 2;
            }

            wav = await File.ReadAllBytesAsync(wavPath, ct);
        }

        if (!await transcriber.IsReadyAsync(ct))
        {
            output.WriteLine($"whisper-check: FAIL: {transcriber.LastError}");
            return 1;
        }

        output.WriteLine($"whisper-check: /health is ok ({transcriber.Engine}, device {transcriber.Device})");
        WhisperServerResult result;
        try
        {
            result = await transcriber.InspectAsync(new TranscriptionRequest(wav, "Um, er, right.", "en"), ct);
        }
        catch (Exception ex) when (ex is TransientTranscriptionException or TranscriptionFailedException)
        {
            output.WriteLine($"whisper-check: FAIL: {ex.Message}");
            return 1;
        }

        var problems = new List<string>();
        if (result.Segments.Count == 0 && wavPath is not null) problems.Add("the server returned no segments for the real audio file");
        if (result.SegmentsWithoutScores > 0) problems.Add($"{result.SegmentsWithoutScores} segments lack avg_logprob or no_speech_prob (the hallucination filter needs both: scores)");
        if (result.SegmentsWithoutWords > 0) problems.Add($"{result.SegmentsWithoutWords} segments lack words with start, end and probability (token_timestamps: words)");
        foreach (var w in result.Segments.SelectMany(s => s.Words))
        {
            if (w.End < w.Start || w.Probability is < 0 or > 1)
            {
                problems.Add("a word has an end before its start or a probability outside 0 to 1 (words)");
                break;
            }
        }

        if (problems.Count > 0)
        {
            foreach (var p in problems) output.WriteLine("whisper-check: FAIL: " + p);
            return 1;
        }

        var words = result.Segments.Sum(s => s.Words.Count);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"whisper-check: PASS: {result.Segments.Count} segments, {words} words{(result.Segments.Count == 0 ? " (no segments for the synthetic tones is expected; check real speech with a WAV file or the curl command in docs/analysis.md)" : "")}"));
        return 0;
    }

    /// <summary>Syllable-rate amplitude-modulated harmonics at about a speaking pitch: not speech, but audio a server must accept and decode.</summary>
    public static byte[] SpeechLikeTones(int seconds)
    {
        const int rate = 16000;
        var pcm = new byte[seconds * rate * 2];
        for (var i = 0; i < seconds * rate; i++)
        {
            var t = i / (double)rate;
            var envelope = Math.Max(0, Math.Sin(2 * Math.PI * 3.5 * t));
            var v = envelope * (0.5 * Math.Sin(2 * Math.PI * 140 * t) + 0.3 * Math.Sin(2 * Math.PI * 280 * t) + 0.2 * Math.Sin(2 * Math.PI * 1400 * t));
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(v * 8000));
        }

        return WavFile.Build(rate, 1, pcm);
    }
}
