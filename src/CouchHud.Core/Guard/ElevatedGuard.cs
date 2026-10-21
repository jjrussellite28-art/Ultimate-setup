using System.Globalization;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>
/// What an elevated process may take from a snapshot file (guard.json, guard-locked.json). The files live in the user's %APPDATA%:
/// any program running as the user can write them, so an administrator process that restored whatever they say (a value in HKLM,
/// a VDF file path) would hand out administrator rights. The uninstaller therefore accepts only a fixed list: DWORD values at
/// the fixed places of the lock and the Game Bar, below HKCU, and the numbered entries of the DisallowRun list with one of our
/// programs. Everything else stays in the file and is left to the next start of CouchHud without administrator rights.
/// </summary>
public static class ElevatedGuard
{
    private static readonly HashSet<(string Key, string Name)> FixedDwords = BuildFixed();

    private static HashSet<(string, string)> BuildFixed()
    {
        var set = new HashSet<(string, string)>(new KeyNameComparer());
        foreach (var c in LockHardening.Changes(NullRegistry.Instance).Where(c => c.Kind == GuardKind.RegistryDword)) set.Add((c.Location, c.Name));
        set.Add((LockHardening.TaskManagerChange.Location, LockHardening.TaskManagerChange.Name));
        set.Add((GameBarGuard.Change.Location, GameBarGuard.Change.Name));
        set.Add((GameBarGuard.CaptureOff.Location, GameBarGuard.CaptureOff.Name));
        return set;
    }

    /// <summary>The files of the user's data folder an administrator process reads or deletes.</summary>
    public static readonly string[] ElevatedFiles =
        ["guard.json", "guard-locked.json", "guard-locked.keys", "guard-locked.hotkeys", "guard-locked.helper", "config.json"];

    /// <summary>
    /// May an administrator process work in this data folder of a user? The folder is the user's own: a junction or symbolic
    /// link in its place (or in a folder above it, below the profile) or as one of the files in it would make the administrator
    /// read or write wherever it points. Any of those and the folder is left alone.
    /// </summary>
    /// <param name="profile">The user's profile folder: the check goes up to it, not beyond (null: three levels).</param>
    public static bool IsSafeUserFolder(string dir, string? profile = null)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(dir));
            var stop = profile is null ? null : Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar);
            for (var level = 0; current is not null; level++, current = current.Parent)
            {
                if (stop is null ? level >= 3 : string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) break;
                if ((int)current.Attributes != -1 && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            foreach (var name in ElevatedFiles)
            {
                // Attributes of a missing file are -1; a link whose target is gone still has its own attributes.
                var attributes = new FileInfo(Path.Combine(dir, name)).Attributes;
                if ((int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the elevated restore may write this entry back.</summary>
    public static bool Accepts(GuardEntry entry)
    {
        if (!entry.Location.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) return false;
        switch (entry.Kind)
        {
            case GuardKind.RegistryDword:
                return FixedDwords.Contains((entry.Location, entry.Name))
                       && (entry.Original is null || long.TryParse(entry.Original, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v is >= int.MinValue and <= uint.MaxValue);
            case GuardKind.RegistryString:
                // Only the list entries we added under a free number: the original is "missing", so restoring deletes the value.
                return entry.Location.Equals(LockHardening.DisallowRunKey, StringComparison.OrdinalIgnoreCase)
                       && entry.Original is null
                       && entry.Name.Length is > 0 and <= 3 && entry.Name.All(char.IsAsciiDigit)
                       && LockHardening.BlockedPrograms.Contains(entry.Applied, StringComparer.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private sealed class KeyNameComparer : IEqualityComparer<(string Key, string Name)>
    {
        public bool Equals((string Key, string Name) a, (string Key, string Name) b) =>
            a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Name) x) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(x.Key), StringComparer.OrdinalIgnoreCase.GetHashCode(x.Name));
    }
}
