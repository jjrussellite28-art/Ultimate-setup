using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>Entries from config.json "apps": an executable or a URL.</summary>
public static partial class CustomEntries
{
    public static IReadOnlyList<LibraryItem> From(IEnumerable<CustomEntry> entries)
    {
        var items = new List<LibraryItem>();
        var usedIds = new HashSet<string>();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || (string.IsNullOrEmpty(e.Url) && string.IsNullOrEmpty(e.Path))) continue;

            var launch = !string.IsNullOrEmpty(e.Url)
                ? new LaunchTarget(LaunchKind.Url, e.Url) { Embedded = WebLinks.IsWebUrl(e.Url) && !WebLinks.UsesEdge(e) }
                : new LaunchTarget(LaunchKind.Exe, e.Path!)
                {
                    Args = e.Args,
                    WorkingDirectory = string.IsNullOrWhiteSpace(e.Cwd) ? Path.GetDirectoryName(e.Path) : e.Cwd
                };

            var id = $"custom:{Slug(e.Name)}";
            // Same name twice ("Kodi", "kodi") would otherwise collide and one entry vanish.
            for (var n = 2; !usedIds.Add(id); n++) id = $"custom:{Slug(e.Name)}-{n}";

            items.Add(new LibraryItem
            {
                Id = id,
                Source = "custom",
                Name = e.Name,
                Type = string.Equals(e.Type, "game", StringComparison.OrdinalIgnoreCase) ? ItemType.Game : ItemType.App,
                Launch = launch,
                // The program's own folder, never the working folder: that is where it runs, not where it lives (and not what it is looked up by).
                InstallDir = launch.Kind == LaunchKind.Exe ? InstallFolder(e.Path!) : null,
                Art = new Artwork
                {
                    Cover = string.IsNullOrEmpty(e.Image) ? [] : [ToUrl(e.Image)],
                    Hero = string.IsNullOrEmpty(e.Hero) ? [] : [ToUrl(e.Hero)]
                }
            });
        }
        return items;
    }

    /// <summary>The folder of the program, except the folder of the app aliases (wt.exe, python.exe …): every Store app's stub lives there.</summary>
    internal static string? InstallFolder(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return null;
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        return string.Equals(Path.TrimEndingDirectorySeparator(dir), aliases, StringComparison.OrdinalIgnoreCase) ? null : dir;
    }

    private static string Slug(string name)
    {
        var slug = NonAlphaNumeric().Replace(name.ToLowerInvariant(), "-").Trim('-');
        // Names without latin letters or digits (e.g. Japanese titles) still need a stable, unique id.
        return slug.Length > 0
            ? slug
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
    }

    internal static string ToUrl(string pathOrUrl) =>
        UrlScheme().IsMatch(pathOrUrl) ? pathOrUrl : new Uri(Path.GetFullPath(pathOrUrl)).AbsoluteUri;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("^[a-z][a-z0-9+.-]+://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlScheme();
}
