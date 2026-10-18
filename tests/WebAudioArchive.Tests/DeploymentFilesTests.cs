using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The deployment rules that keep the recorders safe, checked on the files so a later edit cannot quietly break them.</summary>
public class DeploymentFilesTests
{
    const string AnalysisFile = "docker/docker-compose.analysis.yml";
    static readonly Dictionary<string, string> Compose = RepoFiles.ComposeServices("docker/docker-compose.yml");
    static readonly Dictionary<string, string> Analysis = RepoFiles.ComposeServices(AnalysisFile);

    /// <summary>The file without its comment lines: comments may say "no CUDA"; only real configuration is checked.</summary>
    static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void Analysis_reads_the_recordings_read_only_and_writes_only_its_own_folder()
    {
        var a = Analysis["analysis"];

        Assert.Contains("/a:/data/a:ro", a);
        Assert.Contains("/b:/data/b:ro", a);
        Assert.Contains("/analysis:/analysis", a);
        Assert.Contains("web-cache:/web-cache:ro", a);
        Assert.Contains("../config:/config:ro", a);
        Assert.DoesNotContain("/models", a);
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
        Assert.Contains("WAA_OLLAMA_URL: ${WAA_OLLAMA_URL:-http://pdl-rune-02.panoramicdata.com:11434}", a);
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
    public void Gpu_override_is_gone()
    {
        Assert.False(File.Exists(Path.Combine(RepoFiles.Root, "docker", "docker-compose.gpu.yml")), "decision D19: the analysis container is CPU only; the GPU is on the whisper server");
        Assert.DoesNotContain("docker-compose.gpu.yml", RepoFiles.Read(".github/workflows/ci.yml"));
    }

    [Fact]
    public void Analysis_compose_has_no_gpu_and_no_models_mount()
    {
        var text = Code(RepoFiles.Read(AnalysisFile));
        var a = Code(Analysis["analysis"]);

        Assert.DoesNotContain("nvidia", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpu", a, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuda", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/models", text);
        Assert.DoesNotContain("target:", a);
        Assert.DoesNotContain("deploy:", a);
        foreach (var gone in new[] { "WAA_WHISPER_MODEL_PATH", "WAA_WHISPER_DEVICE", "WAA_WHISPER_THREADS", "WAA_WHISPER_DTW" })
            Assert.DoesNotContain(gone, text);
    }

    [Fact]
    public void Analysis_compose_names_the_whisper_server_and_the_hosts_the_audio_may_go_to()
    {
        var a = Analysis["analysis"];

        Assert.Contains("WAA_WHISPER_URL: ${WAA_WHISPER_URL:-http://pdl-rune-02.panoramicdata.com:8080}", a);
        Assert.Contains("WAA_WHISPER_ALLOWED_HOSTS: ${WAA_WHISPER_ALLOWED_HOSTS:-pdl-rune-02.panoramicdata.com,pdl-rune-01.panoramicdata.com}", a);
        Assert.Contains("WAA_WHISPER_MODEL: ${WAA_WHISPER_MODEL:-ggml-large-v3-turbo-q8_0}", a);
        Assert.Contains("cap_drop: [ALL]", a);
        Assert.Contains("no-new-privileges:true", a);
        Assert.Contains("user:", a);
    }

    [Fact]
    public void Whisper_dev_compose_is_loopback_only_and_names_no_recorder()
    {
        const string file = "docker/docker-compose.whisper-dev.yml";
        var text = RepoFiles.Read(file);
        var services = RepoFiles.ComposeServices(file);

        Assert.Matches("(?m)^name: waa-whisper-dev$", text);
        Assert.Equal(new[] { "whisper" }, services.Keys.Order(StringComparer.Ordinal));
        var whisper = Code(services["whisper"]);
        Assert.Contains("image: ghcr.io/ggml-org/whisper.cpp:main-cuda-d1be6fde11ac6e0407606b4e42fe72d34add8037", whisper);
        Assert.Contains("\"127.0.0.1:8178:8080\"", whisper);
        foreach (var port in whisper.Split('\n').Where(l => l.Contains(":8080\"", StringComparison.Ordinal)))
            Assert.Contains("127.0.0.1:", port);
        Assert.Contains("capabilities: [gpu]", whisper);
        Assert.Contains(":/models:ro", whisper);
        Assert.Contains("--flash-attn", whisper);
        Assert.DoesNotContain("--convert", whisper);
        Assert.DoesNotContain("--print-realtime", whisper);
        Assert.Contains("DEVELOPMENT ONLY", text);
        Assert.DoesNotContain("recorder", whisper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/data", whisper);
        Assert.DoesNotContain("container_name: waa-recorder", text);
        Assert.DoesNotContain("container_name: waa-web", text);
    }

    [Fact]
    public void The_whisper_url_default_and_allow_list_in_compose_pass_the_settings_rule()
    {
        var compose = RepoFiles.Read(AnalysisFile);
        var url = System.Text.RegularExpressions.Regex.Match(compose, @"WAA_WHISPER_URL: \$\{WAA_WHISPER_URL:-([^}]+)\}");
        var hosts = System.Text.RegularExpressions.Regex.Match(compose, @"WAA_WHISPER_ALLOWED_HOSTS: \$\{WAA_WHISPER_ALLOWED_HOSTS:-([^}]+)\}");
        Assert.True(url.Success && hosts.Success);
        var root = Directory.CreateTempSubdirectory("waa-dep-").FullName;
        try
        {
            var env = new Dictionary<string, string>
            {
                ["WAA_RECORDERS"] = "a=" + root,
                ["WAA_WHISPER_URL"] = url.Groups[1].Value,
                ["WAA_WHISPER_ALLOWED_HOSTS"] = hosts.Groups[1].Value,
            };

            var w = WebAudioArchive.Analysis.Settings.AnalysisSettings.Load(n => env.GetValueOrDefault(n)).WhisperServer!;

            Assert.Equal("pdl-rune-02.panoramicdata.com", w.BaseUrl.Host);
            Assert.Equal(8080, w.BaseUrl.Port);
            env["WAA_WHISPER_URL"] = "http://pdl-rune-01.panoramicdata.com:8080";
            Assert.Equal("pdl-rune-01.panoramicdata.com", WebAudioArchive.Analysis.Settings.AnalysisSettings.Load(n => env.GetValueOrDefault(n)).WhisperServer!.BaseUrl.Host);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ci_builds_the_single_cpu_analysis_image_and_checks_the_compose_files()
    {
        var ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Contains("docker build -f docker/Dockerfile.analysis -t webaudioarchive-analysis:ci .", ci);
        Assert.DoesNotContain("--target", ci);
        Assert.DoesNotContain("cuda", ci, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ldd", ci);
        Assert.DoesNotContain("whisper.so", ci);
        Assert.Contains("docker compose -f docker/docker-compose.analysis.yml config --quiet", ci);
        Assert.Contains("docker compose -f docker/docker-compose.whisper-dev.yml config --quiet", ci);
        Assert.Contains("docker compose -f docker/docker-compose.yml -f docker/docker-compose.web-analysis.yml config --quiet", ci);
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
    public void Analysis_image_has_no_cuda_and_no_native_whisper()
    {
        var analysis = Code(RepoFiles.Read("docker/Dockerfile.analysis"));
        Assert.Contains("USER $APP_UID", analysis);
        Assert.Contains("--healthcheck", analysis);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(analysis, "(?m)^FROM ").Count); // build and runtime: one runtime stage, no cuda stage
        Assert.Contains("AS runtime", analysis);
        Assert.DoesNotContain("AS cpu", analysis);
        Assert.DoesNotContain("AS cuda", analysis);
        Assert.DoesNotContain("cuda", analysis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("libcublas", analysis);
        Assert.DoesNotContain("libgomp", analysis);
        Assert.DoesNotContain("nvidia", analysis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtimes", analysis);
        Assert.DoesNotContain("whisper", analysis, StringComparison.OrdinalIgnoreCase);
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
