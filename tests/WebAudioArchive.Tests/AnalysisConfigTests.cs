using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Tests;

public sealed class AnalysisConfigTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-acfg-" + Guid.NewGuid().ToString("N"));

    public AnalysisConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string CommittedConfig => Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json");

    string WriteConfig(string json, bool withSwears = true)
    {
        if (withSwears) File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "swearwords.json"), Path.Combine(_dir, "swearwords.json"), overwrite: true);
        var path = Path.Combine(_dir, "analysis.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_committed_config_loads()
    {
        var loaded = AnalysisConfigLoader.Load(CommittedConfig);

        Assert.Equal(0.99, loaded.Config.Clipping.Threshold);
        Assert.Equal(-50, loaded.Config.DeadAir.ThresholdDbfs);
        Assert.Equal("21:00", loaded.Config.Swearing.WatershedStart);
        Assert.DoesNotContain("like", loaded.Config.Fillers.Words);
        Assert.Equal(300, loaded.Config.Transcription.ChunkSeconds);
        Assert.StartsWith("2026-10-16.1|", loaded.DetectorVersion);
        Assert.EndsWith("|code" + AnalysisConfigLoader.DetectorCodeVersion, loaded.DetectorVersion);
    }

    [Fact]
    public void The_committed_swear_list_has_three_tiers_suffixes_and_an_allow_list()
    {
        var list = AnalysisConfigLoader.Load(CommittedConfig).Swears;

        Assert.Equal(SwearTiers.Severe, list.Forms["fucking"]);
        Assert.Equal(SwearTiers.Severe, list.Forms["motherfuckers"]);
        Assert.Equal(SwearTiers.Strong, list.Forms["bitches"]);
        Assert.Equal(SwearTiers.Mild, list.Forms["pissed"]);
        Assert.False(list.Forms.ContainsKey("pricked"));
        Assert.False(list.Forms.ContainsKey("knobs"));
        foreach (var tier in SwearTiers.All) Assert.Contains(tier, list.Forms.Values);
    }

    [Fact]
    public void Unknown_field_is_rejected_by_name()
    {
        var path = WriteConfig("""{ "version": "x", "clipping": { "threshhold": 0.99 } }""");
        var ex = Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path));
        Assert.Contains("threshhold", ex.Message);
    }

    [Fact]
    public void Missing_version_is_rejected()
    {
        var path = WriteConfig("""{ }""");
        Assert.Contains("version", Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Theory]
    [InlineData("""{ "version": "x", "clipping": { "threshold": 0.97 } }""", "clipping.threshold")]
    [InlineData("""{ "version": "x", "clipping": { "minRunSamples": 1 } }""", "clipping.minRunSamples")]
    [InlineData("""{ "version": "x", "deadAir": { "thresholdDbfs": 3 } }""", "deadAir.thresholdDbfs")]
    [InlineData("""{ "version": "x", "deadAir": { "minSeconds": 10, "severeSeconds": 5 } }""", "deadAir.severeSeconds")]
    [InlineData("""{ "version": "x", "swearing": { "watershedStart": "9pm" } }""", "swearing.watershedStart")]
    [InlineData("""{ "version": "x", "fillers": { "words": ["um", "like"] } }""", "too ambiguous")]
    [InlineData("""{ "version": "x", "transcription": { "chunkSeconds": 5 } }""", "transcription.chunkSeconds")]
    [InlineData("""{ "version": "x", "transcription": { "maxRepeats": 1 } }""", "transcription.maxRepeats")]
    [InlineData("""{ "version": "x", "enrichment": { "numCtx": 100 } }""", "enrichment.numCtx")]
    public void Bad_values_are_refused_by_field(string json, string expected)
    {
        var path = WriteConfig(json);
        Assert.Contains(expected, Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void Missing_swear_list_is_named()
    {
        var path = WriteConfig("""{ "version": "x" }""", withSwears: false);
        Assert.Contains("swearwords.json", Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Theory]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "rude": ["x"] }, "allow": [] }""", "unknown tier 'rude'")]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "mild": ["damn"], "strong": ["damn"] }, "allow": [] }""", "'damn' is in more than one tier")]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "mild": ["Damn!"] }, "allow": [] }""", "lower-case letters")]
    [InlineData("""{ "suffixes": [], "tiers": { "mild": ["damn"] }, "allow": [] }""", "version")]
    public void Bad_swear_lists_are_refused(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<AnalysisConfigException>(() => SwearList.Parse(json, "test.json")).Message);
    }

    [Fact]
    public void Changing_the_config_changes_the_detector_version()
    {
        var a = AnalysisConfigLoader.Load(WriteConfig("""{ "version": "x" }""")).DetectorVersion;
        var b = AnalysisConfigLoader.Load(WriteConfig("""{ "version": "x", "deadAir": { "minSeconds": 6 } }""")).DetectorVersion;
        Assert.NotEqual(a, b);
    }
}
