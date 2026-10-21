using System.Collections.Concurrent;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Saves;

/// <summary>What the detail page shows about a game's saves; <see cref="Reason"/> says why there are none.</summary>
/// <param name="Unavailable">The game database could not be loaded (offline), so "no saves" is not known.</param>
public sealed record SaveInfoDto(
    bool Supported,
    bool Unavailable,
    string? Reason,
    string? GameName,
    IReadOnlyList<string> Locations,
    int FileCount,
    long TotalBytes,
    long LastWrite,
    IReadOnlyList<string> Registry,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<BackupDto> Backups,
    string BackupDir);

public sealed record BackupDto(string Id, long Created, long SizeBytes, int FileCount, bool Safety, bool Complete);

public sealed record SaveSettingsDto(string BackupDir, string DefaultBackupDir, bool Custom, int Keep, bool IncludeConfig);

/// <summary>Ties manifest, locator and backups to the library; this is what the UI bridge talks to.</summary>
public sealed class SaveGameService(
    ConfigStore config,
    Func<string, LibraryItem?> findItem,
    Func<string, int> countWithName,
    ManifestStore manifest,
    string dataDir,
    Func<string?> steamRoot,
    Func<LibraryItem, bool> isRunning)
{
    /// <summary>How long the (possibly slow) folder scan is reused when the page is opened again.</summary>
    public static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, SaveScan Scan)> _scans = new();

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public string DefaultBackupDir => Path.Combine(dataDir, "Backups");

    public string BackupDir => config.Current.BackupDir is { Length: > 0 } custom && Path.IsPathRooted(custom) ? custom : DefaultBackupDir;

    private SaveBackupService Backups() => new(BackupDir, Math.Clamp(config.Current.BackupsToKeep, 1, 50));

    private SaveBackupService BackupsFor(LibraryItem item)
    {
        var service = Backups();
        if (countWithName(item.Name) == 1) service.MigrateLegacy(Ref(item));
        return service;
    }

    private static GameRef Ref(LibraryItem item) => new(item.Name, item.Id);

    public SaveSettingsDto GetSettings() => new(BackupDir, DefaultBackupDir, BackupDir != DefaultBackupDir,
        Math.Clamp(config.Current.BackupsToKeep, 1, 50), config.Current.BackupConfigFiles);

    public void SetBackupDir(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException(Strings.Get("saves.pickLocal"));
            Directory.CreateDirectory(path);
        }
        config.Update(c => c.BackupDir = path?.Trim() ?? "");
    }

    public void SetKeep(int keep) => config.Update(c => c.BackupsToKeep = Math.Clamp(keep, 1, 50));

    public void SetIncludeConfig(bool on)
    {
        config.Update(c => c.BackupConfigFiles = on);
        _scans.Clear();
    }

    internal SaveContext ContextFor(LibraryItem item)
    {
        var root = steamRoot();
        var steamId = SteamStore.AppIdOf(item);
        return SaveContext.ForCurrentUser(root) with
        {
            InstallDir = item.InstallDir,
            Store = item.Source switch
            {
                "steam" => "steam",
                "epic" => "epic",
                "gog" => "gog",
                "ubisoft" => "uplay",
                "xbox" => "microsoft",
                _ => null
            },
            StoreGameId = steamId,
            IncludeConfig = config.Current.BackupConfigFiles
        };
    }

    private async Task<(LibraryItem Item, ManifestEntry? Entry, string? Reason, bool Unavailable)> LookupAsync(string id)
    {
        var item = findItem(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (item.Type == ItemType.App) return (item, null, Strings.Get("saves.noAppSaves"), false);
        var index = await manifest.GetAsync();
        if (index is null) return (item, null, Strings.Get("saves.dbNotLoaded"), true);
        long? steam = long.TryParse(SteamStore.AppIdOf(item), out var n) ? n : null;
        long? gog = item.Source == "gog" && item.Id.StartsWith("gog:", StringComparison.Ordinal) && long.TryParse(item.Id["gog:".Length..], out var g) ? g : null;
        var entry = index.Find(steam, item.Name, gog);
        return (item, entry, entry is null ? Strings.Get("saves.noLocations") : null, false);
    }

    public async Task<SaveInfoDto> GetInfoAsync(string id)
    {
        var (item, entry, reason, unavailable) = await LookupAsync(id);
        var backups = BackupsFor(item);
        var list = ToDto(backups.List(Ref(item)));
        if (entry is null) return new SaveInfoDto(false, unavailable, reason, null, [], 0, 0, 0, [], [], list, BackupDir);

        var scan = await ScanCachedAsync(item, entry);
        var last = scan.LastWriteUtc;
        string? note = null;
        if (scan.Patterns.Count == 0 && scan.RegistryKeys.Count > 0) note = Strings.Get("saves.registryOnly");
        else if (scan.Patterns.Count == 0) note = Strings.Get("saves.noLocationWindows");
        return new SaveInfoDto(scan.Patterns.Count > 0, false, note, entry.Name, scan.Locations, scan.Files.Count, scan.TotalBytes,
            last is null ? 0 : new DateTimeOffset(last.Value, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            scan.RegistryKeys, scan.Errors, list, BackupDir);
    }

    private async Task<SaveScan> ScanCachedAsync(LibraryItem item, ManifestEntry entry)
    {
        if (_scans.TryGetValue(item.Id, out var cached) && Now() - cached.At < ScanCacheTtl) return cached.Scan;
        var scan = await Task.Run(() => SaveLocator.Scan(entry, ContextFor(item)));
        _scans[item.Id] = (Now(), scan);
        return scan;
    }

    private static List<BackupDto> ToDto(IEnumerable<BackupInfo> list) =>
        list.Select(b => new BackupDto(b.Id, new DateTimeOffset(b.CreatedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), b.SizeBytes, b.FileCount, b.Safety, b.Complete)).ToList();

    /// <summary>True if any process runs from a file below <paramref name="dir"/> (games started outside CouchHUD too).</summary>
    public static bool AnyProcessUnder(IEnumerable<(int Pid, string? Path)> processes, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var prefix = dir.TrimEnd('\\', '/') + "\\";
        return processes.Any(p => p.Path is not null && p.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void RequireNotRunning(LibraryItem item)
    {
        if (isRunning(item)) throw new InvalidOperationException(Strings.Get("saves.gameRunning"));
    }

    /// <summary>One backup/restore per game at a time.</summary>
    private async Task<T> ExclusiveAsync<T>(string id, Func<Task<T>> work)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("saves.busy"));
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
            _scans.TryRemove(id, out _);
        }
    }

    public Task<BackupResult> BackupAsync(string id) => ExclusiveAsync(id, async () =>
    {
        var (item, entry, reason, _) = await LookupAsync(id);
        if (entry is null) throw new InvalidOperationException(reason);
        RequireNotRunning(item);
        return await Task.Run(() =>
        {
            var scan = SaveLocator.Scan(entry, ContextFor(item));
            return BackupsFor(item).Backup(Ref(item), scan);
        });
    });

    public Task<RestoreResult> RestoreAsync(string id, string backupId) => ExclusiveAsync(id, async () =>
    {
        var (item, entry, reason, _) = await LookupAsync(id);
        if (entry is null) throw new InvalidOperationException(reason);
        RequireNotRunning(item);
        return await Task.Run(() => BackupsFor(item).Restore(Ref(item), backupId, entry, ContextFor(item)));
    });

    public Task<bool> DeleteBackupAsync(string id, string backupId) => ExclusiveAsync(id, () =>
    {
        var item = findItem(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        return Task.Run(() => BackupsFor(item).Delete(Ref(item), backupId));
    });
}
