extern alias web;

using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests;

public class DurationTextTests
{
    [Theory]
    [InlineData(76825.0, "21 hours, 20 minutes, 25 seconds")]
    [InlineData(574.0, "9 minutes, 34 seconds")]
    [InlineData(59.9, "59 seconds")]
    [InlineData(1.0, "1 second")]
    [InlineData(3600.0, "1 hour")]
    [InlineData(90061.0, "1 day, 1 hour, 1 minute")]
    [InlineData(0.0, "0 seconds")]
    public void Durations_read_in_plain_english(double seconds, string expected) =>
        Assert.Equal(expected, DurationText.Humanize(seconds));

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_invalid_values_show_a_dash(double? seconds) =>
        Assert.Equal("-", DurationText.Humanize(seconds));

    [Fact]
    public void The_host_culture_does_not_change_the_wording()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.Equal("2 hours, 5 minutes", DurationText.Humanize(7500));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
