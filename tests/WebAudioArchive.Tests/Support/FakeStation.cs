using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace WebAudioArchive.Tests.Support;

/// <summary>What one connection to /stream does. The audio is sent with ICY metadata interleaved when MetaInt > 0.</summary>
public sealed class StreamPlan
{
    public byte[] Audio { get; init; } = [];
    public int MetaInt { get; init; } = 16000;
    public Func<int, string?> TitleForBlock { get; init; } = _ => null;
    public Task? Gate { get; init; }            // wait for this before sending anything
    public bool DropAfterSend { get; init; }    // abort the connection after sending
    public Task? DropGate { get; init; }        // with DropAfterSend: wait for this after sending, before aborting
    public bool HoldOpen { get; init; } = true; // keep the connection open and silent after sending
    public int Status { get; init; } = 200;
}

public sealed class FakeStation : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly ConcurrentDictionary<string, int> _hits = new();
    int _streamConnections;

    public Func<int, StreamPlan> Plan { get; set; } = _ => new StreamPlan();
    public Uri BaseUrl { get; private set; } = null!;
    public Uri ListenUrl => new(BaseUrl, "/listen");
    public int Hits(string path) => _hits.GetValueOrDefault(path);
    public int StreamConnections => Volatile.Read(ref _streamConnections);

    FakeStation(WebApplication app) => _app = app;

    public static async Task<FakeStation> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var station = new FakeStation(app);

        app.MapGet("/listen", ctx =>
        {
            station.Count("/listen");
            ctx.Response.ContentType = "text/html";
            return ctx.Response.WriteAsync("""<html><body><audio src="/redirect-stream"></audio></body></html>""");
        });

        app.MapGet("/redirect-stream", ctx =>
        {
            var n = station.Count("/redirect-stream");
            ctx.Response.Redirect($"/stream?token={n}");
            return Task.CompletedTask;
        });

        app.MapGet("/stream", async ctx =>
        {
            station.Count("/stream");
            if (!ctx.Request.Headers.ContainsKey("Icy-MetaData"))
            {
                // The resolver probes without Icy-MetaData: answer headers only, and do not use up a plan.
                ctx.Response.ContentType = "audio/mpeg";
                return;
            }

            var index = Interlocked.Increment(ref station._streamConnections) - 1;
            var plan = station.Plan(index);
            if (plan.Status != 200)
            {
                ctx.Response.StatusCode = plan.Status;
                return;
            }

            ctx.Response.ContentType = "audio/mpeg";
            ctx.Response.Headers["icy-name"] = "Fake FM";
            ctx.Response.Headers["icy-genre"] = "Test";
            if (plan.MetaInt > 0) ctx.Response.Headers["icy-metaint"] = plan.MetaInt.ToString();
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            if (plan.Gate is not null) await plan.Gate.WaitAsync(ctx.RequestAborted);

            var wire = plan.MetaInt > 0 ? IcyTestData.Build(plan.Audio, plan.MetaInt, plan.TitleForBlock) : plan.Audio;
            for (var i = 0; i < wire.Length; i += 1000)
            {
                await ctx.Response.Body.WriteAsync(wire.AsMemory(i, Math.Min(1000, wire.Length - i)), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            if (plan.DropAfterSend)
            {
                if (plan.DropGate is not null) await plan.DropGate.WaitAsync(ctx.RequestAborted);
                // Give the client time to drain its socket: an RST discards unread data on Windows.
                await Task.Delay(300, ctx.RequestAborted);
                ctx.Abort();
                return;
            }

            if (plan.HoldOpen) await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        });

        await app.StartAsync();
        station.BaseUrl = new Uri(app.Urls.First());
        return station;
    }

    int Count(string path) => _hits.AddOrUpdate(path, 1, (_, v) => v + 1);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
