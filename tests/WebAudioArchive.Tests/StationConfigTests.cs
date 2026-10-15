using WebAudioArchive.Core.Config;

namespace WebAudioArchive.Tests;

public class StationConfigTests
{
    const string Good = """
        { "stations": [ { "id": "burst", "name": "Burst Radio",
            "listenUrl": "https://www.burstradio.org/listen",
            "scheduleUrl": "https://www.burstradio.org/schedule" } ] }
        """;

    [Fact]
    public void Valid_config_parses_with_defaults()
    {
        var s = Assert.Single(StationConfigLoader.Parse(Good, "test"));
        Assert.Equal("burst", s.Id);
        Assert.Equal("Europe/London", s.Timezone);
        Assert.True(s.Enabled);
    }

    [Fact]
    public void Unknown_field_is_rejected_by_name()
    {
        var json = Good.Replace("\"listenUrl\"", "\"listenUrls\"");
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse(json, "test"));
        Assert.Contains(ex.Errors, e => e.Contains("listenUrls"));
    }

    [Fact]
    public void All_problems_are_reported_together()
    {
        const string json = """
            { "stations": [
              { "id": "Bad Id", "name": "A", "listenUrl": "not a url", "timezone": "Nowhere/City" },
              { "id": "ok", "name": "B", "listenUrl": "https://example.org/live" },
              { "id": "ok", "name": "C", "listenUrl": "https://example.org/live2" } ] }
            """;
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse(json, "test"));

        Assert.Contains(ex.Errors, e => e.Contains("Bad Id"));
        Assert.Contains(ex.Errors, e => e.Contains("listenUrl"));
        Assert.Contains(ex.Errors, e => e.Contains("Nowhere/City"));
        Assert.Contains(ex.Errors, e => e.Contains("duplicate") && e.Contains("ok"));
    }

    [Fact]
    public void Empty_station_list_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse("""{ "stations": [] }""", "test"));
        Assert.Contains(ex.Errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void Shipped_config_is_valid()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "stations.json"));
        Assert.True(File.Exists(path), $"Expected the shipped config at {path}");
        var s = Assert.Single(StationConfigLoader.Load(path));
        Assert.Equal("burst", s.Id);
    }
}
