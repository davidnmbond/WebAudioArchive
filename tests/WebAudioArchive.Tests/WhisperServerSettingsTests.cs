using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Tests;

/// <summary>The WAA_WHISPER_* settings of the whisper.cpp server client (decision D19, Task 20).</summary>
public sealed class WhisperServerSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-wset-" + Guid.NewGuid().ToString("N"));

    public WhisperServerSettingsTests() => Directory.CreateDirectory(Path.Combine(_root, "a"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (name, value) in pairs) d[name] = value;
        d["WAA_RECORDERS"] = $"a={_root}/a";
        return n => d.GetValueOrDefault(n);
    }

    [Fact]
    public void The_defaults_follow_the_plan()
    {
        var w = AnalysisSettings.Load(Env(("WAA_WHISPER_URL", "http://127.0.0.1:8178"))).WhisperServer;

        Assert.NotNull(w);
        Assert.Equal(new Uri("http://127.0.0.1:8178"), w.BaseUrl);
        Assert.Equal("ggml-large-v3-turbo-q8_0", w.Model);
        Assert.Null(w.Language);
        Assert.Equal(TimeSpan.FromMinutes(10), w.Timeout);
        Assert.Equal(1, w.Concurrency);
        Assert.Equal(3, w.Retries);
        Assert.Null(w.Token);
    }

    [Fact]
    public void Every_setting_can_be_overridden()
    {
        var w = AnalysisSettings.Load(Env(
            ("WAA_WHISPER_URL", "http://10.1.2.3:8080/"),
            ("WAA_WHISPER_MODEL", "ggml-base.en"),
            ("WAA_WHISPER_LANGUAGE", "fr"),
            ("WAA_WHISPER_TIMEOUT_MINUTES", "30"),
            ("WAA_WHISPER_CONCURRENCY", "2"),
            ("WAA_WHISPER_RETRIES", "5"),
            ("WAA_WHISPER_TOKEN", "abc.DEF-123"))).WhisperServer!;

        Assert.Equal("ggml-base.en", w.Model);
        Assert.Equal("fr", w.Language);
        Assert.Equal(TimeSpan.FromMinutes(30), w.Timeout);
        Assert.Equal(2, w.Concurrency);
        Assert.Equal(5, w.Retries);
        Assert.Equal("abc.DEF-123", w.Token);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://10.210.3.1:8080")]
    [InlineData("http://172.20.0.3:8080")]
    [InlineData("http://host.docker.internal:8178")]
    public void Loopback_and_private_hosts_are_accepted(string url)
    {
        Assert.Equal(new Uri(url), AnalysisSettings.Load(Env(("WAA_WHISPER_URL", url))).WhisperServer!.BaseUrl);
    }

    [Theory]
    [InlineData("http://whisper:8080")]
    [InlineData("http://pdl-rune-02.panoramicdata.com:8080")]
    [InlineData("https://whisper.example.com")]
    [InlineData("http://8.8.8.8:8080")]
    [InlineData("http://localhost.:8080")]
    [InlineData("http://user:pw@127.0.0.1:8080")]
    [InlineData("http://[::ffff:127.0.0.1]:8080")]
    [InlineData("http://172.32.0.1:8080")]
    public void Public_unlisted_and_tricky_hosts_are_refused_by_name_and_say_the_audio_would_leave(string url)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_WHISPER_URL", url))));

        Assert.Contains("WAA_WHISPER_URL", ex.Message);
        Assert.Contains("WAA_WHISPER_ALLOWED_HOSTS", ex.Message);
        Assert.DoesNotContain("pw", ex.Message);
    }

    [Fact]
    public void A_listed_host_is_accepted_and_the_ollama_list_does_not_count()
    {
        var env = (string url) => Env(("WAA_WHISPER_URL", url), ("WAA_WHISPER_ALLOWED_HOSTS", "pdl-rune-02.panoramicdata.com, PDL-RUNE-01.panoramicdata.com"));

        Assert.Equal("pdl-rune-02.panoramicdata.com", AnalysisSettings.Load(env("http://pdl-rune-02.panoramicdata.com:8080")).WhisperServer!.BaseUrl.Host);
        Assert.Equal("pdl-rune-01.panoramicdata.com", AnalysisSettings.Load(env("http://pdl-rune-01.panoramicdata.com:8080")).WhisperServer!.BaseUrl.Host);
        Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(env("http://other.panoramicdata.com:8080")));
        Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_WHISPER_URL", "http://pdl-rune-02.panoramicdata.com:8080"), ("WAA_OLLAMA_ALLOWED_HOSTS", "pdl-rune-02.panoramicdata.com"))));
    }

    [Theory]
    [InlineData("WAA_WHISPER_URL", "not a url", "absolute http(s) URL")]
    [InlineData("WAA_WHISPER_URL", "ftp://127.0.0.1/x", "absolute http(s) URL")]
    [InlineData("WAA_WHISPER_TIMEOUT_MINUTES", "9", "WAA_WHISPER_TIMEOUT_MINUTES")]
    [InlineData("WAA_WHISPER_TIMEOUT_MINUTES", "soon", "WAA_WHISPER_TIMEOUT_MINUTES")]
    [InlineData("WAA_WHISPER_CONCURRENCY", "0", "WAA_WHISPER_CONCURRENCY")]
    [InlineData("WAA_WHISPER_CONCURRENCY", "17", "WAA_WHISPER_CONCURRENCY")]
    [InlineData("WAA_WHISPER_RETRIES", "-1", "WAA_WHISPER_RETRIES")]
    [InlineData("WAA_WHISPER_RETRIES", "50", "WAA_WHISPER_RETRIES")]
    [InlineData("WAA_WHISPER_LANGUAGE", "english please", "WAA_WHISPER_LANGUAGE")]
    [InlineData("WAA_WHISPER_MODEL", "a/b", "WAA_WHISPER_MODEL")]
    [InlineData("WAA_WHISPER_TOKEN", "has space", "WAA_WHISPER_TOKEN")]
    [InlineData("WAA_WHISPER_TOKEN", "line\nbreak", "WAA_WHISPER_TOKEN")]
    public void Bad_values_are_refused_by_name(string name, string value, string expected)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_WHISPER_URL", "http://127.0.0.1:8080"), (name, value))));

        Assert.Contains(expected, ex.Message);
        if (name == "WAA_WHISPER_TOKEN") Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void Without_a_url_there_is_no_server_client_yet()
    {
        Assert.Null(AnalysisSettings.Load(Env()).WhisperServer);
    }
}
