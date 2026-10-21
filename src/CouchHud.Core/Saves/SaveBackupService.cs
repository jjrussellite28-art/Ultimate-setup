using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Saves;

/// <summary>The game a backup belongs to: display name and the stable library id (folders are bound to the id).</summary>
public sealed record GameRef(string Name, string Key);

public sealed record BackupInfo(string Id, DateTime CreatedUtc, long SizeBytes, int FileCount, bool Safety, bool Complete);

public sealed record BackupResult(string? Id, int FileCount, long Bytes, IReadOnlyList<string> Errors, bool Complete);

/// <param name="RolledBack">A write failed halfway and the state from before the restore was put back.</param>
public sealed record RestoreResult(int Restored, IReadOnlyList<string> Errors, string? SafetyBackupId, bool RolledBack = false);

/// <summary>
/// ZIP backups of a game's saves below <c>&lt;backupRoot&gt;\&lt;game&gt; [&lt;id&gt;]\&lt;yyyy-MM-dd_HH-mm&gt;.zip</c>.
/// Inside: <c>manifest.json</c> plus <c>files/&lt;placeholder&gt;/&lt;relative path&gt;</c>, so a restore finds the
/// right place even if the user name or install folder changed. Everything is written to a temp file
/// first and moved into place; single files that can't be read are reported, and such a backup is
/// marked incomplete (<c>_unvollstaendig</c>): it never counts toward the limit and is never rotated away.
/// Safety backups taken before a restore (<c>_vorher</c>) have their own, separate limit.
/// </summary>
public sealed partial class SaveBackupService(string backupRoot, int keep)
{
    private const string ManifestName = "manifest.json";
    private const int FormatVersion = 1;
    private const string SafetySuffix = "_vorher";
    private const string IncompleteSuffix = "_unvollstaendig";

    /// <summary>How many safety backups are kept per game, independent of the regular limit.</summary>
    public const int SafetyKeep = 3;

    /// <summary>A backup with fewer than this share of the previous one's files counts as incomplete.</summary>
    private const double SuspiciousShrink = 0.5;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal sealed record ZipEntryInfo(string Zip, string Root, string Rel, long Size, DateTime ModifiedUtc);

    internal sealed record ZipManifest(int Version, string Game, DateTime CreatedUtc, List<ZipEntryInfo> Entries)
    {
        public bool Complete { get; init; } = true;
        public bool Safety { get; init; }
    }

    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Test seam: called before each file is written during a restore.</summary>
    internal Action<string>? BeforeWrite { get; init; }

    /// <summary>Test seam: replaces the scan of the current state taken for the safety backup.</summary>
    internal Func<ManifestEntry, SaveContext, SaveScan>? SafetyScan { get; init; }

    public string GameDirectory(GameRef game) => Path.Combine(backupRoot, FolderName(game));

    public static string FolderName(GameRef game) => $"{SafeName(game.Name)} [{SafeName(game.Key)}]";

    /// <summary>Folder name older versions used (the display name only).</summary>
    public string LegacyDirectory(GameRef game) => Path.Combine(backupRoot, SafeName(game.Name));

