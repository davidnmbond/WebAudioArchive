extern alias web;

using web::WebAudioArchive.Web;

namespace WebAudioArchive.Tests.Support;

public static class TestSettings
{
    public static WebSettings Create(params (string Id, string Root)[] recorders) => new(
        "burst",
        recorders.Select(r => new RecorderSettings(r.Id, r.Root, null)).ToList(),
        Path.Combine(Path.GetTempPath(), "waa-cache-" + Guid.NewGuid().ToString("N")));
}

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset to) => _now = to;
}
