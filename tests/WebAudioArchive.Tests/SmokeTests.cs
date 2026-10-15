namespace WebAudioArchive.Tests;

public class SmokeTests
{
    [Fact]
    public void Fixtures_are_copied_next_to_the_tests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "burst-schedule-2026-10-15.html");
        Assert.True(File.Exists(path), $"Fixture missing: {path}. Check the None Include in the test csproj.");
    }
}
