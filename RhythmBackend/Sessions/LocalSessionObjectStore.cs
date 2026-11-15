using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace RhythmBackend.Sessions;

// Development/test only: atomic renames plus process-wide striped locks. Production
// uses Blob ETags, which coordinate all App Service instances and survive redeploys.
public sealed class LocalSessionObjectStore : ISessionObjectStore
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly string _directory;

    public LocalSessionObjectStore(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        _directory = Path.EndsInDirectorySeparator(fullPath) ? fullPath : fullPath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_directory);
    }

    private string Resolve(string name)
    {
        string path = Path.GetFullPath(Path.Combine(_directory, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid storage object path.");
        return path;
    }

    private static string Version(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private SemaphoreSlim Gate(string name) => Gates[(uint)StringComparer.Ordinal.GetHashCode(Resolve(name)) % Gates.Length];

    public async Task<StoredObject?> ReadAsync(string name, CancellationToken ct)
    {
        var gate = Gate(name);
        await gate.WaitAsync(ct);
        try
        {
            string path = Resolve(name);
            if (!File.Exists(path)) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            return new(bytes, Version(bytes), File.GetLastWriteTimeUtc(path));
        }
        finally { gate.Release(); }
    }

    public async Task<bool> WriteAsync(string name, byte[] bytes, string? expectedVersion, CancellationToken ct)
    {
        var gate = Gate(name);
        await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            string path = Resolve(name);
            bool exists = File.Exists(path);
            if (expectedVersion is null ? exists : !exists || Version(await File.ReadAllBytesAsync(path, ct)) != expectedVersion)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    public async Task DeleteAsync(string name, string? expectedVersion, CancellationToken ct)
    {
        var gate = Gate(name);
        await gate.WaitAsync(ct);
        try
        {
            string path = Resolve(name);
            if (!File.Exists(path)) return;
            if (expectedVersion is not null && Version(await File.ReadAllBytesAsync(path, ct)) != expectedVersion) return;
            File.Delete(path);
        }
        finally { gate.Release(); }
    }

    public async IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (string path in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetRelativePath(_directory, path).Replace('\\', '/');
            if (name.StartsWith(prefix, StringComparison.Ordinal) && !name.EndsWith(".tmp"))
                yield return new(name, File.GetLastWriteTimeUtc(path));
        }
        await Task.CompletedTask;
    }
}