    /// <summary>Moves a pre-id backup folder to the id-bound name. Call only if the name is unique in the library.</summary>
    public void MigrateLegacy(GameRef game)
    {
        var old = LegacyDirectory(game);
        var target = GameDirectory(game);
        if (!Directory.Exists(old) || Directory.Exists(target)) return;
        try
        {
            Directory.Move(old, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[saves] migrate {old}: {ex.Message}");
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Spiel" : cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    [GeneratedRegex(@"^[0-9A-Za-z_\- ]{1,100}\.zip$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id) && Path.GetFileName(id) == id;

    /// <summary>ZIP can't store years before 1980 or after 2107 (and throws).</summary>
    internal static DateTimeOffset ZipTime(DateTime utc)
    {
        if (utc.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (utc.Year > 2107) return new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    // --- Backup ---------------------------------------------------------

    public BackupResult Backup(GameRef game, SaveScan scan, bool safety = false, string? protectedId = null)
    {
        var errors = new List<string>(scan.Errors);
        if (scan.Files.Count == 0) return new BackupResult(null, 0, 0, [Strings.Get("saves.noneFound"), .. errors], false);

        var dir = GameDirectory(game);
        var temp = Path.Combine(dir, "backup-" + Guid.NewGuid().ToString("N") + ".part");
        var entries = new List<ZipEntryInfo>();
        long bytes = 0;
        string? id = null;
        var complete = false;
        try
        {
            Directory.CreateDirectory(dir);
            var previous = safety ? null : List(game).FirstOrDefault(b => b is { Safety: false, Complete: true });
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var file in scan.Files)
                {
                    var name = $"files/{file.Root}/{file.Rel}";
                    try
                    {
                        // Games often keep the save open; reading along is fine.
                        using var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTime(file.LastWriteUtc);
                        using (var target = entry.Open()) source.CopyTo(target);
                        entries.Add(new ZipEntryInfo(name, file.Root, file.Rel, file.Size, file.LastWriteUtc));
                        bytes += file.Size;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        errors.Add($"{file.Rel}: {ex.Message}");
                    }
                }

                complete = errors.Count == 0 && !scan.Truncated;
                if (complete && previous is { FileCount: >= 4 } && entries.Count < previous.FileCount * SuspiciousShrink)
                {
                    complete = false;
                    errors.Add(Strings.Get("saves.fewerFiles", entries.Count, previous.FileCount));
                }
                if (entries.Count > 0)
                {
                    var created = Now().ToUniversalTime();
                    var manifest = new ZipManifest(FormatVersion, game.Name, created, entries) { Complete = complete, Safety = safety };
                    var m = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                    using var w = m.Open();
                    JsonSerializer.Serialize(w, manifest, Json);
                }
            }

            if (entries.Count == 0)
            {
                TryDelete(temp);
                return new BackupResult(null, 0, 0, errors.Count > 0 ? errors : [Strings.Get("saves.noneReadable")], false);
            }

            var baseName = Now().ToString("yyyy-MM-dd_HH-mm") + (safety ? SafetySuffix : "") + (complete ? "" : IncompleteSuffix);
            id = baseName + ".zip";
            for (var n = 2; File.Exists(Path.Combine(dir, id)); n++) id = $"{baseName}-{n}.zip";
            File.Move(temp, Path.Combine(dir, id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(temp);
            errors.Add(ex.Message);
            return new BackupResult(null, 0, 0, errors, false);
        }

        Rotate(game, id, protectedId);
        return new BackupResult(id, entries.Count, bytes, errors, complete);
    }

    /// <summary>
    /// Regular complete backups: newest <c>keep</c>. Safety backups: newest <see cref="SafetyKeep"/>, counted
    /// apart. Incomplete ones are never touched. The new backup and a restore source always stay.
    /// </summary>
    internal void Rotate(GameRef game, params string?[] protectedIds)
    {
        var all = List(game);
        var regular = all.Where(b => !b.Safety && b.Complete).Skip(Math.Max(1, keep));
        var safety = all.Where(b => b.Safety && b.Complete).Skip(SafetyKeep);
        foreach (var old in regular.Concat(safety).Where(b => !protectedIds.Contains(b.Id)))
        {
            TryDelete(Path.Combine(GameDirectory(game), old.Id));
        }
    }

    // --- List -----------------------------------------------------------

    /// <summary>Newest first, by the creation time stored inside the backup.</summary>
    public List<BackupInfo> List(GameRef game)
    {
        var dir = GameDirectory(game);
        if (!Directory.Exists(dir)) return [];
        var result = new List<BackupInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var info = new FileInfo(path);
            if (!IsValidId(info.Name)) continue;
            var stem = Path.GetFileNameWithoutExtension(info.Name);
            var count = 0;
            var created = info.LastWriteTimeUtc;
            var complete = !stem.Contains(IncompleteSuffix);
            var safety = stem.Contains(SafetySuffix);
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (ReadManifest(zip) is { } m)
                {
                    count = m.Entries.Count;
                    created = m.CreatedUtc;
                    complete &= m.Complete;
                    safety |= m.Safety;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                complete = false;
            }
            result.Add(new BackupInfo(info.Name, created, info.Length, count, safety, complete));
        }
        return result.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToList();
    }

    public bool Delete(GameRef game, string id)
    {
        if (!IsValidId(id)) return false;
        var path = Path.Combine(GameDirectory(game), id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static ZipManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName);
        if (entry is null || entry.Length > 32 * 1024 * 1024) return null;
        using var s = entry.Open();
        return JsonSerializer.Deserialize<ZipManifest>(s, Json);
    }

    // --- Restore --------------------------------------------------------

    private sealed record PlanItem(ZipEntryInfo Item, string Target);

    /// <summary>
    /// Writes the backup's files back. Order: check every target, take a safety backup of the current state
    /// (aborting if that is not complete), write; if a write fails the state from before is put back.
    /// Only paths the manifest names as save locations are ever written, whatever the ZIP says.
    /// Files that exist now but not in the backup are left alone.
    /// </summary>
    public RestoreResult Restore(GameRef game, string id, ManifestEntry entry, SaveContext ctx)
    {
        if (!IsValidId(id)) throw new ArgumentException(Strings.Get("saves.invalidBackup"));
        var path = Path.Combine(GameDirectory(game), id);
        if (!File.Exists(path)) throw new FileNotFoundException(Strings.Get("saves.backupNotFound"));

        var errors = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadManifest(zip) ?? throw new InvalidDataException(Strings.Get("saves.backupDamaged"));
        if (manifest.Version > FormatVersion) throw new InvalidDataException(Strings.Get("saves.backupNewer"));

        var patterns = SaveLocator.ResolvePatterns(entry, ctx);

        // 1. Every target resolvable and writable, before anything changes.
        var plan = Plan(zip, manifest, ctx, patterns, errors);
        if (plan.Count == 0) throw new InvalidDataException(Strings.Get("saves.nothingToRestore") + (errors.FirstOrDefault() ?? Strings.Get("saves.empty")));
        var blocked = plan.Where(p => !IsWritable(p.Target)).Take(5).Select(p => p.Item.Rel).ToList();
        if (blocked.Count > 0) throw new IOException(Strings.Get("saves.notWritable") + string.Join(", ", blocked));

        // 2. Safety net: if it isn't complete, nothing has been touched yet.
        string? safetyId = null;
        var current = (SafetyScan ?? ((e, c) => SaveLocator.Scan(e, c)))(entry, ctx);
        if (current.Files.Count > 0)
        {
            var safety = Backup(game, current, safety: true, protectedId: id);
            if (safety.Id is null || !safety.Complete)
            {
                throw new IOException(Strings.Get("saves.safetyIncomplete")
                                      + string.Join("; ", safety.Errors.Take(3)));
            }
            safetyId = safety.Id;
        }

        // 3. Write; on failure undo.
        var written = new List<(string Target, bool Existed)>();
        try
        {
            foreach (var p in plan)
            {
                BeforeWrite?.Invoke(p.Target);
                var existed = File.Exists(p.Target);
                WriteFile(zip.GetEntry(p.Item.Zip)!, p.Target, p.Item.ModifiedUtc);
                written.Add((p.Target, existed));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add(Strings.Get("saves.writeFailed", ex.Message));
            var rollbackErrors = Rollback(game, written, safetyId, entry, ctx, patterns);
            errors.AddRange(rollbackErrors);
            return new RestoreResult(0, errors, safetyId, RolledBack: rollbackErrors.Count == 0);
        }
        return new RestoreResult(written.Count, errors, safetyId);
    }

    private List<PlanItem> Plan(ZipArchive zip, ZipManifest manifest, SaveContext ctx, IReadOnlyList<string> patterns, List<string> errors)
    {
        var plan = new List<PlanItem>();
        foreach (var item in manifest.Entries)
        {
            try
            {
                var target = ResolveTarget(item, ctx, patterns);
                if (!item.Zip.StartsWith("files/", StringComparison.Ordinal) || zip.GetEntry(item.Zip) is null)
                {
                    throw new InvalidDataException(Strings.Get("saves.fileMissing"));
                }
                plan.Add(new PlanItem(item, target));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"{item.Rel}: {ex.Message}");
            }
        }
        return plan;
    }

    /// <summary>Probes whether the target can be written without changing it (the folder may get created).</summary>
    private static bool IsWritable(string target)
    {
        try
        {
            if (Directory.Exists(target)) return false;
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);
            if (File.Exists(target))
            {
                // Read-only is fine, it gets cleared for the write; locked is not.
                using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            var probe = Path.Combine(dir, ".couchhud-probe-" + Guid.NewGuid().ToString("N") + SaveLocator.TempSuffix);
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only file can't be opened ReadWrite; check the attribute alone.
            return File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0;
        }
    }

    private static void WriteFile(ZipArchiveEntry zipEntry, string target, DateTime modifiedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + SaveLocator.TempSuffix;
        FileAttributes? original = File.Exists(target) ? File.GetAttributes(target) : null;
        var readOnly = original is { } a && (a & FileAttributes.ReadOnly) != 0;
        try
        {
            using (var source = zipEntry.Open())
            using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                source.CopyTo(dest);
            }
            try
            {
                File.SetLastWriteTimeUtc(temp, modifiedUtc);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Absurd stored date: keep "now".
            }
            if (readOnly) File.SetAttributes(target, original!.Value & ~FileAttributes.ReadOnly);
            File.Move(temp, target, true);
            if (readOnly) File.SetAttributes(target, original!.Value);
        }
        catch
        {
            TryDelete(temp);
            if (readOnly)
            {
                try { File.SetAttributes(target, original!.Value); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Deletes what the restore created and writes the safety backup back over what it changed.</summary>
    private List<string> Rollback(GameRef game, List<(string Target, bool Existed)> written, string? safetyId,
        ManifestEntry entry, SaveContext ctx, IReadOnlyList<string> patterns)
    {
        var errors = new List<string>();
        foreach (var (target, existed) in written.Where(w => !w.Existed))
        {
            try { File.Delete(target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Rollback {target}: {ex.Message}"); }
        }
        if (safetyId is null) return errors;
        try
        {
            using var zip = ZipFile.OpenRead(Path.Combine(GameDirectory(game), safetyId));
            var manifest = ReadManifest(zip) ?? throw new InvalidDataException(Strings.Get("saves.manifestMissing"));
            var plan = Plan(zip, manifest, ctx, patterns, errors);
            foreach (var p in plan)
            {
                try { WriteFile(zip.GetEntry(p.Item.Zip)!, p.Target, p.Item.ModifiedUtc); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Rollback {p.Item.Rel}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            errors.Add(Strings.Get("saves.rollbackFailed", ex.Message));
        }
        if (errors.Count > 0) errors.Add(Strings.Get("saves.beforeRestore", safetyId));
        return errors;
    }

    /// <summary>Absolute target for a manifest entry; throws if it would leave its root or the save locations.</summary>
    internal static string ResolveTarget(ZipEntryInfo item, SaveContext ctx, IReadOnlyList<string> patterns)
    {
        var parts = item.Rel.Split('/');
        string rootDir;
        if (item.Root == SaveLocator.AbsoluteToken)
        {
            // "C/Games/x/save.dat": drive letter first.
            if (parts.Length < 2 || parts[0].Length != 1 || !char.IsAsciiLetter(parts[0][0])) throw new InvalidDataException(Strings.Get("saves.illegalPath"));
            rootDir = char.ToUpperInvariant(parts[0][0]) + ":";
            parts = parts[1..];
        }
        else
        {
            rootDir = ctx.RootDir(item.Root) ?? throw new InvalidDataException(Strings.Get("saves.unknownRoot", item.Root));
        }
        if (item.Rel.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException(Strings.Get("saves.illegalPath"));
        }
        var full = Path.GetFullPath(Path.Combine(rootDir + "\\", string.Join('\\', parts)));
        if (!full.StartsWith(rootDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Strings.Get("saves.outsideRoot"));
        if (!SaveLocator.IsAllowedTarget(full, patterns)) throw new InvalidDataException(Strings.Get("saves.notSaveLocation"));
        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
