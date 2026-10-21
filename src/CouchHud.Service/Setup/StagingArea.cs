using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Privileged;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Service.Setup;

/// <summary>Whether a folder is the administrators' (tests pass a fake: a test cannot make folders it does not own).</summary>
public interface IFolderSecurity
{
    /// <summary>Owned by SYSTEM, the Administrators or TrustedInstaller, and no one else may write, delete or change the ACL.</summary>
    bool IsProtected(string directory);
}

/// <summary>The real ACLs (<see cref="InstallProtection.IsFolderProtected"/>).</summary>
public sealed class WindowsFolderSecurity : IFolderSecurity
{
    public bool IsProtected(string directory) => InstallProtection.IsFolderProtected(directory);
}

/// <summary>Why a setup was refused after its bytes arrived: the error code and the reason for the log.</summary>
public sealed class SetupRejectedException(string code, string reason) : Exception(reason)
{
    public string Code { get; } = code;
}

/// <summary>Why the data folder is not usable: the service then refuses setups (fail-closed) and raises an alarm in the event log.</summary>
public sealed class UnsafeFolderException(string reason) : IOException(reason);

/// <summary>
/// The service's data below its own program folder (security review H1/H2): <c>{app}\service\data</c> with <c>staging</c>, <c>jobs</c>
/// and <c>logs</c>. Program Files lets only administrators write, the installer creates the folder protected, and nothing is ever moved
/// aside: a folder in the chain <c>{app}\service → data → staging → &lt;guid&gt; (→ tmp)</c> that is a reparse point, not owned by
/// SYSTEM / Administrators / TrustedInstaller, or writable by anyone else fails the setup closed with an alarm. Every folder made
/// here is checked again after it was made, and the folders a setup sits in are held open without delete sharing (nobody can rename
/// or remove them) until the staged file is gone. The file name is the one from the signed checksum file.
/// </summary>
public sealed class StagingArea(string serviceDirectory, IFolderSecurity security, IEventSink log)
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _ready;
    private bool _cleaned;

    /// <summary>The folder of CouchHud.Service.exe (<c>{app}\service</c>).</summary>
    public string ServiceDirectory { get; } = Path.GetFullPath(serviceDirectory).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary><c>{app}\service\data</c> (<see cref="ServiceInstall.DataFolder"/>, created protected by the setup).</summary>
    public string DataRoot => ServiceInstall.DataFolder(ServiceDirectory);

    public string StagingRoot => Path.Combine(DataRoot, "staging");

    /// <summary>Where the setup logs of updates go (<see cref="ServiceInstall.SetupLogFolder"/>).</summary>
    public string SetupLogRoot => ServiceInstall.SetupLogFolder(ServiceDirectory);

    /// <summary>
    /// The outcome of the last <see cref="Prepare"/>, for the authorization (no file system work there). False until a Prepare succeeded.
    /// </summary>
    public bool Ready => _ready;

    /// <summary>Checks the chain (making data and staging when missing, then checking them); null when it is safe, else why not (and an alarm).</summary>
    public string? Prepare()
    {
        lock (_lock)
        {
            try
            {
                RequireSafe(ServiceDirectory);
                MakeOrCheck(DataRoot);
                MakeOrCheck(StagingRoot);
                if (!_cleaned)
                {
                    CleanLeftovers();
                    _cleaned = true;
                }
                _ready = true;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _ready = false;
                log.Error($"[alarm] the service's data folder is not safe, setups refused: {(ex is UnsafeFolderException ? ex.Message : ex.GetType().Name)}");
                return "data folder not safe";
            }
        }
    }

    /// <summary>A folder of the service right below the data folder (logs, jobs), made and checked like the staging folder; its full path.</summary>
    public string EnsureFolder(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), DataRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("not a folder right below the data folder", nameof(folder));
        if (Prepare() is { } problem) throw new UnsafeFolderException(problem);
        lock (_lock) MakeOrCheck(full);
        return full;
    }

    /// <summary>A new, empty folder with the file to write the setup into (no one else may open it while it is written).</summary>
    public StagedFile Create(string fileName)
    {
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("not a plain file name", nameof(fileName));
        }
        if (Prepare() is { } problem) throw new UnsafeFolderException(problem);
        var locks = new List<SafeFileHandle>();
        var folder = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            // Held from here on: data and staging cannot be renamed or removed while the setup is staged and started.
            locks.Add(DirectoryLock.Open(DataRoot));
            locks.Add(DirectoryLock.Open(StagingRoot));
            Directory.CreateDirectory(folder);
            RequireSafe(folder);
            locks.Add(DirectoryLock.Open(folder));
            var path = Path.Combine(folder, fileName);
            var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            lock (_lock) _active.Add(folder);
            return new StagedFile(path, folder, writer, locks, () =>
            {
                lock (_lock) _active.Remove(folder);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            foreach (var handle in locks) handle.Dispose();
            TryDelete(folder);
            log.Error($"[alarm] staging folder refused: {(ex is UnsafeFolderException ? ex.Message : ex.GetType().Name)}");
            throw ex as IOException ?? new UnsafeFolderException(ex.GetType().Name);
        }
    }

    /// <summary>
    /// A fresh, empty temp folder for the setup next to its file (its TEMP/TMP), checked after it was made and held like the others;
    /// it goes with the staged file.
    /// </summary>
    public string CreateTempFolder(StagedFile staged)
    {
        var folder = Path.Combine(staged.Folder, "tmp");
        if (Directory.Exists(folder) || File.Exists(folder)) throw new UnsafeFolderException("temp folder exists already");
        Directory.CreateDirectory(folder);
        RequireSafe(folder);
        staged.Hold(DirectoryLock.Open(folder));
        return folder;
    }

    private void MakeOrCheck(string folder)
    {
        if (!Directory.Exists(folder))
        {
            if (File.Exists(folder)) throw new UnsafeFolderException($"{folder} is a file");
            Directory.CreateDirectory(folder);
        }
        RequireSafe(folder);
    }

    /// <summary>A real folder (no reparse point anywhere up the chain) that only administrators can change.</summary>
    private void RequireSafe(string folder)
    {
        if (!Directory.Exists(folder)) throw new UnsafeFolderException($"{folder} missing");
        try
        {
            PathSafety.RequireNoReparsePoints(folder);
        }
        catch (CatalogException)
        {
            throw new UnsafeFolderException($"reparse point in {folder}");
        }
        if (!security.IsProtected(folder)) throw new UnsafeFolderException($"{folder} is not protected (owner or write access of others)");
    }

    /// <summary>What a previous run left (a setup that ran while the service was stopped by it).</summary>
    private void CleanLeftovers()
    {
        foreach (var old in Directory.EnumerateDirectories(StagingRoot))
        {
            if (!_active.Contains(old)) TryDelete(old);
        }
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            // A reparse point goes as the link only.
            var link = File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(folder, recursive: !link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (a setup still runs from it): the next start of the service takes it.
        }
    }
}

