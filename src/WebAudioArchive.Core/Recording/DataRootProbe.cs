namespace WebAudioArchive.Core.Recording;

/// <summary>Start-up check that the data root can be written, so a root-owned or read-only volume fails loudly.</summary>
public static class DataRootProbe
{
    /// <summary>Returns one message per problem found; empty when every station folder is writable.</summary>
    public static IReadOnlyList<string> Check(string dataRoot, IEnumerable<string> stationIds)
    {
        var errors = new List<string>();
        foreach (var id in stationIds)
        {
            var dir = Path.Combine(dataRoot, id);
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".waa-write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, [0]);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"data folder '{dir}' is not writable: {ex.Message}");
            }
        }

        return errors;
    }
}
