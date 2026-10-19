extern alias web;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Consolidation;
using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests;

/// <summary>The Consolidation card on Status and the /consolidation deletion review page, against a stub consolidator (issue #104).</summary>
public sealed class ConsolidationPageTests : IAsyncLifetime
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-consolidation-web-" + Guid.NewGuid().ToString("N"));
    WebApplicationFactory<WebEntryPoint> _factory = null!;
    HttpClient _client = null!;
    StubHandler _consolidator = null!;
    readonly List<(HttpMethod Method, string Path, string Body)> _calls = [];
    string _healthJson = Healthy;
    bool _unreachable;
    TimeSpan _deletionDelay = TimeSpan.Zero;
    bool _deletionFailsAfterSend;
    readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 13, 0, 0, TimeSpan.Zero));

    const string Healthy = """
        { "station": "burst", "mode": "hold", "lastMergedHourUtc": "2026-10-15T11:00:00+01:00", "backlogHours": 3, "waitingForAnalysis": 2,
          "failures": [], "held": 14, "ready": 2, "heldFailed": 0, "deletedBytes": 5368709120, "lastRunUtc": "2026-10-17T12:00:00Z", "lastError": null }
        """;

    const string Failing = """
        { "station": "burst", "mode": "hold", "lastMergedHourUtc": null, "backlogHours": 1, "waitingForAnalysis": 0,
          "failures": [ { "hourUtc": "2026-10-15T09:00:00Z", "reason": "merged hour failed verification" } ],
          "held": 0, "ready": 0, "heldFailed": 0, "deletedBytes": 0, "lastRunUtc": null, "lastError": null }
        """;

    const string Ready = """
        [ { "recorder": "a", "station": "burst", "file": "2026/10-15/100000.mp3", "sha256": "aa", "bytes": 1048576, "heldUtc": "2026-10-08T10:00:00Z", "mergedFiles": ["2026/10-15/100000.mp3"], "status": "ready", "checkedUtc": "2026-10-17T11:00:00Z", "reason": null },
          { "recorder": "b", "station": "burst", "file": "2026/10-15/100000.mp3", "sha256": "bb", "bytes": 2097152, "heldUtc": "2026-10-08T10:00:00Z", "mergedFiles": ["2026/10-15/100000.mp3"], "status": "ready", "checkedUtc": "2026-10-17T11:00:00Z", "reason": null },
          { "recorder": "a", "station": "burst", "file": "2026/10-16/090000.mp3", "sha256": "cc", "bytes": 3145728, "heldUtc": "2026-10-09T09:00:00Z", "mergedFiles": ["2026/10-16/090000.mp3"], "status": "ready", "checkedUtc": "2026-10-17T11:00:00Z", "reason": null } ]
        """;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        _consolidator = new StubHandler(req =>
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _calls.Add((req.Method, req.RequestUri!.AbsolutePath, body));
            if (_unreachable) throw new HttpRequestException("Connection refused (consolidator:8080)");
            return (req.Method.Method, req.RequestUri.AbsolutePath) switch
            {
                ("GET", "/healthz") => Json(_healthJson),
                ("GET", "/held") => Json(Ready),
                ("POST", "/deletions") => Json(DeletionsReply(body)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        _factory = new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WAA_STATION", "burst");
            b.UseSetting("WAA_RECORDERS", $"a={Path.Combine(_root, "a")},b={Path.Combine(_root, "b")}");
            b.UseSetting("WAA_CONSOLIDATOR", "http://consolidator:8080");
            b.UseSetting("WAA_CACHE", Path.Combine(_root, "cache"));
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddHttpClient(ConsolidatorClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _consolidator);
                services.AddHttpClient(ConsolidatorClient.DeletionsHttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SlowDeletions(this) { InnerHandler = _consolidator });
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }


    // Sits in front of the stub for deletions only: it can take longer than the 3 s health timeout, or fail after the request was sent.
    sealed class SlowDeletions(ConsolidationPageTests owner) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner._deletionDelay > TimeSpan.Zero) await Task.Delay(owner._deletionDelay, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            if (owner._deletionFailsAfterSend) throw new HttpRequestException("The connection was closed before the reply arrived");
            return response;
        }
    }
    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Deletes everything except recorder b's file, which the consolidator refuses.
    static string DeletionsReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var results = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => new
        {
            recorder = i.GetProperty("recorder").GetString(),
            file = i.GetProperty("file").GetString(),
            deleted = i.GetProperty("recorder").GetString() != "b",
            reason = i.GetProperty("recorder").GetString() == "b" ? "the last recheck is older than 24 hours" : null,
        });
        return JsonSerializer.Serialize(results);
    }

    ConsolidatorClient Client() => _factory.Services.GetRequiredService<ConsolidatorClient>();

    [Fact]
    public async Task Status_shows_the_consolidation_card_and_marks_a_failure_red()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));

        Assert.Contains("data-card=\"consolidation\" data-level=\"Green\"", html);
        Assert.Contains("Consolidation", html);
        Assert.Contains("hold", html);
        Assert.Contains("2026-10-15 10:00:00 UTC", html); // sent as 11:00+01:00, shown in UTC
        Assert.Contains("5.0 GiB", html);
        Assert.Contains("href=\"consolidation\"", html);

        _healthJson = Failing;
        await _factory.Services.GetRequiredService<StatusService>().ReadAsync(CancellationToken.None);
        html = WebUtility.HtmlDecode(await _client.GetStringAsync("/")); // reuses the fresh reading just taken
        Assert.Contains("data-card=\"consolidation\" data-level=\"Red\"", html);
        Assert.Contains("merged hour failed verification", html);
        Assert.Contains("2026-10-15 09:00:00 UTC", html);
    }

    [Fact]
    public async Task Status_marks_an_unreachable_consolidator_amber()
    {
        _unreachable = true;
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));

        Assert.Contains("data-card=\"consolidation\" data-level=\"Amber\"", html);
        Assert.Contains("did not answer", html);
    }

    async Task<string> StatusWith(string healthJson)
    {
        _healthJson = healthJson;
        await _factory.Services.GetRequiredService<StatusService>().ReadAsync(CancellationToken.None);
        return WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Status_marks_an_hour_waiting_for_analysis_more_than_48_hours_past_eligibility_amber()
    {
        // 2026-10-15 10:00 became eligible (old enough) at 2026-10-16 11:00; at 2026-10-17 13:00 that is 26 h ago: still green.
        var waiting = Healthy.Replace("\"lastError\": null", "\"lastError\": null, \"oldestWaitingHourUtc\": \"2026-10-15T10:00:00Z\"", StringComparison.Ordinal);
        Assert.Contains("data-card=\"consolidation\" data-level=\"Green\"", await StatusWith(waiting));

        // 48 h after it became eligible it is amber, and the card says which hour.
        _time.Set(new DateTimeOffset(2026, 10, 18, 11, 1, 0, TimeSpan.Zero));
        var html = await StatusWith(waiting.Replace("2026-10-17T12:00:00Z", "2026-10-18T11:00:00Z", StringComparison.Ordinal));
        Assert.Contains("data-card=\"consolidation\" data-level=\"Amber\"", html);
        Assert.Contains("2026-10-15 10:00:00 UTC", html);
        Assert.Contains("waiting for analysis", html);
    }

    [Fact]
    public async Task Status_marks_a_consolidator_that_has_not_run_for_3_hours_amber()
    {
        Assert.Contains("data-card=\"consolidation\" data-level=\"Green\"", await StatusWith(Healthy));

        _time.Set(new DateTimeOffset(2026, 10, 17, 15, 1, 0, TimeSpan.Zero));
        var html = await StatusWith(Healthy);
        Assert.Contains("data-card=\"consolidation\" data-level=\"Amber\"", html);
        Assert.Contains("2026-10-17 12:00:00 UTC", html);
    }

    [Fact]
    public async Task The_review_page_lists_ready_originals_by_utc_day()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/consolidation"));

        Assert.Contains("2026-10-15 UTC", html);
        Assert.Contains("2026-10-16 UTC", html);
        Assert.True(html.IndexOf("2026-10-15 UTC", StringComparison.Ordinal) < html.IndexOf("2026-10-16 UTC", StringComparison.Ordinal));
        Assert.Contains("10:00:00 UTC", html);
        Assert.Contains("Recorder A", html);
        Assert.Contains("Recorder B", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.DoesNotContain(_calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Confirming_posts_the_chosen_items_and_shows_each_result()
    {
        var review = new ConsolidationReview(Client());
        await review.LoadAsync(CancellationToken.None);
        var a = review.Items.First(i => i.Recorder == "a" && i.File.StartsWith("2026/10-15", StringComparison.Ordinal));
        var b = review.Items.First(i => i.Recorder == "b");
        review.Toggle(a);
        review.Toggle(b);
        review.RequestConfirmation();

        await review.ConfirmAsync("loopback", CancellationToken.None);

        var post = Assert.Single(_calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("/deletions", post.Path);
        using var doc = JsonDocument.Parse(post.Body);
        Assert.Equal("loopback", doc.RootElement.GetProperty("confirmedBy").GetString());
        var posted = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("recorder").GetString(), i.GetProperty("file").GetString())).ToList();
        Assert.Equal([("a", "2026/10-15/100000.mp3"), ("b", "2026/10-15/100000.mp3")], posted);
        Assert.DoesNotContain(("a", "2026/10-16/090000.mp3"), posted); // chosen by nobody, so not sent
        Assert.Equal(2, review.Results.Count);
        Assert.True(review.Results.Single(r => r.Recorder == "a").Deleted);
        var refused = review.Results.Single(r => r.Recorder == "b");
        Assert.False(refused.Deleted);
        Assert.Equal("the last recheck is older than 24 hours", refused.Reason);
        Assert.Empty(review.Selected);
        Assert.False(review.Confirming);
    }

    [Fact]
    public async Task A_deletion_slower_than_the_health_timeout_still_shows_each_result()
    {
        var review = new ConsolidationReview(Client());
        await review.LoadAsync(CancellationToken.None);
        review.Toggle(review.Items.First(i => i.Recorder == "a"));
        review.RequestConfirmation();
        _deletionDelay = TimeSpan.FromSeconds(3.5); // longer than the 3 s used for /healthz and /held

        await review.ConfirmAsync("loopback", CancellationToken.None);

        Assert.Null(review.Error);
        Assert.Null(review.UnknownOutcome);
        Assert.True(Assert.Single(review.Results).Deleted);
    }

    [Fact]
    public async Task A_deletion_that_fails_after_it_was_sent_reports_an_unknown_outcome_and_reloads_the_list()
    {
        var review = new ConsolidationReview(Client());
        await review.LoadAsync(CancellationToken.None);
        review.Toggle(review.Items[0]);
        review.RequestConfirmation();
        _deletionFailsAfterSend = true;
        var heldReads = _calls.Count(c => c.Path == "/held");

        await review.ConfirmAsync("loopback", CancellationToken.None);

        Assert.Single(_calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("unknown", review.UnknownOutcome);
        Assert.Contains("reloaded", review.UnknownOutcome);
        Assert.Null(review.Error); // not "could not reach", as if nothing had happened
        Assert.Empty(review.Results);
        Assert.Equal(heldReads + 1, _calls.Count(c => c.Path == "/held"));
        Assert.Empty(review.Selected);
    }

    [Fact]
    public async Task A_repeated_confirmation_is_refused_and_posts_nothing_more()
    {
        var review = new ConsolidationReview(Client());
        await review.LoadAsync(CancellationToken.None);
        review.Toggle(review.Items[0]);
        review.RequestConfirmation();
        await review.ConfirmAsync("loopback", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ConfirmAsync("loopback", CancellationToken.None));

        Assert.Single(_calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Nothing_is_deleted_without_the_confirmation_step()
    {
        var review = new ConsolidationReview(Client());
        await review.LoadAsync(CancellationToken.None);
        review.Toggle(review.Items[0]);

        // Selecting is not enough, and neither is asking for the confirmation: only ConfirmAsync after it posts.
        Assert.False(review.Confirming);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ConfirmAsync("loopback", CancellationToken.None));
        review.RequestConfirmation();
        Assert.True(review.Confirming);
        review.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ConfirmAsync("loopback", CancellationToken.None));
        Assert.DoesNotContain(_calls, c => c.Method == HttpMethod.Post);

        // An empty selection cannot be confirmed at all.
        review.Toggle(review.Items[0]);
        review.RequestConfirmation();
        Assert.False(review.Confirming);
    }
}
