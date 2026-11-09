using MegaHub.Core.Config;
using MegaHub.Core.Guard;
using CouchHud.Sdk;

namespace MegaHub.Core.Library;

/// <summary>
/// A media player that Mega Hub knows how to start and to come back from. Compatibility only: nothing of it ships with Mega Hub and
/// nothing is downloaded or controlled (no remote control, no library of the player).
/// </summary>
/// <param name="ProcessNames">Image names without .exe. The app is "running" while any of them runs: a launcher that hands over to the real program (or a program that a Store app starts under another name) does not end the session early.</param>
/// <param name="PackagePrefix">Start of the package family of the Store version ("XBMCFoundation.Kodi_"), empty when there is none that is known.</param>
/// <param name="FullscreenArgs">Parameters for a start in full screen, offered when the app is added; empty where the player starts like that by itself or the flag is not known.</param>
public sealed record PlayerProfile(string Id, string Name, IReadOnlyList<string> ProcessNames, string PackagePrefix, IReadOnlyList<string> FullscreenArgs);

/// <summary>
/// Known start profiles of Kodi, Emby, Jellyfin and Plex clients. They feed two places that already exist: the running-detection of an
/// app (<see cref="GameSessionTracker"/>), and the start parameters an app is added with. Everything else is the normal app path: the foreground
/// guard releases a program Mega Hub started, the Guide button always leads back, and a profile never changes what the lock or the parental rules allow.
/// </summary>
public static class PlayerProfiles
{
    public static readonly IReadOnlyList<PlayerProfile> All =
    [
        // Kodi documents "-fs" (start in full screen). The Store version is the package family XBMCFoundation.Kodi.
        new("kodi", "Kodi", ["kodi"], "XBMCFoundation.Kodi_", ["-fs"]),
        // Emby Theater and the Plex clients open in full screen on their own.
        new("emby", "Emby Theater", ["EmbyTheater", "Emby Theater"], "", []),
        // Jellyfin's desktop client has been named Jellyfin Media Player and Jellyfin Desktop; "--fullscreen" starts it in full screen.
        new("jellyfin", "Jellyfin", ["JellyfinMediaPlayer", "jellyfin-desktop", "Jellyfin Desktop"], "", ["--fullscreen"]),
        // Plex Desktop starts Plex HTPC under its own name; both count as the same player.
        new("plex", "Plex", ["Plex HTPC", "PlexHTPC", "Plex"], "", [])
    ];

    /// <summary>The profile of a program path or a Store app id ("shell:AppsFolder\…"); null for anything else.</summary>
    public static PlayerProfile? Find(LaunchTarget? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Target)) return null;
        if (target.Kind == LaunchKind.Url)
        {
            var id = InstalledApps.IdOf(target.Target);
            return id is null || !InstalledApps.IsPackagedId(id)
                ? null
                : All.FirstOrDefault(p => p.PackagePrefix.Length > 0 && id.StartsWith(p.PackagePrefix, StringComparison.OrdinalIgnoreCase));
        }
        var stem = StemOf(target.Target);
        return stem is null ? null : All.FirstOrDefault(p => p.ProcessNames.Contains(stem, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The profile of a library app. Only an app: a game that happens to carry such a name is not a player.</summary>
    public static PlayerProfile? ForItem(LibraryItem item) => item.Type == ItemType.App ? Find(item.Launch) : null;

    /// <summary>
    /// Is this running process one of the player's, for this entry? Its name is one of the profile's and its program lies where the entry's
    /// program does: the entry's own program file, its install folder, or the folder of the entry's Store package (the package family). A
    /// program that only carries the name, somewhere else, is not the player.
    /// </summary>
    public static bool IsProcessOf(PlayerProfile profile, LibraryItem item, string? imagePath, string? windowsAppsRoot = null)
    {
        if (imagePath is null || StemOf(imagePath) is not { } stem || !profile.ProcessNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        if (item.Launch.Kind == LaunchKind.Exe)
        {
            // Both sides normalised (case, slashes, "." and ".." parts) before they are compared.
            var image = Normal(imagePath);
            var own = Normal(item.Launch.Target?.Trim().Trim('"'));
            if (image is null) return false;
            if (own is not null && string.Equals(own, image, StringComparison.OrdinalIgnoreCase)) return true;
            return GameSessionTracker.InstallRoot(item) is { } root && Normal(root) is { } normalRoot
                && image.StartsWith(Path.TrimEndingDirectorySeparator(normalRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        return InPackageFolder(InstalledApps.IdOf(item.Launch.Target), imagePath, windowsAppsRoot);
    }

    /// <summary>
    /// A Store entry of a known player can be watched by its package (the process runs from the package's folder under WindowsApps); every
    /// other entry needs an install folder (<see cref="GameSessionTracker.CanDetect"/>), not just a name that looks like a player.
    /// </summary>
    public static bool TrackableByPackage(LibraryItem item) =>
        item.Type == ItemType.App && item.Launch.Kind == LaunchKind.Url && Find(item.Launch) is { PackagePrefix.Length: > 0 }
        && PackageParts(InstalledApps.IdOf(item.Launch.Target)) is not null;

    private static (string Name, string Publisher)? PackageParts(string? appUserModelId)
    {
        var family = appUserModelId?.Split('!')[0];
        var cut = family?.LastIndexOf('_') ?? -1;
        if (family is null || cut <= 0 || cut >= family.Length - 1) return null;
        return (family[..cut], family[(cut + 1)..]);
    }

    private static string? Normal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The real package folder of the Store apps: %ProgramFiles%\WindowsApps, never a folder of that name somewhere else.</summary>
    public static string DefaultWindowsAppsRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    /// <summary>"%ProgramFiles%\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe" belongs to the family "XBMCFoundation.Kodi_4n2hpmxwrvr6p".</summary>
    private static bool InPackageFolder(string? appUserModelId, string? imagePath, string? windowsAppsRoot)
    {
        if (PackageParts(appUserModelId) is not { } parts || Normal(imagePath) is not { } image) return false;
        var root = Normal(windowsAppsRoot ?? DefaultWindowsAppsRoot());
        if (root is null) return false;
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var folder = image[prefix.Length..].Split(Path.DirectorySeparatorChar)[0];
        return folder.StartsWith(parts.Name + "_", StringComparison.OrdinalIgnoreCase) && folder.EndsWith("__" + parts.Publisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A new app entry of a known player starts with the player's full-screen parameters, when it has none of its own (a Store app has no
    /// command line). A suggestion at the moment of adding, changed or removed later in the entry's startup options.
    /// </summary>
    public static void ApplyDefaults(CustomEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Url) || string.IsNullOrWhiteSpace(entry.Path) || entry.Args.Count > 0) return;
        if (!string.Equals(entry.Type, "app", StringComparison.OrdinalIgnoreCase)) return;
        if (Find(new LaunchTarget(LaunchKind.Exe, entry.Path)) is { FullscreenArgs.Count: > 0 } profile) entry.Args = [.. profile.FullscreenArgs];
    }

    private static string? StemOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? null : name;
    }
}
