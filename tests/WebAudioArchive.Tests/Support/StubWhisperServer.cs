using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A real HTTP server on 127.0.0.1 (random port) that behaves like whisper.cpp's server for the two routes the client uses:
/// POST /inference (multipart, answers a canned verbose_json of synthetic words) and GET /health. Each route can be replaced
/// to simulate a slow server, a dropped connection, a redirect or a large answer.
/// </summary>
public sealed class StubWhisperServer : IAsyncDisposable
{
    /// <summary>
    /// The shape measured in the D19 spike (whisper.cpp v1.9.5 verbose_json), with synthetic text. Like the real server it drops special
    /// and timestamp tokens (id at or above eot) from "words", and a segment with only special tokens has no "words" key at all.
    /// </summary>
    public const string CannedJson = """
        {"task":"transcribe","language":"english","duration":6.0,"text":" Um, hello and welcome.","segments":[
         {"id":0,"text":" Um, hello and welcome.","start":0,"end":3.5,"tokens":[1,2,3],
          "words":[{"word":" Um","start":0.1,"end":0.3,"t_dtw":-1,"probability":0.8},
                   {"word":",","start":0.3,"end":0.32,"t_dtw":-1,"probability":0.95},
                   {"word":" hello","start":0.5,"end":0.9,"t_dtw":-1,"probability":0.97},
                   {"word":" and","start":0.9,"end":1.0,"t_dtw":-1,"probability":0.99},
                   {"word":" wel","start":1.1,"end":1.3,"t_dtw":-1,"probability":0.9},
                   {"word":"come","start":1.3,"end":1.6,"t_dtw":-1,"probability":0.7},
                   {"word":".","start":1.6,"end":1.62,"t_dtw":-1,"probability":0.9}],
          "temperature":0,"avg_logprob":-0.159,"no_speech_prob":9.4e-11},
         {"id":1,"text":" [BLANK_AUDIO]","start":3.5,"end":6,"tokens":[50257,50365],"temperature":0,"avg_logprob":-0.05,"no_speech_prob":0.9}]}
        """;

    readonly WebApplication _app;
    readonly object _lock = new();
    readonly List<SeenRequest> _seen = [];
    int _inFlight;

    public Uri BaseUrl { get; }

    public int Requests => Seen.Count;

    public int MaxInFlight { get; private set; }

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_lock) return [.. _seen];
        }
    }

    /// <summary>Answers POST /inference. Defaults to the canned verbose_json.</summary>
    public Func<HttpContext, Task> Inference { get; set; } = async ctx =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(CannedJson);
    };

    /// <summary>Answers GET /health. Defaults to {"status":"ok"}.</summary>
    public Func<HttpContext, Task> Health { get; set; } = async ctx =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"status\":\"ok\"}");
    };

    StubWhisperServer(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<StubWhisperServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        StubWhisperServer? self = null;

        app.MapPost("/inference", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var fields = form.ToDictionary(f => f.Key, f => f.Value.ToString());
            byte[]? bytes = null;
            string? name = null, type = null;
            if (form.Files.Count > 0)
            {
                var f = form.Files[0];
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ctx.RequestAborted);
                bytes = ms.ToArray();
                name = f.FileName;
                type = f.ContentType;
            }

            var auth = ctx.Request.Headers.Authorization.ToString();
            self!.Enter(new SeenRequest(HttpMethod.Post, ctx.Request.Path, auth.Length > 0 ? auth : null, fields, bytes, name, type));
            try
            {
                await self.Inference(ctx);
            }
            finally
            {
                self.Leave();
            }
        });
        app.MapGet("/health", async ctx => await self!.Health(ctx));

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new StubWhisperServer(app, new Uri(address));
        return self;
    }

    void Enter(SeenRequest r)
    {
        lock (_lock)
        {
            _seen.Add(r);
            MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
        }
    }

    void Leave()
    {
        lock (_lock) _inFlight--;
    }

    /// <summary>A loopback URL where nothing listens (the port was free a moment ago).</summary>
    public static Uri ClosedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
