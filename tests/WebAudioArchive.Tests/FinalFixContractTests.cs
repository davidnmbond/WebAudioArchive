using System.Net;
using System.Text.Json;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

// Final fix wave: settings, the Ollama keep_alive, the atomic JSON replace, and the deployment files.
public sealed class FinalFixSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-ffs-" + Guid.NewGuid().ToString("N"));

    public FinalFixSettingsTests() => Directory.CreateDirectory(Path.Combine(_root, "a"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    AnalysisSettings Load(params (string Name, string Value)[] extra)
    {
        var d = extra.ToDictionary(p => p.Name, p => p.Value);
        d["WAA_RECORDERS"] = $"a={_root}/a";
        return AnalysisSettings.Load(n => d.GetValueOrDefault(n));
    }

    [Fact]
    public void Retranscribe_is_off_by_default_and_only_true_or_false_are_accepted()
    {
        Assert.False(Load().Retranscribe);
        Assert.True(Load(("WAA_RETRANSCRIBE", "true")).Retranscribe);
        Assert.True(Load(("WAA_RETRANSCRIBE", "TRUE")).Retranscribe);
        Assert.False(Load(("WAA_RETRANSCRIBE", "false")).Retranscribe);
        var ex = Assert.Throws<AnalysisSettingsException>(() => Load(("WAA_RETRANSCRIBE", "yes")));
        Assert.Contains("WAA_RETRANSCRIBE", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30s")]
    [InlineData("5m")]
    [InlineData("1h30m")]
    [InlineData("-1")]
    public void The_ollama_keep_alive_accepts_zero_a_duration_or_minus_one(string value) =>
        Assert.Equal(value, Load(("WAA_OLLAMA_KEEP_ALIVE", value)).OllamaKeepAlive);

    [Fact]
    public void The_ollama_keep_alive_defaults_to_zero_so_the_gpu_is_freed_straight_after_an_enrichment()
    {
        Assert.Equal("0", Load().OllamaKeepAlive);
        foreach (var bad in new[] { "banana", "10 minutes", "5x", "-5" })
            Assert.Contains("WAA_OLLAMA_KEEP_ALIVE", Assert.Throws<AnalysisSettingsException>(() => Load(("WAA_OLLAMA_KEEP_ALIVE", bad))).Message);
    }
}

public sealed class FinalFixContractTests
{
    static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config", "analysis");

    static async Task<string> ChatBodyAsync(OllamaClient client)
    {
        var schema = EnrichmentPrompt.Load(ConfigDir, new EnrichmentOptions()).Schema;
        await client.ChatAsync(new LlmRequest("m", "s", "u", schema, 0.1, 1, 2048, false), CancellationToken.None);
        return "";
    }

    [Fact]
    public async Task The_ollama_client_sends_keep_alive_zero_by_default_and_the_configured_value_otherwise()
    {
        var bodies = new List<string>();
        HttpResponseMessage Reply(HttpRequestMessage req)
        {
            bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "message": { "content": "{}" } }""") };
        }

        await ChatBodyAsync(new OllamaClient(new HttpClient(new StubHandler(Reply)), new Uri("http://127.0.0.1:11434")));
        await ChatBodyAsync(new OllamaClient(new HttpClient(new StubHandler(Reply)), new Uri("http://127.0.0.1:11434"), "5m"));
        await ChatBodyAsync(new OllamaClient(new HttpClient(new StubHandler(Reply)), new Uri("http://127.0.0.1:11434"), "-1"));

        Assert.Equal("0", JsonDocument.Parse(bodies[0]).RootElement.GetProperty("keep_alive").GetString());
        Assert.Equal("5m", JsonDocument.Parse(bodies[1]).RootElement.GetProperty("keep_alive").GetString());
        // A bare number is sent as a number (Ollama reads a string without a unit as an error).
        Assert.Equal(JsonValueKind.Number, JsonDocument.Parse(bodies[2]).RootElement.GetProperty("keep_alive").ValueKind);
        Assert.Equal(-1, JsonDocument.Parse(bodies[2]).RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public void A_replace_that_a_reader_blocks_is_tried_again_after_50_100_and_200_ms_then_given_up()
    {
        var sleeps = new List<TimeSpan>();
        var fails = 2;
        var moves = 0;
        AnalysisJson.MoveOver("a", "b", (_, _) =>
        {
            moves++;
            if (fails-- > 0) throw new IOException("in use");
        }, sleeps.Add);
        Assert.Equal(3, moves);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], sleeps);

        sleeps.Clear();
        moves = 0;
        Assert.Throws<IOException>(() => AnalysisJson.MoveOver("a", "b", (_, _) =>
        {
            moves++;
            throw new IOException("still in use");
        }, sleeps.Add));
        Assert.Equal(4, moves);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], sleeps);

        sleeps.Clear();
        Assert.Throws<InvalidOperationException>(() => AnalysisJson.MoveOver("a", "b", (_, _) => throw new InvalidOperationException("not an io error"), sleeps.Add));
        Assert.Empty(sleeps);
    }
}

public class FinalFixDeploymentTests
{
    [Fact]
    public void The_analysis_container_drops_every_capability_and_cannot_gain_any()
    {
        var a = RepoFiles.ComposeServices("docker/docker-compose.analysis.yml")["analysis"];

        Assert.Contains("cap_drop: [ALL]", a);
        Assert.Contains("security_opt: [\"no-new-privileges:true\"]", a);
    }

    [Fact]
    public void The_cuda_keyring_download_is_pinned_by_checksum_and_the_build_opts_out_of_telemetry()
    {
        var docker = RepoFiles.Read("docker/Dockerfile.analysis");

        Assert.Matches(@"(?m)^ADD --checksum=sha256:[0-9a-f]{64} https://developer\.download\.nvidia\.com/\S+cuda-keyring_1\.1-1_all\.deb /tmp/cuda-keyring\.deb$", docker);
        Assert.Contains("ENV DOTNET_CLI_TELEMETRY_OPTOUT=1", docker.Split("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS cpu")[0]);
    }

    [Fact]
    public void The_ci_library_checks_cannot_pass_vacuously_when_a_library_path_is_wrong()
    {
        var ci = RepoFiles.Read(".github/workflows/ci.yml");
        var checks = ci.Split('\n').Where(l => l.Contains("ldd ", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, checks.Count);
        foreach (var line in checks)
            Assert.Matches(@"test -f (/app/\S+\.so) && ! ldd \1 ", line);
    }

    [Fact]
    public void Line_endings_of_yaml_shell_and_dockerfiles_are_pinned_to_lf()
    {
        var attributes = RepoFiles.Read(".gitattributes");

        foreach (var rule in new[] { "*.yml text eol=lf", "*.yaml text eol=lf", "*.sh text eol=lf", "Dockerfile* text eol=lf" })
            Assert.Contains(rule, attributes);
    }

    [Fact]
    public void The_analysis_guide_exists_and_covers_the_operator_questions()
    {
        var guide = RepoFiles.Read("docs/analysis.md");

        foreach (var needle in new[]
                 {
                     "catching-up", "lagging", "stalled", "failing", "paused", "backlogAudioHours", "catchUpEtaHours", "WAA_RETRANSCRIBE", "WAA_OLLAMA_KEEP_ALIVE",
                     "--unpark", "--rebuild", "--retranscribe", "stop the service first", "nvidia-smi", "docker-compose.web-analysis.yml", "404", "Limits",
                 })
            Assert.Contains(needle, guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("—", guide);
        Assert.Contains("docs/analysis.md", RepoFiles.Read("docker/docker-compose.analysis.yml"));
    }

    [Fact]
    public void The_operations_guide_names_each_health_status()
    {
        var ops = RepoFiles.Read("docs/operations.md");

        foreach (var status in new[] { "catching-up", "lagging", "stalled", "failing", "paused" })
            Assert.Contains(status, ops);
    }
}
