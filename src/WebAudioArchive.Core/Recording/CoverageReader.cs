namespace WebAudioArchive.Core.Recording;

public sealed record Coverage(double CoveredSeconds, double Fraction);

public static class CoverageReader
{
    /// <summary>Seconds of audio in the window ending at <paramref name="now"/>, from segment sidecars: each segment's span clipped to the window, minus gaps that fall inside that span.</summary>
    public static Coverage Read(string masterDir, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        double covered = 0;
        if (Directory.Exists(masterDir))
        {
            foreach (var path in Directory.EnumerateFiles(masterDir, "*.json", SearchOption.AllDirectories))
            {
                SegmentMeta? meta;
                try { meta = SegmentMetaStore.Read(path); }
                catch (System.Text.Json.JsonException) { continue; }
                if (meta is null) continue;

                var start = Max(meta.StartUtc, from);
                var end = Min(meta.EndUtc, now);
                if (end <= start) continue;

                var seconds = (end - start).TotalSeconds;
                foreach (var gap in meta.Gaps)
                {
                    var gs = Max(gap.StartUtc, start);
                    var ge = Min(gap.EndUtc, end);
                    if (ge > gs) seconds -= (ge - gs).TotalSeconds;
                }

                covered += Math.Max(0, seconds);
            }
        }

        return new Coverage(covered, Math.Min(1, covered / window.TotalSeconds));
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
