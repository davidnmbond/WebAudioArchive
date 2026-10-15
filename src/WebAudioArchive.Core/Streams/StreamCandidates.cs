using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Streams;

public sealed record StreamCandidate(Uri Url, string How);

public static partial class StreamCandidates
{
    /// <summary>Candidate stream URLs in a station page, best first. The order is: RadioJar widget, audio or source element, media URL in the page.</summary>
    public static IReadOnlyList<StreamCandidate> FromHtml(string html, Uri pageUrl)
    {
        var list = new List<StreamCandidate>();

        foreach (Match m in RadioJar().Matches(html))
            list.Add(new StreamCandidate(new Uri($"https://stream.rcs.revma.com/{m.Groups["n"].Value}"), "radiojar-widget"));

        foreach (Match m in MediaElement().Matches(html))
            if (Uri.TryCreate(pageUrl, m.Groups["u"].Value, out var u) && IsHttp(u))
                list.Add(new StreamCandidate(u, "media-element"));

        foreach (Match m in MediaUrl().Matches(html))
            if (Uri.TryCreate(m.Value, UriKind.Absolute, out var u) && IsHttp(u))
                list.Add(new StreamCandidate(u, "media-url-in-page"));

        return list.DistinctBy(c => c.Url).ToList();
    }

    static bool IsHttp(Uri u) => u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;

    [GeneratedRegex(@"streamName\s*:\s*['""](?<n>[A-Za-z0-9_-]+)['""]")]
    private static partial Regex RadioJar();

    [GeneratedRegex(@"<(?:audio|source)\b[^>]*\bsrc\s*=\s*['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex MediaElement();

    [GeneratedRegex(@"https?://[^\s'""<>\\]+?\.(?:mp3|pls|m3u)(?:\?[^\s'""<>\\]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex MediaUrl();
}
