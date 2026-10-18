using System.Text.Json;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Reads whisper.cpp's server <c>verbose_json</c> (v1.9.5, measured in the D19 spike) into <see cref="RawSegment"/>s:
/// <c>segments[]</c> with <c>start</c>, <c>end</c>, <c>text</c>, <c>avg_logprob</c>, <c>no_speech_prob</c> and <c>words[]</c>,
/// which are whisper tokens (<c>word</c> with a leading space starts a word, <c>start</c> and <c>end</c> in seconds,
/// <c>t_dtw</c> in centiseconds or -1, <c>probability</c>). <see cref="WhisperResponse.MergeTokens"/> merges the tokens into
/// words exactly as it did for Whisper.net; the scores feed <see cref="TranscriptFilter"/> unchanged. Times are seconds from
/// the start of the chunk; the pipeline shifts them.
/// </summary>
public static class WhisperServerResponse
{
    /// <summary>The body is not the JSON this client expects.</summary>
    public sealed class FormatException(string message) : Exception(message);

    public static WhisperServerResult Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FormatException("the body is not JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
                throw new FormatException("the body has no \"segments\" array");

            var result = new List<RawSegment>();
            int withoutScores = 0, withoutWords = 0;
            foreach (var s in segments.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) throw new FormatException("a segment is not an object");
                var start = Number(s, "start") ?? 0;
                var end = Math.Max(start, Number(s, "end") ?? start);
                var text = (s.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)?.Trim() ?? "";
                var noSpeech = Number(s, "no_speech_prob");
                var avgLogProb = Number(s, "avg_logprob");
                if (noSpeech is null || avgLogProb is null) withoutScores++;

                var tokens = new List<WhisperResponse.Token>();
                var complete = false;
                if (s.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
                {
                    complete = words.GetArrayLength() > 0;
                    foreach (var w in words.EnumerateArray())
                    {
                        if (w.ValueKind != JsonValueKind.Object) throw new FormatException("a word is not an object");
                        var ws = Number(w, "start");
                        var we = Number(w, "end");
                        var p = Number(w, "probability");
                        if (ws is null || we is null || p is null) complete = false;
                        var word = w.TryGetProperty("word", out var wt) && wt.ValueKind == JsonValueKind.String ? wt.GetString() ?? "" : "";
                        tokens.Add(new WhisperResponse.Token(word, ws ?? 0, we ?? ws ?? 0, Number(w, "t_dtw") ?? -1, p ?? 1));
                    }
                }

                if (!complete) withoutWords++;
                result.Add(new RawSegment(start, end, text, Math.Round(noSpeech ?? 0, 4), Math.Round(avgLogProb ?? 0, 4), WhisperResponse.MergeTokens(tokens)));
            }

            return new WhisperServerResult(result, withoutScores, withoutWords);
        }
    }

    static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
