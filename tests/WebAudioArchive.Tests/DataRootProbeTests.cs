using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class DataRootProbeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (File.Exists(_root)) File.Delete(_root);
    }

    [Fact]
    public void A_writable_root_passes_and_leaves_no_probe_files()
    {
        var errors = DataRootProbe.Check(_root, ["a", "b"]);

        Assert.Empty(errors);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_unwritable_root_is_reported_per_station()
    {
        File.WriteAllText(_root, "a file where the data folder should be");

        var errors = DataRootProbe.Check(_root, ["a", "b"]);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("not writable", e));
    }
}
