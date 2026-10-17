namespace WebAudioArchive.Tests.Support;

/// <summary>Synthetic audio for detector tests: tones, silence, noise at a level, speech-like bursts, planted clipping. Mono unless made stereo.</summary>
public static class Signals
{
    public const int Rate = 44100;

    public static float[] Sine(double seconds, double hz = 440, double amp = 0.5, int rate = Rate)
    {
        var n = (int)Math.Round(seconds * rate);
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    public static float[] Silence(double seconds, int rate = Rate) => new float[(int)Math.Round(seconds * rate)];

    /// <summary>Uniform noise whose RMS level is <paramref name="dbfs"/>.</summary>
    public static float[] Noise(double seconds, double dbfs, int seed, int rate = Rate)
    {
        var rng = new Random(seed);
        var amp = Math.Pow(10, dbfs / 20) * Math.Sqrt(3);
        var s = new float[(int)Math.Round(seconds * rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (float)(amp * (rng.NextDouble() * 2 - 1));
        return s;
    }

    /// <summary>
    /// Syllable-like bursts (120 to 250 ms of noise under a smooth envelope, peak 0.3) separated by short pauses
    /// (50 to 500 ms of digital silence) and an occasional breath (0.8 to 1.5 s). Never a pause of 2 s or more.
    /// </summary>
    public static float[] SpeechLike(double seconds, int seed, int rate = Rate)
    {
        var rng = new Random(seed);
        var total = (int)Math.Round(seconds * rate);
        var s = new float[total];
        var i = 0;
        var syllables = 0;
        while (i < total)
        {
            var len = (int)(rate * (0.12 + rng.NextDouble() * 0.13));
            for (var k = 0; k < len && i + k < total; k++)
                s[i + k] = (float)(0.3 * Math.Sin(Math.PI * k / len) * (rng.NextDouble() * 2 - 1));
            i += len;
            var pause = ++syllables % 12 == 0 ? 0.8 + rng.NextDouble() * 0.7 : 0.05 + rng.NextDouble() * 0.45;
            i += (int)(rate * pause);
        }

        return s;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A copy with <paramref name="samples"/> consecutive samples set to <paramref name="level"/> from <paramref name="atSeconds"/>.</summary>
    public static float[] Clip(float[] signal, double atSeconds, int samples, float level = 1f, int rate = Rate)
    {
        var copy = (float[])signal.Clone();
        var start = (int)Math.Round(atSeconds * rate);
        for (var k = 0; k < samples; k++) copy[start + k] = level;
        return copy;
    }

    public static float[] Stereo(float[] left, float[] right)
    {
        var s = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            s[2 * i] = left[i];
            s[2 * i + 1] = right[i];
        }

        return s;
    }

    public static float[] Stereo(float[] mono) => Stereo(mono, mono);

    public static double Dbfs(float[] signal) => 10 * Math.Log10(signal.Average(x => (double)x * x));
}
