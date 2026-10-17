using System.Diagnostics;
using System.Globalization;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Analysis;

/// <summary>
/// --benchmark &lt;file.mp3&gt; [seconds]: decodes a copy of the file in scratch, transcribes its first N seconds (600 by
/// default) with the configured transcriber and prints the realtime factor. Writes no analysis output (issue #85).
/// Returns 2 for an unusable file (missing, or an open .part recording) and 1 when the transcriber is not ready.
/// </summary>
public static class Benchmark
{
    public static async Task<int> RunAsync(string mp3, double seconds, SegmentDecoder decoder, ScratchSpace scratch, ITranscriber transcriber,
        TranscriptionOptions o, TextWriter output, CancellationToken ct)
    {
        if (mp3.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"benchmark: {mp3} is an open recording (.part); give a closed segment");
            return 2;
        }

        if (!File.Exists(mp3))
        {
            output.WriteLine($"benchmark: {mp3} not found");
            return 2;
        }

        if (!await transcriber.IsReadyAsync(ct))
        {
            output.WriteLine($"benchmark: the transcriber ({transcriber.Engine} {transcriber.Model}) is not ready; check the model file (WAA_WHISPER_MODEL_PATH) and the device");
            return 1;
        }

        var length = new FileInfo(mp3).Length;
        using var lease = scratch.Acquire("benchmark", SegmentDecoder.ScratchBytesFor(length, length / 16000.0));
        var decodeClock = Stopwatch.StartNew();
        var decoded = await decoder.DecodeAsync(mp3, length, 44100, 2, lease, ct);
        var decodeSeconds = decodeClock.Elapsed.TotalSeconds;
        var wav = WavFile.ReadInfo(decoded.SpeechWavPath);
        var audio = Math.Min(seconds, wav.Seconds);
        var chunks = ChunkPlanner.Plan(decoded.Features, o.ChunkSeconds, o.BoundarySearchSeconds)
            .Where(c => c.Start < audio)
            .Select(c => c with { End = Math.Min(c.End, audio) })
            .ToList();

        double transcribe = 0;
        var words = 0;
        foreach (var c in chunks)
        {
            var bytes = WavFile.Slice(decoded.SpeechWavPath, wav, c.Start, c.End);
            var started = Stopwatch.GetTimestamp();
            var segments = await transcriber.TranscribeAsync(new TranscriptionRequest(bytes, PromptBuilder.Build(o, null), o.Language), ct);
            transcribe += Stopwatch.GetElapsedTime(started).TotalSeconds;
            words += segments.Sum(s => s.Words.Count);
        }

        var rtf = audio > 0 ? transcribe / audio : 0;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"benchmark: {transcriber.Engine} {transcriber.Model} on {transcriber.Device}: {audio:0.0} s of audio in {chunks.Count} chunks, decode {decodeSeconds:0.0} s, transcribe {transcribe:0.0} s, realtime factor {rtf:0.000} ({(rtf > 0 ? 1 / rtf : 0):0.0} times faster than real time), {words} words"));
        return 0;
    }
}
