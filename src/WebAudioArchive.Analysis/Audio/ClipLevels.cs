namespace WebAudioArchive.Analysis.Audio;

/// <summary>
/// The fixed levels (fractions of full scale) at which clipped-sample runs are kept in features.json, so the clipping
/// threshold can be retuned without decoding again. A threshold that is not one of these needs a new features format.
/// </summary>
public static class ClipLevels
{
    public static readonly double[] Levels = [0.98, 0.99, 0.999];

    /// <summary>Shorter runs are not kept: one sample at full scale is normal for loud material.</summary>
    public const int MinStoredRun = 2;

    /// <summary>Per level, per segment; beyond this the level is marked truncated (very loud or distorted audio).</summary>
    public const int MaxRunsPerLevel = 50_000;

    public static int IndexOf(double level)
    {
        for (var i = 0; i < Levels.Length; i++)
            if (Math.Abs(Levels[i] - level) < 1e-9) return i;
        return -1;
    }
}
