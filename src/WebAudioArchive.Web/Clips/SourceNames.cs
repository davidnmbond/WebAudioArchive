namespace WebAudioArchive.Web.Clips;

/// <summary>
/// How a clip source is named to a person. The merged copy is built from both recorders, so it is never called a
/// recorder; recorders keep their upper-case id (A, B).
/// </summary>
public static class SourceNames
{
    public static bool IsMerged(string id) => id == WebSettings.MergedId;

    /// <summary>A table or lane label: "Merged" or "A".</summary>
    public static string Label(string id) => IsMerged(id) ? "Merged" : id.ToUpperInvariant();

    /// <summary>Inside a sentence after a preposition: "the merged copy" or "A".</summary>
    public static string Short(string id) => IsMerged(id) ? "the merged copy" : id.ToUpperInvariant();

    /// <summary>A subject or object phrase: "the merged copy" or "recorder A".</summary>
    public static string Who(string id) => IsMerged(id) ? "the merged copy" : $"recorder {id.ToUpperInvariant()}";

    /// <summary>As <see cref="Who"/>, starting a sentence: "The merged copy" or "Recorder A".</summary>
    public static string WhoCap(string id) => IsMerged(id) ? "The merged copy" : $"Recorder {id.ToUpperInvariant()}";

    /// <summary>"recorders A and B", "the merged copy and recorder A", "the merged copy and recorders A and B".</summary>
    public static string Many(IEnumerable<string> ids)
    {
        var list = ids.Order(StringComparer.Ordinal).ToList();
        var recorders = list.Where(i => !IsMerged(i)).Select(i => i.ToUpperInvariant()).ToList();
        var parts = new List<string>();
        if (list.Any(IsMerged)) parts.Add("the merged copy");
        if (recorders.Count == 1) parts.Add($"recorder {recorders[0]}");
        else if (recorders.Count > 1) parts.Add($"recorders {string.Join(" and ", recorders)}");
        return string.Join(" and ", parts);
    }
}
