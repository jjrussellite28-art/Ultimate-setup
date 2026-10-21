using System.Text.Json;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>What is known about an installed CaseHUD.exe: whether it understands <c>--background</c> and announces the local API.</summary>
public enum CaseHudCapability { Unknown, Capable, Old }

/// <summary>
/// Remembers the result of the capability probe for exactly one build of CaseHUD.exe (path + version + time stamp of the file),
/// so the program is not probed on every start. A new build gets a new key and is probed again. The version number is only
/// part of the key, never a rule: whether CaseHUD can be controlled is decided by what it does (api.json appears, or exit
/// code 2 on the unknown flag), not by what it calls itself.
/// </summary>
public sealed class CaseHudCapabilityStore(string file, Func<string, string>? stamp = null, Func<DateTimeOffset>? now = null)
{
    /// <summary>"Old" is only held this long: the user may have updated in a way that keeps path, version and time stamp.</summary>
    public static readonly TimeSpan OldLifetime = TimeSpan.FromDays(7);
    private const long MaxFileBytes = 4096;

    private sealed record Entry(string Key, bool Capable, DateTimeOffset At = default);

    private readonly object _lock = new();
    private readonly Func<string, string> _stamp = stamp ?? DefaultStamp;

    private static string DefaultStamp(string exe)
    {
        try { return File.GetLastWriteTimeUtc(exe).Ticks.ToString(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "0"; }
    }

    private string KeyOf(CaseHudInstall install) => $"{install.Exe.ToLowerInvariant()}|{install.Version}|{_stamp(install.Exe)}";

    public CaseHudCapability Get(CaseHudInstall install)
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileBytes) return CaseHudCapability.Unknown;
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                if (entry is null || entry.Key != KeyOf(install)) return CaseHudCapability.Unknown;
                if (entry.Capable) return CaseHudCapability.Capable;
                return (now?.Invoke() ?? DateTimeOffset.UtcNow) - entry.At < OldLifetime ? CaseHudCapability.Old : CaseHudCapability.Unknown;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return CaseHudCapability.Unknown;
            }
        }
    }

    public void Set(CaseHudInstall install, bool capable)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                // Next to the file, then moved over it: a crash in between never leaves half a file.
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(KeyOf(install), capable, now?.Invoke() ?? DateTimeOffset.UtcNow)));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[casehud] capability not saved: {ex.Message}");
            }
        }
    }
}
