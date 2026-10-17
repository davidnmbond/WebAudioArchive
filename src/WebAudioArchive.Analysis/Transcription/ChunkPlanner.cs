using WebAudioArchive.Analysis.Audio;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>A stretch of the segment's audio sent to whisper in one request. Index is its place in the plan (resume key).</summary>
public sealed record AudioChunk(int Index, double Start, double End);

/// <summary>
/// Splits a segment into chunks of about chunkSeconds, each boundary moved to the quietest 50 ms window within
/// searchSeconds of the nominal point (ties go to the nearest), so a word is rarely cut in two. The plan depends only on
/// the audio, so a resumed job gets the same chunks. A tail shorter than chunkSeconds plus searchSeconds joins the last.
/// </summary>
public static class ChunkPlanner
{
    public static IReadOnlyList<AudioChunk> Plan(AudioFeatures f, double chunkSeconds, double searchSeconds)
    {
        var total = f.Seconds;
        var chunks = new List<AudioChunk>();
        if (total <= 0) return chunks;
        var w = f.WindowSeconds;
        var start = 0.0;
        while (total - start > chunkSeconds + searchSeconds)
        {
            var nominal = start + chunkSeconds;
            var lo = Math.Max((int)Math.Ceiling((start + 1) / w), (int)Math.Floor((nominal - searchSeconds) / w));
            var hi = Math.Min(f.WindowDbfsTenths.Count - 1, (int)Math.Ceiling((nominal + searchSeconds) / w));
            var best = nominal;
            var bestLevel = short.MaxValue;
            var bestDistance = double.MaxValue;
            for (var i = lo; i <= hi; i++)
            {
                var t = i * w;
                var level = f.WindowDbfsTenths[i];
                var distance = Math.Abs(t - nominal);
                if (level < bestLevel || (level == bestLevel && distance < bestDistance))
                    (best, bestLevel, bestDistance) = (t, level, distance);
            }

            chunks.Add(new AudioChunk(chunks.Count, start, best));
            start = best;
        }

        chunks.Add(new AudioChunk(chunks.Count, start, total));
        return chunks;
    }
}
