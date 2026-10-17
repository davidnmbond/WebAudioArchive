namespace WebAudioArchive.Analysis.Work;

/// <summary>
/// Free space on the disk holding the analysis output, which on a single-disk host is the recorders' disk too. Below
/// the threshold analysis stops between chunks and does not start new jobs, so the recorders keep the space (spec 3.5).
/// </summary>
public sealed class DiskGuard(string path, long minFreeBytes, Func<string, long>? freeBytes = null)
{
    public long MinFreeBytes => minFreeBytes;

    public long FreeBytes() => (freeBytes ?? Free)(path);

    public bool IsLow() => FreeBytes() < minFreeBytes;

    static long Free(string p)
    {
        var full = Path.GetFullPath(p);
        Directory.CreateDirectory(full);
        return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full).AvailableFreeSpace;
    }
}
