using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MegaHub.Core.Plugins;

/// <summary>
/// A plugin folder held still while it is judged and loaded. The folder is writable by every program of the user, so "check the files, then
/// load the files" has a gap in which something could swap a DLL. This closes it: every file is opened once with <see cref="FileShare.Read"/>
/// (nobody can write to it, replace it or delete it while it is held), every folder of the tree is held the same way (so none can be renamed
/// or replaced by a link), each handle is compared with the path it is supposed to belong to (<c>GetFinalPathNameByHandle</c>: a link or a
/// swapped path shows up there), the hash that the trust list is compared with is taken from exactly these handles, and the loader
/// (<see cref="PluginManager.PluginLoadContext"/>) may load only paths that are in this snapshot. A file that shows up in the folder after the
/// hash is not part of it and is refused by the loader (managed and native). The hash is the one <see cref="PluginTrust.HashFolder"/> computes,
/// so a recorded entry matches either way. Held for as long as the plugin runs (its DLLs are loaded lazily); <see cref="Dispose"/> lets go.
/// Links (symbolic links, junctions) inside a plugin folder are refused: a package never contains one.
/// </summary>
internal sealed class PluginFolderSnapshot : IDisposable
{
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SafeFileHandle> _folders = [];
    private bool _disposed;

    public string Directory { get; }

    /// <summary>The folder hash, taken from the held handles (lower-case hex SHA-256).</summary>
    public string Hash { get; private set; } = "";

    private PluginFolderSnapshot(string directory) => Directory = Path.GetFullPath(directory);

    /// <summary>Opens and hashes the folder. Throws <see cref="IOException"/> when a file cannot be held (it is in use for writing, or a link is in the way).</summary>
    public static PluginFolderSnapshot Open(string dir)
    {
        var snapshot = new PluginFolderSnapshot(dir);
        try
        {
            snapshot.Fill();
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private void Fill()
    {
        var root = Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // The folders first: held, so none of them can be renamed, deleted or replaced by a link while the plugin runs.
        string? canonicalRoot = null;
        var folders = new List<string> { Directory };
        folders.AddRange(System.IO.Directory.EnumerateDirectories(Directory, "*", SearchOption.AllDirectories));
        foreach (var folder in folders)
        {
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("link inside the plugin folder");
            var handle = HoldFolder(folder);
            if (handle is null) continue;
            _folders.Add(handle);
            var final = FinalPath(handle);
            var withSeparator = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = withSeparator.Length > root.Length ? withSeparator[root.Length..].TrimEnd('\\') : "";
            if (canonicalRoot is null) canonicalRoot = final;
            else if (final is not null && canonicalRoot is not null && !SamePath(final, Path.Combine(canonicalRoot, relative))) throw new IOException("the folder is not where its path says");
        }
        var names = System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Where(f => !PluginTrust.Ignored(Path.GetFileName(f)))
            .Select(f => (Full: Path.GetFullPath(f), Relative: Path.GetFullPath(f)[root.Length..].Replace('\\', '/').ToLowerInvariant()))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();
        if (names.Count > PackageInstaller.MaxFiles) throw new IOException("too many files");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (full, relative) in names)
        {
            if (new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("link inside the plugin folder");
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            _files[full] = stream;
            // The handle must be the file the path names (under the canonical form of the folder), not something a link or a swap led to.
            if (canonicalRoot is not null && FinalPath(stream.SafeFileHandle) is { } final
                && !SamePath(final, Path.Combine(canonicalRoot, relative.Replace('/', '\\')))) throw new IOException("the file is not where its path says");
            var name = Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            hash.AppendData(SHA256.HashData(stream));
            stream.Position = 0;
        }
        Hash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // The relative names were lower-cased for the hash; the comparison with a final path ignores case anyway.
    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ---- Windows: hold a folder, ask a handle for its real path ----

    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    private static SafeFileHandle? HoldFolder(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFileW(folder, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("a plugin folder cannot be held: " + Marshal.GetLastWin32Error());
        return handle;
    }

    /// <summary>The path a handle really belongs to, without the <c>\\?\</c> prefix; null off Windows.</summary>
    internal static string? FinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("the real path of a plugin file cannot be read");
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Is this path one of the held files (and so part of what was hashed)?</summary>
    public bool Contains(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            lock (_files) return !_disposed && _files.ContainsKey(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The number of files held.</summary>
    public int Count => _files.Count;

    public void Dispose()
    {
        lock (_files)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stream in _files.Values)
            {
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                }
            }
            _files.Clear();
            foreach (var folder in _folders) folder.Dispose();
            _folders.Clear();
        }
    }
}
