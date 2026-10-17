namespace WebAudioArchive.Core.Analysis;

/// <summary>
/// The A-before-B rule shared by every reader: where a better-ranked recorder's analysed audio covers an instant, results
/// from a worse-ranked recorder at that instant are dropped. Recorders not in the preference list rank last.
/// </summary>
public static class RecorderPreference
{
    public static int Rank(string recorder, IReadOnlyList<string> preference)
    {
        for (var i = 0; i < preference.Count; i++)
            if (preference[i] == recorder) return i;
        return preference.Count;
    }

    public static bool Keep(string recorder, DateTimeOffset at, IEnumerable<SourceSpan> spans, IReadOnlyList<string> preference)
    {
        var rank = Rank(recorder, preference);
        foreach (var s in spans)
            if (Rank(s.Recorder, preference) < rank && s.FromUtc <= at && at < s.ToUtc) return false;
        return true;
    }
}
