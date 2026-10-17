namespace WebAudioArchive.Worker.Consolidation;

/// <summary>One recorder's data root, as its id (the id analysis uses: a or b) and the folder its station folders are in.</summary>
public sealed record ConsolidatorSource(string Id, string Root);

/// <summary>
/// The consolidator's settings (issue #104), read from WAA_STATION, WAA_SOURCES (a=/data/a,b=/data/b), WAA_MERGED,
/// WAA_ANALYSIS (empty means analysis is not waited for), WAA_CONSOLIDATE_MODE (merge or hold) and WAA_PORT.
/// </summary>
public sealed record ConsolidatorSettings(
    string Station, ConsolidatorSource A, ConsolidatorSource B, string MergedRoot, string? AnalysisRoot, string Mode, string Port)
{
    public const string MergeMode = "merge";
    public const string HoldMode = "hold";

    public IReadOnlyList<ConsolidatorSource> Sources => [A, B];

    public bool Holds => Mode == HoldMode;

    /// <summary>&lt;merged&gt;/&lt;station&gt;: where failures.json and deletions.jsonl live.</summary>
    public string MergedStationDir => Path.Combine(MergedRoot, Station);

    /// <summary>&lt;merged&gt;/&lt;station&gt;/master: the merged hours.</summary>
    public string MergedMasterDir => Path.Combine(MergedStationDir, "master");

    /// <summary>The settings, or the reasons they cannot be used. <paramref name="get"/> returns null for a setting that is not set.</summary>
    public static (ConsolidatorSettings? Settings, IReadOnlyList<string> Errors) Read(Func<string, string?> get)
    {
        var errors = new List<string>();
        var station = get("WAA_STATION")?.Trim();
        if (string.IsNullOrEmpty(station)) errors.Add("WAA_STATION is required");
        else if (station.IndexOfAny(['/', '\\']) >= 0 || station is "." or "..") errors.Add($"WAA_STATION '{station}' is not a station id");

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (get("WAA_SOURCES") ?? "a=/data/a,b=/data/b").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var id = eq > 0 ? part[..eq].Trim() : "";
            var root = eq > 0 ? part[(eq + 1)..].Trim() : "";
            if (id.Length == 0 || root.Length == 0) errors.Add($"WAA_SOURCES entry '{part}' is not id=path");
            else if (!sources.TryAdd(id, root)) errors.Add($"WAA_SOURCES names recorder '{id}' twice");
        }

        // The merge counts frames from recorder a, and analysis names its jobs by these ids, so they must be a and b.
        if (sources.Count != 2 || !sources.ContainsKey("a") || !sources.ContainsKey("b"))
            errors.Add("WAA_SOURCES must name exactly the recorders a and b, as analysis does (a=/data/a,b=/data/b)");

        var merged = get("WAA_MERGED") ?? "/data/merged";
        if (string.IsNullOrWhiteSpace(merged)) errors.Add("WAA_MERGED must not be empty");

        var analysis = get("WAA_ANALYSIS") ?? "/data/analysis";
        var mode = (get("WAA_CONSOLIDATE_MODE") ?? MergeMode).Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = MergeMode;
        if (mode is not (MergeMode or HoldMode)) errors.Add($"WAA_CONSOLIDATE_MODE '{mode}' must be merge or hold");

        var port = get("WAA_PORT") ?? "8080";
        if (errors.Count > 0) return (null, errors);

        return (new ConsolidatorSettings(
            station!,
            new ConsolidatorSource("a", sources["a"]),
            new ConsolidatorSource("b", sources["b"]),
            merged,
            string.IsNullOrWhiteSpace(analysis) ? null : analysis,
            mode,
            port), errors);
    }
}
