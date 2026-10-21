using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>A program from the Windows apps folder (shell:AppsFolder), as the Start menu lists it.</summary>
/// <param name="Id">What the shell calls it: an AppUserModelID of a Store app ("Contoso.Notes_8wekyb3d8bbwe!App"), a path or "{KnownFolderGuid}\sub\app.exe".</param>
/// <param name="Known">Already in the library.</param>
public sealed record InstalledApp(string Name, string Id)
{
    public bool Known { get; init; }

    /// <summary>The shell sees it as a packaged (Store/MSIX) app.</summary>
    public bool IsPackaged => InstalledApps.IsPackagedId(Id);
}

/// <summary>
/// The list "Add an app" offers: the apps folder cleaned up (no duplicates, no uninstallers and help links), sorted, with
/// the entries marked that the library already has, and the step from a pick to a library entry.
/// </summary>
public static partial class InstalledApps
{
    public const string FolderPrefix = @"shell:AppsFolder\";

    /// <summary>"shell:AppsFolder\Id" - what ShellExecute and the activation manager start.</summary>
    public static string UrlOf(string id) => FolderPrefix + id;

    /// <summary>The id behind a "shell:AppsFolder\Id" launch target; null for everything else.</summary>
    public static string? IdOf(string? target) =>
        target is not null && target.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase) && target.Length > FolderPrefix.Length ? target[FolderPrefix.Length..] : null;

    /// <summary>"PackageFamily!App": no path separators, no braces. Only these go through the activation manager.</summary>
    public static bool IsPackagedId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Contains('!') && id.IndexOfAny(['\\', '/', ':', '{', '}', '"']) < 0 && !id.Any(char.IsControl);

    [GeneratedRegex(@"(^|\s)(uninstall|deinstallier\w*|deinstallation|remove)(\s|$)|^(readme|read me|help|hilfe|manual|handbuch|website|webseite|release notes|license)(\s|$)|\b(documentation|dokumentation)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseName();

    /// <summary>Drops empty and unusable rows, uninstallers and help links, CouchHUD itself and duplicates; sorted A to Z.</summary>
    public static IReadOnlyList<InstalledApp> Clean(IEnumerable<InstalledApp> raw, IComparer<string>? comparer = null)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<InstalledApp>();
        // Store apps first: when a program shows up twice (Store and classic), the packaged one is what the Start menu starts.
        foreach (var app in raw.OrderByDescending(a => IsPackagedId(a.Id)))
        {
            var name = app.Name?.Trim() ?? "";
            var id = app.Id?.Trim() ?? "";
            if (name.Length == 0 || id.Length == 0 || id.Any(char.IsControl) || name.Any(char.IsControl)) continue;
            if (IsDocument(id)) continue;
            if (NoiseName().IsMatch(name) || name.StartsWith("CouchHUD", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seenIds.Add(id) || !seenNames.Add(name)) continue;
            list.Add(app with { Name = name, Id = id });
        }
        var order = comparer ?? StringComparer.CurrentCultureIgnoreCase;
        return list.OrderBy(a => a.Name, order).ToList();
    }

    /// <summary>A path that is not a program: manuals (.chm), text files, web links the installers put in the Start menu.</summary>
    internal static bool IsDocument(string id)
    {
        if (id.IndexOfAny(['\\', '/']) < 0 || IsPackagedId(id)) return false;
        var ext = Path.GetExtension(id);
        return !(ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Marks the apps that already are in the library (same program or same Store app).</summary>
    public static IReadOnlyList<InstalledApp> MarkKnown(IEnumerable<InstalledApp> apps, IEnumerable<LibraryItem> library, Func<string, string?> resolveKnownFolder, Func<string, bool> fileExists)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in library) targets.Add(item.Launch.Target);
        return apps.Select(a =>
        {
            var entry = ToEntry(a, "app", resolveKnownFolder, fileExists);
            var key = entry.Path ?? entry.Url ?? "";
            return a with { Known = targets.Contains(key) || targets.Contains(UrlOf(a.Id)) };
        }).ToList();
    }

    /// <summary>
    /// A library entry for the pick. Programs with a real file become an exe entry (so the install folder, icon and
    /// running state work); Store apps and everything the shell cannot resolve start through shell:AppsFolder.
    /// </summary>
    public static CustomEntry ToEntry(InstalledApp app, string type, Func<string, string?> resolveKnownFolder, Func<string, bool> fileExists)
    {
        var entry = new CustomEntry { Name = app.Name, Type = type };
        var id = app.Id;
        if (!IsPackagedId(id))
        {
            var path = ResolvePath(id, resolveKnownFolder);
            if (path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && fileExists(path))
            {
                entry.Path = path;
                return entry;
            }
        }
        entry.Url = UrlOf(id);
        return entry;
    }

    [GeneratedRegex(@"^\{(?<guid>[0-9A-Fa-f-]{36})\}[\\/](?<rest>.+)$")]
    private static partial Regex KnownFolderId();

    /// <summary>A rooted path, or "{KnownFolderGuid}\rest" turned into one; null for anything else.</summary>
    internal static string? ResolvePath(string id, Func<string, string?> resolveKnownFolder)
    {
        try
        {
            if (Path.IsPathRooted(id) && !id.StartsWith(@"\\", StringComparison.Ordinal)) return Path.GetFullPath(id);
            if (KnownFolderId().Match(id) is { Success: true } m && resolveKnownFolder(m.Groups["guid"].Value) is { Length: > 0 } root)
                return Path.GetFullPath(Path.Combine(root, m.Groups["rest"].Value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return null;
    }
}
