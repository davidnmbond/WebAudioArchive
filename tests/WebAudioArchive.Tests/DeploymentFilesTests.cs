using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The deployment rules that keep the recorders safe, checked on the files so a later edit cannot quietly break them.</summary>
public class DeploymentFilesTests
{
    const string AnalysisFile = "docker/docker-compose.analysis.yml";
    static readonly Dictionary<string, string> Compose = RepoFiles.ComposeServices("docker/docker-compose.yml");
    static readonly Dictionary<string, string> Analysis = RepoFiles.ComposeServices(AnalysisFile);

    [Fact]
    public void Analysis_reads_the_recordings_read_only_and_writes_only_its_own_folder()
    {
        var a = Analysis["analysis"];

        Assert.Contains("/a:/data/a:ro", a);
        Assert.Contains("/b:/data/b:ro", a);
        Assert.Contains("/analysis:/analysis", a);
        Assert.Contains("web-cache:/web-cache:ro", a);
        Assert.Contains("../config:/config:ro", a);
        Assert.Contains(":/models:ro", a);
        foreach (var mount in a.Split('\n').Where(l => l.Contains(":/data", StringComparison.Ordinal)))
            Assert.EndsWith(":ro", mount.Trim());
        Assert.Contains("/scratch:size=2g", a);
        Assert.Contains("host.docker.internal:host-gateway", a);
    }

    [Fact]
    public void Analysis_runs_below_the_recorders_and_whisper_has_no_container_of_its_own()
    {
        var a = Analysis["analysis"];
        Assert.Contains("cpu_shares: 256", a);
        Assert.Contains("oom_score_adj: 800", a);
        Assert.Contains("mem_limit:", a);
        Assert.Contains("cpus:", a);
        Assert.DoesNotContain("depends_on", a);
        Assert.False(Analysis.ContainsKey("whisper"), "whisper runs inside the analysis process since decision D18");
        Assert.False(Compose.ContainsKey("whisper"));

        Assert.Contains("\"nice\", \"-n\", \"10\"", RepoFiles.Read("docker/Dockerfile.analysis"));
    }

    [Fact]
    public void Analysis_gets_time_to_stop_gracefully_is_non_root_and_published_on_loopback_only()
    {
        var a = Analysis["analysis"];

        var grace = a.Split('\n').Single(l => l.TrimStart().StartsWith("stop_grace_period:", StringComparison.Ordinal));
        var seconds = int.Parse(grace.Split(':')[1].Trim().TrimEnd('s'));
        Assert.True(seconds >= 60, "a graceful stop must be able to finish or abort a chunk and restore attempts before Docker kills the container");
        Assert.Contains("restart: unless-stopped", a);
        Assert.Contains("user:", a);
        Assert.Contains("\"127.0.0.1:8095:8080\"", a);
        Assert.Contains("WAA_OLLAMA_URL: ${WAA_OLLAMA_URL:-http://host.docker.internal:11434}", a);
    }

    [Fact]
    public void Analysis_is_its_own_compose_project_and_shares_only_the_web_cache()
    {
        var text = RepoFiles.Read(AnalysisFile);

        Assert.Matches("(?m)^name: waa-analysis$", text);
        Assert.Contains("external: true", text);
        Assert.Equal(new[] { "analysis" }, Analysis.Keys.Order(StringComparer.Ordinal));
        Assert.False(Compose.ContainsKey("analysis"), "analysis lives in docker-compose.analysis.yml, not in the recorder project");
    }

    [Fact]
    public void Recorders_are_untouched_by_analysis()
    {
        foreach (var name in new[] { "recorder-a", "recorder-b" })
        {
            Assert.DoesNotContain("analysis", Compose[name]);
            Assert.DoesNotContain("gpu", Compose[name]);
            Assert.DoesNotContain("depends_on", Compose[name]);
        }
    }

    [Fact]
    public void The_gpu_override_changes_only_analysis_and_falls_back_to_the_cpu()
    {
        var gpu = RepoFiles.ComposeServices("docker/docker-compose.gpu.yml");

        Assert.Equal(new[] { "analysis" }, gpu.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("capabilities: [gpu]", gpu["analysis"]);
        Assert.Contains("target: cuda", gpu["analysis"]);
        Assert.Contains("WAA_WHISPER_DEVICE: auto", gpu["analysis"]);
        Assert.DoesNotContain("WAA_WHISPER_DEVICE: cuda", gpu["analysis"]);
        Assert.Contains("ggml-large-v3-turbo-q8_0.bin", gpu["analysis"]);
    }

    [Fact]
    public void The_web_override_adds_only_the_analysis_setting_and_a_read_only_mount()
    {
        var web = RepoFiles.ComposeServices("docker/docker-compose.web-analysis.yml");

        Assert.Equal(new[] { "web" }, web.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("WAA_ANALYSIS: /data/analysis", web["web"]);
        Assert.Contains("/analysis:/data/analysis:ro", web["web"]);
        Assert.DoesNotContain("ports:", web["web"]);
        Assert.DoesNotContain("image:", web["web"]);
    }

    [Fact]
    public void The_image_is_non_root_with_cpu_and_cuda_targets_and_no_native_build()
    {
        var analysis = RepoFiles.Read("docker/Dockerfile.analysis");
        Assert.Contains("USER $APP_UID", analysis);
        Assert.Contains("--healthcheck", analysis);
        Assert.Contains("AS cpu", analysis);
        Assert.Contains("FROM cpu AS cuda", analysis);
        Assert.Contains("libgomp1", analysis);
        Assert.Contains("cuda-cudart-13-0", analysis);
        Assert.Contains("libcublas-13-0", analysis);
        Assert.DoesNotContain("ffmpeg", analysis);
        Assert.DoesNotContain("cmake", analysis);
        Assert.False(File.Exists(Path.Combine(RepoFiles.Root, "docker", "Dockerfile.whisper")));
    }

    [Fact]
    public void Models_and_data_are_never_committed()
    {
        var ignore = RepoFiles.Read(".gitignore");
        Assert.Contains("/models/", ignore);
        Assert.Contains("/data/", ignore);
    }
}
