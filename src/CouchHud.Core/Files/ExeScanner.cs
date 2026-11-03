using System.Diagnostics;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Files;

/// <param name="LegacyPaths">Every exe of the folder, helpers included: the older scan picked one of them, and its entry id came from that path (see FolderIdMigration).</param>
public sealed record ExeCandidate(string Name, string Path, long Size, IReadOnlyList<string>? LegacyPaths = null);

/// <summary>
/// Finds the likely game executables below a folder like D:\Games: one per
/// subfolder, skipping installers, uninstallers, crash reporters and runtimes.
/// </summary>
public static class ExeScanner
{
    private const int MaxDepth = 4;

    public static IReadOnlyList<ExeCandidate> Scan(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(Strings.Get("files.folderNotFound", root));

        // A watched Steam library (X:\SteamLibrary\steamapps\common): its games belong to the Steam scanner.
        if (InSteamApps(root)) return [];
        var rootDir = new DirectoryInfo(root);
        var subfolders = rootDir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        // A folder with exes directly inside is itself a game folder (e.g. the user picked "D:\Games\Hades").
        List<DirectoryInfo> groups = rootDir.EnumerateFiles("*.exe").Any(f => !IsJunk(f.Name)) ? new() { rootDir } : subfolders;

        var result = new List<ExeCandidate>();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            // A Steam library or client folder: its games belong to the Steam scanner (and are not walked through here).
            if (Directory.Exists(Path.Combine(group.FullName, "steamapps")) || InSteamApps(group.FullName)) continue;
            var files = Executables(group, ct).ToList();
            // The version resource is read only for exes whose name already passes (helpers are skipped without opening them), and for
            // at most MaxVersionReads of them (the shallower first): a folder with thousands of exes must not stall the scan.
            var reads = 0;
            var facts = files.OrderBy(f => Path.GetRelativePath(group.FullName, f.FullName).Count(c => c is '\\' or '/'))
                .Select(f => Facts(group, f, ref reads)).ToList();
            if (GameDetection.Pick(group.Name, facts) is not { } pick) continue;
            var full = Path.Combine(group.FullName, pick.Exe.RelativePath);
            var size = files.FirstOrDefault(f => string.Equals(f.FullName, full, StringComparison.OrdinalIgnoreCase))?.Length ?? pick.Exe.Size;
            result.Add(new ExeCandidate(pick.Name, full, size, files.Select(f => f.FullName).ToList()));
        }
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>At most this many version resources are read per game folder.</summary>
    internal const int MaxVersionReads = 64;

    /// <summary>Whether a folder path has a "steamapps" segment.</summary>
    internal static bool InSteamApps(string path) =>
        path.Split('\\', '/').Any(seg => seg.Equals("steamapps", StringComparison.OrdinalIgnoreCase));

    private static ExeFacts Facts(DirectoryInfo group, FileInfo file, ref int reads)
    {
        var relative = Path.GetRelativePath(group.FullName, file.FullName).Replace('/', '\\');
        var bare = new ExeFacts(relative, file.Length);
        if (GameDetection.IsJunk(bare) || reads >= MaxVersionReads) return bare;
        reads++;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file.FullName);
            return bare with { ProductName = info.ProductName?.Trim(), FileDescription = info.FileDescription?.Trim() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return bare;
        }
    }

    private static IEnumerable<FileInfo> Executables(DirectoryInfo dir, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in dir.EnumerateFiles("*.exe", options))
        {
            if (ct.IsCancellationRequested) yield break;
            yield return file;
        }
    }

    internal static bool IsJunk(string fileName) => GameDetection.IsJunkName(fileName);
}
