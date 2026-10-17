using System.Text.RegularExpressions;

namespace WebAudioArchive.Tests.Support;

/// <summary>Files in the repository checkout (found by walking up to WebAudioArchive.slnx), for checks on deployment files.</summary>
public static partial class RepoFiles
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebAudioArchive.slnx"))) dir = dir.Parent;
            Assert.True(dir is not null, "WebAudioArchive.slnx was not found above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    /// <summary>Each service under "services:" and its block of lines (to the next service or top-level key).</summary>
    public static Dictionary<string, string> ComposeServices(string relative)
    {
        var services = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var inServices = false;
        var block = new List<string>();
        foreach (var line in Read(relative).Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("services:", StringComparison.Ordinal)) { inServices = true; continue; }
            if (!inServices) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                if (current is not null) services[current] = string.Join('\n', block);
                current = null;
                inServices = false;
                continue;
            }

            var m = ServiceLine().Match(line);
            if (m.Success)
            {
                if (current is not null) services[current] = string.Join('\n', block);
                current = m.Groups[1].Value;
                block = [];
                continue;
            }

            if (current is not null) block.Add(line);
        }

        if (current is not null) services[current] = string.Join('\n', block);
        return services;
    }

    [GeneratedRegex("^  ([a-z0-9-]+):\\s*$")]
    private static partial Regex ServiceLine();
}
