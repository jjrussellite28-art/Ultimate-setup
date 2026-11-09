using System.Security.Cryptography;
using System.Text;
using MegaHub.Core.Files;
using MegaHub.Core.Platform;
using CouchHud.Sdk;

namespace MegaHub.Core.Library;

/// <summary>GOG games, installed with or without Galaxy (the installers register them).</summary>
public sealed class GogScanner(IRegistry registry) : ILibraryProvider, IGameStore
{
    private const string GamesKey = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";

    public string Name => "GOG";
    public string Source => "gog";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.Run(Scan, cancellationToken);

    internal IReadOnlyList<LibraryItem> Scan()
    {
        var items = new List<LibraryItem>();
        foreach (var id in registry.SubKeys(GamesKey))
        {
            var v = registry.ReadValues($@"{GamesKey}\{id}");
            string? Get(string name) => v.TryGetValue(name, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

            // DLCs point at their game and have no exe of their own.
            if (Get("dependsOn") is not null) continue;
            var name = Get("gameName") ?? Get("GAMENAME");
            var exe = Get("exe") ?? Get("EXE");
            if (name is null || exe is null) continue;
            var dir = Get("path") ?? Get("PATH") ?? WinPath.DirectoryName(exe);

            items.Add(new LibraryItem
            {
                Id = $"gog:{id}",
                Source = "gog",
                Name = name,
                InstallDir = dir,
                Launch = new LaunchTarget(LaunchKind.Exe, exe)
                {
                    Args = CommandLine.SplitArguments(Get("launchParam") ?? ""),
                    WorkingDirectory = Get("workingDir") ?? dir
                }
            });
        }
        return items;
    }
}

/// <summary>Ubisoft Connect games; the launcher keeps one registry key per installed game.</summary>
public sealed class UbisoftScanner(IRegistry registry) : ILibraryProvider, IGameStore
{
    private const string InstallsKey = @"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";

    public string Name => "Ubisoft Connect";
    public string Source => "ubisoft";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.Run(Scan, cancellationToken);

    internal IReadOnlyList<LibraryItem> Scan()
    {
        var items = new List<LibraryItem>();
        var launcherDir = registry.ReadString(@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir")?.Trim();
        foreach (var id in registry.SubKeys(InstallsKey))
        {
            var dir = registry.ReadString($@"{InstallsKey}\{id}", "InstallDir")?.Trim();
            if (string.IsNullOrEmpty(dir)) continue;
            var name = WinPath.FileName(dir.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name)) continue;
            items.Add(new LibraryItem
            {
                Art = LauncherArtwork.FromUbisoftCache(launcherDir, id),
                Id = $"ubisoft:{id}",
                Source = "ubisoft",
                Name = name,
                InstallDir = dir.Replace('/', '\\'),
                Launch = new LaunchTarget(LaunchKind.Url, $"uplay://launch/{id}/0")
            });
        }
        return items;
    }
}

/// <summary>
/// Folders the user wants watched (e.g. D:\Games): every subfolder with a game exe
/// becomes an entry, also games installed later. Wrong guesses can be hidden.
/// </summary>
public sealed class FolderGamesProvider(Func<IReadOnlyList<string>> folders) : ILibraryProvider, IGameStore
{
    public string Name => "Spieleordner";
    public string Source => "folder";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    /// <summary>Raised after a scan with the entries whose start exe differs from what an older version picked (see <see cref="FolderIdMigration"/>).</summary>
    public event Action<IReadOnlyList<FolderIdMove>>? IdsMoved;

    private IReadOnlyList<LibraryItem> Scan(CancellationToken ct)
    {
        var items = new List<LibraryItem>();
        var moves = new List<FolderIdMove>();
        foreach (var folder in folders())
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var found = ExeScanner.Scan(folder, ct);
                foreach (var c in found) items.Add(ToItem(c));
                moves.AddRange(FolderIdMigration.MovesFor(found, IdOf));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[folders] {folder}: {ex.Message}");
            }
        }
        if (moves.Count > 0 && !ct.IsCancellationRequested)
        {
            try { IdsMoved?.Invoke(moves); }
            catch (Exception ex) { Console.Error.WriteLine($"[folders] could not move saved settings: {ex.Message}"); }
        }
        return items;
    }

#pragma warning disable CA5350 // an id for the entry (it has to stay the same across versions), not a security use
    public static string IdOf(string exePath) =>
        "folder:" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..12].ToLowerInvariant();
#pragma warning restore CA5350

    /// <summary>The id comes from the exe path, so it survives rescans and renames by the user.</summary>
    public static LibraryItem ToItem(ExeCandidate c) => new()
    {
        Id = IdOf(c.Path),
        Source = "folder",
        Name = c.Name,
        InstallDir = WinPath.DirectoryName(c.Path),
        Launch = new LaunchTarget(LaunchKind.Exe, c.Path) { WorkingDirectory = WinPath.DirectoryName(c.Path) }
    };
}

/// <summary>Finds the usual places games end up when no launcher manages them.</summary>
public static class GameFolderDiscovery
{
    private static readonly string[] Patterns =
    [
        "Games", "Spiele", "GOG Games", "Gaming", "Game", "PC Games",
        // GOG and Ubisoft folders are left out: their scanners already know those games.
        @"Program Files\EA Games", @"Program Files (x86)\Origin Games", @"Program Files\Rockstar Games",
        "Amazon Games\\Library", "Riot Games",
        @"Program Files (x86)\Battle.net", "itch\\apps", "Emulation", "Emulators", "ROMs"
    ];

    /// <summary>Existing candidate folders on the given drive roots that have at least one subfolder.</summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<string> driveRoots, IReadOnlyCollection<string> alreadyWatched)
    {
        var found = new List<string>();
        foreach (var root in driveRoots)
        {
            foreach (var pattern in Patterns)
            {
                var path = Path.Combine(root, pattern);
                try
                {
                    if (!Directory.Exists(path) || !Directory.EnumerateDirectories(path).Any()) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (alreadyWatched.Any(w => string.Equals(w.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(path);
            }
        }
        return found;
    }
}