/// <summary>
/// A folder held open without FILE_SHARE_DELETE: while the handle lives, the folder cannot be renamed or deleted. The handle follows
/// links; its real path must be the expected one, so a folder that was swapped for a junction is refused.
/// </summary>
internal static class DirectoryLock
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    public static SafeFileHandle Open(string folder)
    {
        var handle = CreateFileW(folder, FileReadAttributes, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new UnsafeFolderException($"{folder} cannot be held (Win32 {error})");
        }
        try
        {
            PathSafety.RequireSamePath(folder, SetupProbe.FinalPath(handle));
            return handle;
        }
        catch (Exception ex) when (ex is CatalogException or IOException)
        {
            handle.Dispose();
            throw new UnsafeFolderException($"{folder} is not where it seems");
        }
    }
}

/// <summary>
/// One staged setup: written once (<see cref="Writer"/>), then sealed: closed, opened again for reading only with sharing for reading
/// only (nobody can write, rename or delete it while the handle is held), checked on that handle (size, SHA-256, where it really is,
/// a Windows executable), and checked again right before it is started (<see cref="Reverify"/>). The folders around it are held as
/// well. Disposing deletes it.
/// </summary>
public sealed class StagedFile : IDisposable
{
    private readonly Action _onDispose;
    private readonly List<SafeFileHandle> _locks;
    private FileStream? _writer;
    private FileStream? _held;
    private int _disposed;

