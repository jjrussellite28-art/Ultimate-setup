using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

public sealed class SteamScanner(IRegistry registry, string? steamPath = null) : ILibraryProvider
{
    // Things Steam installs that are not games.
    private static readonly HashSet<string> IgnoredAppIds =
    [
        "228980", // Steamworks Common Redistributables
        "1070560", "1391110", "1628350", // Steam Linux Runtime
        "1493710", "1826330", "2180100", "2805730", "3658110" // Proton
    ];

    private static readonly string[] IgnoredNameParts = ["redistributable", "steamvr", "proton"];

    public const string Cdn = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";

    public string Name => "Steam";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private IReadOnlyList<LibraryItem> Scan(CancellationToken ct)
    {
        var root = steamPath ?? FindSteamPath();
        if (root is null) return [];

        var items = new List<LibraryItem>();
        foreach (var library in ReadLibraryFolders(root))
        {
            ct.ThrowIfCancellationRequested();
            var appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var file in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var m = ParseManifest(File.ReadAllText(file));
                    if (m is null || IgnoredAppIds.Contains(m.Value.AppId)) continue;
                    if (IgnoredNameParts.Any(p => m.Value.Name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                    items.Add(new LibraryItem
                    {
                        Id = $"steam:{m.Value.AppId}",
                        Source = "steam",
                        Name = m.Value.Name,
                        LastPlayed = m.Value.LastPlayed,
                        InstallDir = string.IsNullOrEmpty(m.Value.InstallDir) ? null : Path.Combine(appsDir, "common", m.Value.InstallDir),
                        SizeOnDisk = m.Value.SizeOnDisk,
                        Launch = new LaunchTarget(LaunchKind.Url, $"steam://rungameid/{m.Value.AppId}"),
                        Art = ArtworkFor(root, m.Value.AppId)
                    });
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[steam] {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return items;
    }

    /// <summary>Games Steam is downloading, updating or has queued, with progress.</summary>
    public IReadOnlyList<SteamDownload> Downloads()
    {
        var root = steamPath ?? FindSteamPath();
        if (root is null) return [];
        var list = new List<SteamDownload>();
        foreach (var library in ReadLibraryFolders(root))
        {
            var appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir)) continue;
            foreach (var file in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    if (ParseDownload(File.ReadAllText(file)) is { } d && !IgnoredAppIds.Contains(d.AppId)) list.Add(d);
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[steam] {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return list.OrderBy(d => d.State == SteamDownloadState.Downloading ? 0 : d.State == SteamDownloadState.Paused ? 1 : 2).ThenBy(d => d.Name).ToList();
    }

    // AppState.StateFlags bits as Steam writes them.
    private const int UpdateRequired = 2, FullyInstalled = 4, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024, Downloading = 1048576, Staging = 2097152, Committing = 4194304;

    /// <summary>A manifest as a download entry, or null when the game is simply installed.</summary>
    internal static SteamDownload? ParseDownload(string text)
    {
        var state = Vdf.Parse(text).GetObject("AppState");
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        if (state is null || string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;
        if (!int.TryParse(state.GetString("StateFlags"), out var flags)) return null;
        long Num(string key) => long.TryParse(state.GetString(key), out var v) ? v : 0;
        var toDownload = Num("BytesToDownload");
        var downloaded = Num("BytesDownloaded");

        var busy = (flags & (UpdateRunning | Downloading | Staging | Committing)) != 0;
        var paused = (flags & UpdatePaused) != 0;
        var needsUpdate = (flags & UpdateRequired) != 0 || (flags & UpdateStarted) != 0 || (flags & FullyInstalled) == 0;
        if (!busy && !paused && !needsUpdate) return null;

        var stateKind = busy ? SteamDownloadState.Downloading : paused ? SteamDownloadState.Paused : SteamDownloadState.Queued;
        var progress = toDownload > 0 ? Math.Clamp((double)downloaded / toDownload, 0, 1) : 0;
        return new SteamDownload(appId, name, stateKind, progress, downloaded, toDownload, (flags & FullyInstalled) != 0);
    }

    private string? FindSteamPath()
    {
        var candidates = new[]
        {
            registry.ReadString(@"HKCU\Software\Valve\Steam", "SteamPath"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };
        return candidates
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p!))
            .FirstOrDefault(p => Directory.Exists(Path.Combine(p, "steamapps")));
    }

    internal static IReadOnlyList<string> ReadLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };
        var file = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) return folders;

        try
        {
            var root = Vdf.Parse(File.ReadAllText(file)).GetObject("libraryfolders");
            if (root is null) return folders;
            foreach (var (key, entry) in root)
            {
                if (!key.All(char.IsDigit)) continue; // skip "contentstatsid" etc.
                // New format: "0" { "path" "..." }, old format: "1" "D:\SteamLibrary"
                var path = entry is VdfObject o ? o.GetString("path") : entry as string;
                if (string.IsNullOrEmpty(path)) continue;
                var full = Path.GetFullPath(path);
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
            }
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"[steam] libraryfolders.vdf: {ex.Message}");
        }
        return folders;
    }

    internal static (string AppId, string Name, long LastPlayed, string? InstallDir, long SizeOnDisk)? ParseManifest(string text)
    {
        var state = Vdf.Parse(text).GetObject("AppState");
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        if (state is null || string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;

        // StateFlags bit 4 = fully installed; skip anything mid-download or uninstalling.
        if (long.TryParse(state.GetString("StateFlags"), out var flags) && flags != 0 && (flags & 4) == 0) return null;

        long.TryParse(state.GetString("LastPlayed"), out var lastPlayedSeconds);
        long.TryParse(state.GetString("SizeOnDisk"), out var size);
        return (appId, name, lastPlayedSeconds * 1000, state.GetString("installdir"), size);
    }

    /// <summary>Local library cache first (works offline), CDN as fallback.</summary>
    private static Artwork ArtworkFor(string steamPath, string appId)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        IReadOnlyList<string> Pick(string file)
        {
            var local = new[] { Path.Combine(cache, $"{appId}_{file}"), Path.Combine(cache, appId, file) }
                .Where(File.Exists)
                .Select(p => new Uri(p).AbsoluteUri);
            return [.. local, $"{Cdn}/{appId}/{file}"];
        }
        return new Artwork
        {
            Cover = Pick("library_600x900.jpg"),
            Hero = Pick("library_hero.jpg"),
            Logo = Pick("logo.png")
        };
    }
}
