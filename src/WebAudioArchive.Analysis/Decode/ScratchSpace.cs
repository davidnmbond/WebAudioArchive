using System.Text.RegularExpressions;

namespace WebAudioArchive.Analysis.Decode;

public sealed class ScratchFullException(string message) : Exception(message);

/// <summary>
/// The only place decoded audio is written: a size-bounded folder (a tmpfs in the container). Each job gets its own
/// folder, deleted when the lease is disposed, success or failure. The budget counts files on disk plus what running
/// jobs have reserved, so a job that would overflow it is refused before it starts.
/// </summary>
public sealed partial class ScratchSpace
{
    readonly object _gate = new();
    readonly Dictionary<string, long> _reserved = new();

    public ScratchSpace(string root, long maxBytes)
    {
        Root = Path.GetFullPath(root);
        MaxBytes = maxBytes;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public long MaxBytes { get; }

    public long UsedBytes() => Directory.Exists(Root)
        ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch (IOException)
            {
                return 0L;
            }
        })
        : 0;

    /// <summary>Deletes everything under the root. Call at start-up, before any job runs. Returns how many entries went.</summary>
    public int SweepStale()
    {
        var n = 0;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            TryDelete(d);
            n++;
        }

        foreach (var f in Directory.EnumerateFiles(Root))
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }

            n++;
        }

        return n;
    }

    public ScratchLease Acquire(string jobName, long expectedBytes)
    {
        var name = Unsafe().Replace(jobName, "_") + "-" + Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            var wanted = UsedBytes() + _reserved.Values.Sum() + expectedBytes;
            if (wanted > MaxBytes)
                throw new ScratchFullException($"scratch {Root} would need {wanted / 1048576.0:0.0} MB, over its {MaxBytes / 1048576.0:0.0} MB limit (WAA_SCRATCH_MAX_MB)");
            _reserved[name] = expectedBytes;
        }

        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return new ScratchLease(dir, () =>
        {
            TryDelete(dir);
            lock (_gate) _reserved.Remove(name);
        });
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("[^A-Za-z0-9-]")]
    private static partial Regex Unsafe();
}

public sealed class ScratchLease : IDisposable
{
    readonly Action _release;
    bool _disposed;

    internal ScratchLease(string dir, Action release)
    {
        Dir = dir;
        _release = release;
    }

    public string Dir { get; }

    public string PathFor(string fileName) => Path.Combine(Dir, fileName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release();
    }
}
