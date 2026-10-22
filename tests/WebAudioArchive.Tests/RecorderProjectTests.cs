using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The recorders are their own compose project, so building or restarting anything else can never recreate them (issue #102).</summary>
public class RecorderProjectTests
{
    const string RecordersFile = "docker/docker-compose.recorders.yml";
    static readonly Dictionary<string, string> Recorders = RepoFiles.ComposeServices(RecordersFile);
    static readonly Dictionary<string, string> Main = RepoFiles.ComposeServices("docker/docker-compose.yml");

    [Fact]
    public void Recorders_live_only_in_their_own_project()
    {
        Assert.Equal(new[] { "recorder-a", "recorder-b" }, Recorders.Keys.Order(StringComparer.Ordinal));
        Assert.Matches("(?m)^name: waa-recorders\r?$", RepoFiles.Read(RecordersFile));
        Assert.False(Main.ContainsKey("recorder-a"));
        Assert.False(Main.ContainsKey("recorder-b"));
        foreach (var name in Recorders.Keys)
        {
            Assert.Contains("waa.role: recorder", Recorders[name]);
            Assert.Contains("restart: always", RepoFiles.Read(RecordersFile));
        }
    }

    [Fact]
    public void The_consolidator_never_builds_the_recorder_image_and_cannot_restart_a_recorder()
    {
        var c = Main["consolidator"];

        Assert.DoesNotContain("build:", c);
        Assert.DoesNotContain("depends_on", c);
        Assert.Contains("image: webaudioarchive:local", c);
        Assert.Contains("[\"--consolidate\"]", c);
        Assert.Contains("WAA_CONSOLIDATE_MODE: merge", c);
        Assert.Contains("WAA_SOURCES: a=/data/a,b=/data/b", c);
        // Owner decision: analysis is not required until the analysis service runs (issue #111); the read-only mount
        // stays so switching it on is one line.
        Assert.Contains("WAA_ANALYSIS: \"\"", c);
        Assert.DoesNotContain("WAA_ANALYSIS: /data/analysis", c);
        Assert.Contains("/analysis:/data/analysis:ro", c);
        Assert.Contains("\"127.0.0.1:8096:8080\"", c);
        foreach (var (name, block) in Main)
            Assert.DoesNotContain("waa.role: recorder", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_docs_rebuild_the_consolidator_through_the_recorders_file_and_never_with_build()
    {
        var ops = RepoFiles.Read("docs/operations.md");

        // The consolidator has no build, so --build on it keeps the old image: the shared image is built from the
        // recorders file (which leaves the running recorders alone) and the consolidator is then recreated from it.
        Assert.Matches(
            @"docker compose -f docker/docker-compose\.recorders\.yml build recorder-a\r?\n\s*docker compose -f docker/docker-compose\.yml up -d consolidator\r?\n",
            ops);

        foreach (var (file, text) in new[] { ("docs/operations.md", ops), ("docker/docker-compose.yml", RepoFiles.Read("docker/docker-compose.yml")) })
            foreach (var line in text.Split('\n'))
                Assert.False(line.Contains("docker compose", StringComparison.Ordinal) && line.Contains("--build", StringComparison.Ordinal) && line.Contains("consolidator", StringComparison.Ordinal),
                    $"{file}: '{line.Trim()}' builds nothing for the consolidator, which has no build");

        Assert.Matches(@"data/merged[^\n]*\n?[^\n]*1654|1654[^\n]*\n?[^\n]*data/merged", ops);
    }

    [Fact]
    public void Web_and_consolidator_reach_the_recorders_over_the_shared_network()
    {
        Assert.Matches("(?m)^networks:\r?$", RepoFiles.Read(RecordersFile));
        Assert.Contains("name: waa", RepoFiles.Read(RecordersFile));
        foreach (var name in Recorders.Keys) Assert.Contains("- waa", Recorders[name]);

        var main = RepoFiles.Read("docker/docker-compose.yml");
        Assert.Matches("(?ms)^networks:\r?\n  waa:\r?\n    external: true", main);
        Assert.Contains("- waa", Main["web"]);
        Assert.Contains("- waa", Main["consolidator"]);
        Assert.Contains("WAA_MERGED: /data/merged", Main["web"]);
        Assert.Contains("WAA_CONSOLIDATOR: http://consolidator:8080", Main["web"]);
    }
}
