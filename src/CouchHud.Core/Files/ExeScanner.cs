using System.Diagnostics;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Files;

public sealed record ExeCandidate(string Name, string Path, long Size);

/// <summary>
/// Finds the likely game executables below a folder like D:\Games: one per
/// subfolder, skipping installers, uninstallers, crash reporters and runtimes.
/// </summary>
public static partial class ExeScanner
{
    private const int MaxDepth = 4;

    public static IReadOnlyList<ExeCandidate> Scan(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(Strings.Get("files.folderNotFound", root));

        var rootDir = new DirectoryInfo(root);
        var subfolders = rootDir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        // A folder with exes directly inside is itself a game folder (e.g. the user picked "D:\Games\Hades").
        List<DirectoryInfo> groups = rootDir.EnumerateFiles("*.exe").Any(f => !IsJunk(f.Name)) ? new() { rootDir } : subfolders;

        var result = new List<ExeCandidate>();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var best = Executables(group, ct)
                .Where(f => !IsJunk(f.Name))
                // Prefer exes named like the folder, then the biggest one: game binaries dwarf helpers.
                .OrderByDescending(f => Similar(f.Name, group.Name))
                .ThenBy(f => Depth(group, f))
                .ThenByDescending(f => f.Length)
                .FirstOrDefault();
            if (best is not null) result.Add(new ExeCandidate(SuggestName(best, group), best.FullName, best.Length));
        }
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
            if (JunkFolders().IsMatch(file.DirectoryName ?? "")) continue;
            yield return file;
        }
    }

    internal static bool IsJunk(string fileName) => JunkExe().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    private static int Similar(string fileName, string folderName)
    {
        var a = Normalize(Path.GetFileNameWithoutExtension(fileName));
        var b = Normalize(folderName);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a)) ? 1 : 0;
    }

    private static int Depth(DirectoryInfo root, FileInfo file) =>
        Path.GetRelativePath(root.FullName, file.DirectoryName ?? root.FullName).Count(c => c is '\\' or '/');

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Product name from the exe's version info, otherwise a cleaned-up folder name.</summary>
    internal static string SuggestName(FileInfo exe, DirectoryInfo folder)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(exe.FullName).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product) && !GenericProduct().IsMatch(product)) return product;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var name = Separators().Replace(folder.Name, " ").Trim();
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe.Name);
    }

    [GeneratedRegex(@"unins|uninstall|setup|install|redist|vcredist|vc_redist|directx|dxsetup|dxwebsetup|dotnet|ndp\d|crash|report|bugsplat|updater|update|patcher|helper|prereq|physx|oalinst|easyanticheat|battleye|eac_|cefsharp|unitycrashhandler|notification_helper|quicksfv|config|settings|benchmark|server$|dedicated|editor|sdk|tool",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkExe();

    [GeneratedRegex(@"[\\/](_?commonredist|redist|redistributables?|directx|support|installers?|__installer|prereqs?|_CommonRedist|EasyAntiCheat|BattlEye|Engine[\\/]Binaries[\\/]ThirdParty)([\\/]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkFolders();

    [GeneratedRegex(@"^(unity|unreal|microsoft|windows|game|launcher|application|app)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericProduct();

    [GeneratedRegex(@"[_.\-]+")]
    private static partial Regex Separators();
}