    internal StagedFile(string path, string folder, FileStream writer, List<SafeFileHandle> locks, Action onDispose)
    {
        Path = path;
        Folder = folder;
        _writer = writer;
        _locks = locks;
        _onDispose = onDispose;
    }

    public string Path { get; }

    public string Folder { get; }

    public Stream Writer => _writer ?? throw new InvalidOperationException("sealed already");

    public SafeFileHandle Handle => _held?.SafeFileHandle ?? throw new InvalidOperationException("not sealed");

    /// <summary>How many folders are held open (tests).</summary>
    internal int HeldFolders
    {
        get { lock (_locks) return _locks.Count; }
    }

    internal void Hold(SafeFileHandle folder)
    {
        lock (_locks) _locks.Add(folder);
    }

    /// <summary>Throws <see cref="SetupRejectedException"/> when the file is not exactly the signed setup.</summary>
    public void Seal(long size, string sha256)
    {
        var writer = _writer ?? throw new InvalidOperationException("sealed already");
        writer.Flush(flushToDisk: true);
        writer.Dispose();
        _writer = null;
        _held = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_held.Length != size) throw new SetupRejectedException(Contracts.ErrorCodes.VerificationFailed, $"size {_held.Length}, announced {size}");
        CheckWhereAndWhat(sha256);
        if (!PortableExecutable.IsExecutable(_held)) throw new SetupRejectedException(Contracts.ErrorCodes.VerificationFailed, "not a Windows executable");
    }

    /// <summary>The same checks on the held handle once more: right before the start.</summary>
    public void Reverify(string sha256) => CheckWhereAndWhat(sha256);

    private void CheckWhereAndWhat(string sha256)
    {
        var held = _held ?? throw new InvalidOperationException("not sealed");
        try
        {
            PathSafety.RequireSamePath(Path, SetupProbe.FinalPath(held.SafeFileHandle));
            PathSafety.RequireNoReparsePoints(Path);
            ReleaseIntegrity.VerifyHash(held, sha256);
        }
        catch (CatalogException ex)
        {
            throw new SetupRejectedException(Contracts.ErrorCodes.VerificationFailed, ex.Message);
        }
        catch (IOException ex)
        {
            throw new SetupRejectedException(Contracts.ErrorCodes.VerificationFailed, ex.GetType().Name);
        }
    }

    /// <summary>The subject of an Authenticode signature, for the log only: the signed checksum file is what is trusted (as in the shell's self-update).</summary>
    public string AuthenticodeSubject()
    {
        try
        {
#pragma warning disable SYSLIB0057 // the only API that reads the Authenticode signature of a file
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(Path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return "none";
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _writer?.Dispose();
        _held?.Dispose();
        lock (_locks)
        {
            foreach (var handle in _locks) handle.Dispose();
            _locks.Clear();
        }
        StagingArea.TryDelete(Folder);
        _onDispose();
    }
}

/// <summary>Whether bytes are a Windows program at all: MZ, a PE header in range, a known machine, an executable image and no DLL.</summary>
public static class PortableExecutable
{
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;
    private const ushort ExecutableImage = 0x0002;
    private const ushort Dll = 0x2000;

    /// <summary>Reads from the start of the stream and puts it back at the start.</summary>
    public static bool IsExecutable(Stream stream)
    {
        try
        {
            var dos = new byte[0x40];
            stream.Position = 0;
            if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
            var peOffset = BitConverter.ToInt32(dos, 0x3C);
            if (peOffset < 0x40 || peOffset > 1024 * 1024 || peOffset + 24L > stream.Length) return false;
            var header = new byte[24];
            stream.Position = peOffset;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
            if (header[0] != (byte)'P' || header[1] != (byte)'E' || header[2] != 0 || header[3] != 0) return false;
            var machine = BitConverter.ToUInt16(header, 4);
            var characteristics = BitConverter.ToUInt16(header, 22);
            return machine is MachineI386 or MachineAmd64 or MachineArm64 && (characteristics & ExecutableImage) != 0 && (characteristics & Dll) == 0;
        }
        finally
        {
            stream.Position = 0;
        }
    }
}
