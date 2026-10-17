using System.Globalization;
using Humanizer;

namespace WebAudioArchive.Web.Status;

/// <summary>Readable durations such as "21 hours, 20 minutes, 25 seconds", in English whatever the host culture.</summary>
public static class DurationText
{
    public static string Humanize(double? seconds, int precision = 3)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || seconds.Value < 0)
        {
            return "-";
        }

        var span = TimeSpan.FromSeconds(Math.Floor(seconds.Value));
        if (span == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        return span.Humanize(precision, CultureInfo.InvariantCulture, maxUnit: TimeUnit.Day, minUnit: TimeUnit.Second);
    }
}
