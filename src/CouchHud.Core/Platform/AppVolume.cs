using System.Globalization;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Platform;

/// <summary>One Core Audio session as read from the system (a process can own several).</summary>
public sealed record RawAudioSession(int Pid, string ProcessName, string? DisplayName, bool IsSystemSounds, float Volume, bool Muted);

/// <summary>An app in the per-app volume list: all sessions of one process folded together.</summary>
public sealed record AppVolumeEntry(string Id, string Name, bool IsSystem, bool IsGame, int Volume, bool Muted);

/// <summary>Pure logic behind "Lautstärke pro App": grouping, naming and ordering of audio sessions.</summary>
public static class AppVolume
{
    public const string SystemId = "system";

    /// <summary>CouchHud itself (its UI sounds and music), not an app the user knows.</summary>
    private static readonly string[] Hidden = ["couchhud"];

    public static string IdOf(RawAudioSession s) => s.IsSystemSounds ? SystemId : s.Pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Folds sessions per process; the running game first, then apps by name, system sounds last.
    /// <paramref name="ownPids"/> are CouchHud and its WebView2 helpers, which are left out.
    /// </summary>
    public static IReadOnlyList<AppVolumeEntry> Arrange(IEnumerable<RawAudioSession> sessions, IReadOnlySet<int> gamePids, IReadOnlySet<int>? ownPids = null)
    {
        var entries = new List<AppVolumeEntry>();
        foreach (var group in sessions
                     .Where(s => s.IsSystemSounds
                                 || (!Hidden.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase) && ownPids?.Contains(s.Pid) != true))
                     .GroupBy(IdOf))
        {
            var first = group.First();
            // Several streams in one process: show the loudest, muted only if all are.
            var volume = group.Max(s => s.Volume);
            var muted = group.All(s => s.Muted);
            entries.Add(new AppVolumeEntry(
                group.Key,
                first.IsSystemSounds ? Strings.Get("audio.systemSounds") : NameOf(first),
                first.IsSystemSounds,
                !first.IsSystemSounds && gamePids.Contains(first.Pid),
                (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100),
                muted));
        }
        return entries
            .OrderBy(e => e.IsSystem)
            .ThenByDescending(e => e.IsGame)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The session's own title if it is plain text, else the process name with a capital letter.</summary>
    public static string NameOf(RawAudioSession s)
    {
        var display = s.DisplayName?.Trim();
        // "@%SystemRoot%\system32\..." is an unresolved resource string, not a name.
        if (!string.IsNullOrEmpty(display) && !display.StartsWith('@')) return display;
        var name = s.ProcessName.Trim();
        if (name.Length == 0) return Strings.Get("audio.unknownApp");
        return char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
    }
}
