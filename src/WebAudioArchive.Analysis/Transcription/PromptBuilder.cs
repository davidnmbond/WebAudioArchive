using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// The initial prompt for each chunk (spec 3.6): a verbatim sentence full of fillers, so whisper keeps um and er, then
/// the station glossary and the show title, so names are spelled right. Whisper reads at most about 224 prompt tokens,
/// so the prompt is cut at 600 characters.
/// </summary>
public static class PromptBuilder
{
    public const int MaxChars = 600;

    public static string Build(TranscriptionOptions o, string? showTitle)
    {
        var names = new List<string>();
        foreach (var n in o.Glossary.Append(showTitle ?? ""))
        {
            var name = n.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        var text = names.Count == 0 ? o.VerbatimPrompt.Trim() : $"{o.VerbatimPrompt.Trim()} {string.Join(", ", names)}.";
        return text.Length <= MaxChars ? text : text[..MaxChars];
    }
}
