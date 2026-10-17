using System.Text;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// One whisper word to its matching form: lower case, only a to z, digits and '*' kept, so "Fuckin'" is "fuckin",
/// "bull-shit" is "bullshit" and "F***!" is "f***". Letters with accents are dropped (the lists are English).
/// </summary>
public static class WordNormaliser
{
    public static string Normalise(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormKC))
        {
            var c = char.ToLowerInvariant(ch);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '*') sb.Append(c);
        }

        return sb.ToString();
    }
}
