using System.Globalization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Updates;

/// <summary>
/// Marker next to the config while a setup started by CouchHUD replaces it: the guard must neither lock Windows nor
/// restart the old version for that. Content is "unixMilliseconds setupPid"; the file time is ignored (anyone can
/// touch it). The marker counts only while it is younger than <see cref="Lifetime"/> (and not from the future)
/// and the setup process is still alive, so a stale file never switches the guard off.
/// Two places: the user's config folder (self-update, written by CouchHUD) and the program folder (written by the
/// installer and the uninstall cleanup; only admins can write there, and %APPDATA% would be the wrong user's
/// when the setup runs in another session than CouchHUD). <see cref="IsActiveAny"/> checks both.
/// </summary>
public static class UpdateMarker
{
    public const string FileName = "updating";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static void Write(string dataDir, int setupPid, DateTimeOffset now)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, FileName), $"{now.ToUnixTimeMilliseconds()} {setupPid}");
    }

    public static void Clear(string dataDir)
    {
        try { File.Delete(Path.Combine(dataDir, FileName)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static (DateTimeOffset At, int Pid)? Read(string dataDir)
    {
        try
        {
            var parts = File.ReadAllText(Path.Combine(dataDir, FileName)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return null;
            return (DateTimeOffset.FromUnixTimeMilliseconds(ms), pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Whether the marker in any of the folders is active (see <see cref="IsActive"/>).</summary>
    public static bool IsActiveAny(IEnumerable<string> dataDirs, DateTimeOffset now, Func<int, bool> isAlive) =>
        dataDirs.Any(dir => IsActive(dir, now, isAlive));

    /// <param name="isAlive">Whether a process with this id is running.</param>
    public static bool IsActive(string dataDir, DateTimeOffset now, Func<int, bool> isAlive)
    {
        if (Read(dataDir) is not { } marker) return false;
        var age = now - marker.At;
        return age >= TimeSpan.Zero && age < Lifetime && marker.Pid > 0 && isAlive(marker.Pid);
    }
}

/// <summary>Redirections (junctions, symbolic links, cloud placeholders) have no business in the path of a setup that is run elevated.</summary>
public static class PathSafety
{
    /// <summary>Throws if the path or any folder above it is a reparse point.</summary>
    public static void RequireNoReparsePoints(string path)
    {
        var current = StripPrefix(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                current = Path.GetDirectoryName(current);
                continue;
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new CatalogException(Strings.Get("updatesafety.reparse", current));
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>An empty, real folder: an existing one is removed first (a link only as the link, never its target).</summary>
    public static void PrepareCleanDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: !File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
        }
        else if (File.Exists(directory))
        {
            File.Delete(directory);
        }
        Directory.CreateDirectory(directory);
        RequireNoReparsePoints(directory);
    }

    /// <summary>"\\?\C:\x" (what GetFinalPathNameByHandle returns) as "C:\x".</summary>
    public static string StripPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>Whether the file the handle really points to is the one we think it is.</summary>
    public static void RequireSamePath(string expected, string finalPathOfHandle)
    {
        if (!StripPrefix(finalPathOfHandle).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogException(Strings.Get("updatesafety.setupMoved"));
        }
    }
}
