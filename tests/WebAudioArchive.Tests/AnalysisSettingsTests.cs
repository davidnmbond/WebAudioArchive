using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class AnalysisSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-aset-" + Guid.NewGuid().ToString("N"));

    public AnalysisSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static Func<string, string?> Env(params (string Name, string Value)[] pairs) => EnvWith(false, pairs);

    /// <summary>The default environment has a whisper URL (it is required); withoutWhisperUrl leaves it out.</summary>
    static Func<string, string?> EnvWith(bool withoutWhisperUrl, params (string Name, string Value)[] pairs)
    {
        var d = pairs.ToDictionary(p => p.Name, p => p.Value);
        return n => d.GetValueOrDefault(n) ?? (n == "WAA_WHISPER_URL" && !withoutWhisperUrl ? "http://127.0.0.1:8080" : null);
    }

    string Recorders => $"a={_root}/a,b={_root}/b";

    [Fact]
    public void Defaults_are_the_compose_values()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders)));

        Assert.Equal("burst", s.Station);
        Assert.Equal(new[] { "a", "b" }, s.Preference);
        Assert.Equal("/analysis", s.OutputDir);
        Assert.Equal("/scratch", s.ScratchDir);
        Assert.Equal(2048L * 1024 * 1024, s.ScratchMaxBytes);
        Assert.Equal(20L * 1024 * 1024 * 1024, s.MinFreeBytes);
        Assert.Equal("ggml-large-v3-turbo-q8_0", s.WhisperServer.Model);
        Assert.Equal(new Uri("http://127.0.0.1:8080"), s.WhisperServer.BaseUrl);
        Assert.Equal(new Uri("http://host.docker.internal:11434"), s.OllamaUrl);
        Assert.Equal("qwen3.6:latest", s.OllamaModel);
        Assert.True(s.EnrichmentEnabled);
        Assert.Equal("/config/analysis/analysis.json", s.ConfigPath);
        Assert.Equal(TimeSpan.FromHours(6), s.LagAlarm);
        Assert.Equal(8080, s.Port);
        Assert.Null(s.Since);
    }

    [Fact]
    public void Preference_puts_listed_recorders_first_and_appends_the_rest()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_PREFERENCE", "b")));
        Assert.Equal(new[] { "b", "a" }, s.Preference);
    }

    [Fact]
    public void Since_must_be_utc()
    {
        var ok = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_SINCE", "2026-10-15T00:00:00Z")));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), ok.Since);

        Assert.Contains("WAA_ANALYSIS_SINCE", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_SINCE", "2026-10-15T01:00:00+01:00")))).Message);
    }

    [Theory]
    [InlineData("WAA_OLLAMA_MODEL", "kimi-k2.6:cloud", "cloud model")]
    [InlineData("WAA_OLLAMA_URL", "https://ollama.example.com", "leave")]
    [InlineData("WAA_SCRATCH_MAX_MB", "10", "WAA_SCRATCH_MAX_MB")]
    [InlineData("WAA_ENRICHMENT", "maybe", "on or off")]
    [InlineData("WAA_ANALYSIS_PREFERENCE", "c", "recorder 'c'")]
    [InlineData("WAA_STATION", "../etc", "WAA_STATION")]
    public void Bad_values_are_refused_by_name(string name, string value, string expected)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), (name, value))));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.20.0.3:11434")]
    [InlineData("http://[::1]:11434")]
    public void Local_and_private_ollama_hosts_are_accepted(string url)
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url)));
        Assert.Equal(new Uri(url), s.OllamaUrl);
    }


    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://host.docker.internal:11434")]
    [InlineData("http://169.254.10.1:11434")]
    [InlineData("http://[fe80::1]:11434")]
    [InlineData("http://[fd12:3456::1]:11434")]
    public void More_local_and_private_forms_are_accepted(string url)
    {
        Assert.Equal(new Uri(url), AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url))).OllamaUrl);
    }

    [Theory]
    [InlineData("http://ai:11434")]
    [InlineData("http://ollama:11434")]
    [InlineData("http://localhost.:11434")]
    [InlineData("http://127.0.0.1.:11434")]
    [InlineData("http://user@localhost:11434")]
    [InlineData("http://localhost:x@evil.example.com:11434")]
    [InlineData("http://[::ffff:8.8.8.8]:11434")]
    [InlineData("http://[::ffff:127.0.0.1]:11434")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://172.32.0.1:11434")]
    [InlineData("http://[2001:db8::1]:11434")]
    public void Unlisted_dotless_and_tricky_hosts_are_refused(string url)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url))));
        Assert.Contains("WAA_OLLAMA_URL", ex.Message);
    }

    [Fact]
    public void A_host_in_the_allow_list_is_accepted_and_others_still_are_not()
    {
        var env = (string url) => Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url), ("WAA_OLLAMA_ALLOWED_HOSTS", "ollama, Gpu-Box"));
        Assert.Equal("ollama", AnalysisSettings.Load(env("http://ollama:11434")).OllamaUrl.Host);
        Assert.Equal("gpu-box", AnalysisSettings.Load(env("http://gpu-box:11434")).OllamaUrl.Host);
        Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(env("http://ai:11434")));
    }
    [Theory]
    [InlineData("http://pdl-rune-02.panoramicdata.com:11434")]
    [InlineData("http://pdl-rune-01.panoramicdata.com:11434")]
    public void The_owners_ollama_servers_are_accepted_with_the_compose_allow_list(string url)
    {
        var compose = RepoFiles.Read("docker/docker-compose.analysis.yml");
        var m = System.Text.RegularExpressions.Regex.Match(compose, @"WAA_OLLAMA_ALLOWED_HOSTS: \$\{WAA_OLLAMA_ALLOWED_HOSTS:-([^}]+)\}");
        Assert.True(m.Success, "docker-compose.analysis.yml must set a default WAA_OLLAMA_ALLOWED_HOSTS");
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url), ("WAA_OLLAMA_ALLOWED_HOSTS", m.Groups[1].Value)));
        Assert.Equal(new Uri(url), s.OllamaUrl);
        Assert.Contains("pdl-rune-02.panoramicdata.com:11434", compose);
    }

    [Fact]
    public void A_remote_ollama_needs_an_explicit_opt_in()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", "https://ollama.example.com"), ("WAA_OLLAMA_ALLOW_REMOTE", "true")));
        Assert.Equal("ollama.example.com", s.OllamaUrl.Host);
    }

    [Fact]
    public void A_missing_whisper_url_is_refused_and_says_how_to_set_it()
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(EnvWith(true, ("WAA_RECORDERS", Recorders))));

        Assert.Contains("WAA_WHISPER_URL is not set", ex.Message);
        Assert.Contains("docs/analysis.md", ex.Message);
    }

    [Theory]
    [InlineData("WAA_WHISPER_MODEL_PATH", "/models/ggml-base.en.bin")]
    [InlineData("WAA_WHISPER_DEVICE", "cuda")]
    [InlineData("WAA_WHISPER_THREADS", "4")]
    [InlineData("WAA_WHISPER_DTW", "on")]
    public void The_old_in_process_settings_are_refused_with_a_message_naming_the_url_setting(string name, string value)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), (name, value))));

        Assert.Contains($"{name} is no longer used", ex.Message);
        Assert.Contains("set WAA_WHISPER_URL (docs/analysis.md)", ex.Message);
    }

    [Fact]
    public void Missing_recorders_or_roots_name_the_setting()
    {
        Assert.Contains("WAA_RECORDERS is not set", Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env())).Message);
        Assert.Contains("does not exist", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/missing")))).Message);
        Assert.Contains("listed twice", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a,a={_root}/b")))).Message);
    }
}
