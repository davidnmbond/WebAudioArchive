using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WorkerHostTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Healthz_reports_a_recording_station_and_the_archive_has_a_part_file()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(60) };

        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "stations.json");
        File.WriteAllText(config, $$"""
            { "stations": [ { "id": "fake", "name": "Fake FM", "listenUrl": "{{station.ListenUrl}}" } ] }
            """);
        var data = Path.Combine(_root, "data");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WAA_CONFIG", config);
            b.UseSetting("WAA_DATA", data);
            b.UseSetting("WAA_INSTANCE_ID", "host-test");
        });
        var client = factory.CreateClient();

        JsonElement body = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            var current = body.GetProperty("stations")[0];
            if (current.GetProperty("lastFrameWrittenUtc").ValueKind != JsonValueKind.Null
                && current.GetProperty("currentSegmentStartUtc").ValueKind != JsonValueKind.Null
                && current.GetProperty("coverageComputedUtc").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }

        Assert.Equal("host-test", body.GetProperty("instance").GetString());
        var s = body.GetProperty("stations")[0];
        Assert.Equal("fake", s.GetProperty("id").GetString());
        Assert.True(s.GetProperty("connected").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastByteUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastFrameWrittenUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("secondsSinceLastFrameWritten").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("lastError").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("currentSegmentStartUtc").ValueKind);
        // Coverage comes from the background refresh, not from reading sidecars on each request.
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("coverageComputedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Number, s.GetProperty("coverage24h").ValueKind);
        Assert.Equal(JsonValueKind.Number, s.GetProperty("coverage30d").ValueKind);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(data, "fake", "master"), "*.part", SearchOption.AllDirectories));
    }
}
