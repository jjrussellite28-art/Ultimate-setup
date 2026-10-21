using System.Globalization;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

public enum SteamGuardStep
{
    /// <summary>Nothing to write.</summary>
    None,
    /// <summary>Steam is not running: write now.</summary>
    PatchNow,
    /// <summary>Steam runs and nothing is playing: end it cleanly, write, start it again quietly.</summary>
    CycleSteam,
    /// <summary>Steam runs and a game is on (or the moment is not a quiet one): leave it, try again later.</summary>
    Defer
}

/// <summary>
/// Steam must not get in CouchHud's way on the couch: no Big Picture at its start, the Guide button does not bring Steam
/// to the front, no controller pop-ups. These are settings of Steam itself, in its own files, set through
/// <see cref="GuardSnapshot"/> (own state file, original kept, put back only while the value is still ours).
/// Steam keeps its settings in memory and writes the whole file again whenever one changes and on exit, so nothing is
/// written while it runs (see <see cref="Plan"/>).
/// All four keys were confirmed in a Steam client (October 2026, German UI), each by switching exactly one option in
/// the settings and comparing the files before and after:
///   Settings › Interface › "Start Steam in Big Picture mode"        → HKCU\Software\Valve\Steam\StartupMode (DWORD) 4 = on, 7 = off
///   Settings › Controller › Advanced › "Guide button focuses Steam" → userdata\id\config\localconfig.vdf, UserLocalConfigStore/Controller_CheckGuideButton "0" = off
///   Settings › In Game › "Use Big Picture overlay when using a controller" → localconfig.vdf, UserLocalConfigStore/system/EnableSCTenFootOverlayCheckNew "1" = on
///   Settings › Notifications › "When I connect or disconnect a controller" (pop-up) → localconfig.vdf, UserLocalConfigStore/system/ControllerConnectNotificationToast "0" = off
/// Not touched on purpose: "Allow Guide button chords" (UserLocalConfigStore/SteamController_Enable_Chord, confirmed too) would
/// take Steam's own Guide shortcuts away in games that use Steam Input, and the overlay itself (EnableGameOverlay) stays on.
/// </summary>
public static class SteamGuard
{
    public const string StateFile = "guard-steam.json";
    public const string RegistryKey = @"HKCU\Software\Valve\Steam";
    public const string StartupModeValue = "StartupMode";
    public const int StartupBigPicture = 4;
    public const int StartupDesktop = 7;

    /// <summary>Keys below UserLocalConfigStore of every account's localconfig.vdf and the value that keeps Steam quiet.</summary>
    public static readonly IReadOnlyList<(string Key, string Value)> LocalConfigKeys =
    [
        ("UserLocalConfigStore/Controller_CheckGuideButton", "0"),
        ("UserLocalConfigStore/system/EnableSCTenFootOverlayCheckNew", "0"),
        ("UserLocalConfigStore/system/ControllerConnectNotificationToast", "0")
    ];

    /// <summary>Full integration on the couch with "CouchHud stays in front" on; light mode and PC mode leave Steam as it is.</summary>
    public static bool ShouldApply(bool full, bool pcMode, bool suppressCompetitors) => full && !pcMode && suppressCompetitors;

    public static string LocalConfigPath(string steamRoot, string userId) =>
        Path.Combine(steamRoot, "userdata", userId, "config", "localconfig.vdf");

    /// <summary>Account folders in userdata: the numeric ones (0 is the anonymous one and has no settings).</summary>
    public static IReadOnlyList<string> UserIds(IEnumerable<string> folderNames) =>
        folderNames.Where(n => n.Length is > 0 and <= 20 && n.All(char.IsAsciiDigit) && n.TrimStart('0').Length > 0).Distinct().Order().ToList();

    /// <summary>
    /// What to set. Per account only where localconfig.vdf exists and can be read (Steam creates it; a missing or damaged file is
    /// not ours to make). StartupMode only where Steam would start Big Picture (4): nothing is written for a user who never set it.
    /// </summary>
    public static IReadOnlyList<GuardChange> Changes(string steamRoot, IEnumerable<string> userIds, IVdfStore vdf, IRegistry registry)
    {
        var changes = new List<GuardChange>();
        foreach (var id in userIds)
        {
            var file = LocalConfigPath(steamRoot, id);
            try
            {
                if (!vdf.Exists(file)) continue;
                _ = vdf.Lookup(file, LocalConfigKeys[0].Key); // throws if the file cannot be parsed
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var (key, value) in LocalConfigKeys) changes.Add(new GuardChange(GuardKind.VdfKey, file, key, value, OnlyIfUnchanged: true));
        }
        if (registry.GetValueType(RegistryKey, StartupModeValue) == RegistryValueType.Dword
            && registry.ReadDword(RegistryKey, StartupModeValue) == StartupBigPicture)
        {
            changes.Add(new GuardChange(GuardKind.RegistryDword, RegistryKey, StartupModeValue,
                StartupDesktop.ToString(CultureInfo.InvariantCulture), OnlyIfUnchanged: true));
        }
        return changes;
    }

    /// <summary>Whether applying these would write anything (a value that already reads like that does not need Steam to stop).</summary>
    public static bool WouldChange(IEnumerable<GuardChange> changes, IVdfStore vdf, IRegistry registry) =>
        changes.Any(c => Current(c.Kind, c.Location, c.Name, vdf, registry) != Normal(c.Kind, c.Value));

    /// <summary>Whether a restore would write anything: only entries that still hold our value (and are not back to the original already).</summary>
    public static bool WouldRestore(IEnumerable<GuardEntry> entries, IVdfStore vdf, IRegistry registry) =>
        entries.Any(e =>
        {
            var current = Current(e.Kind, e.Location, e.Name, vdf, registry);
            return current == Normal(e.Kind, e.Applied) && current != (e.Original is null ? null : Normal(e.Kind, e.Original));
        });

    public static SteamGuardStep Plan(bool needsWrite, bool steamRunning, bool gameRunning, bool allowCycle)
    {
        if (!needsWrite) return SteamGuardStep.None;
        if (!steamRunning) return SteamGuardStep.PatchNow;
        return gameRunning || !allowCycle ? SteamGuardStep.Defer : SteamGuardStep.CycleSteam;
    }

    private static string Normal(GuardKind kind, string value) =>
        kind == GuardKind.RegistryDword && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? unchecked((uint)(int)v).ToString(CultureInfo.InvariantCulture)
            : value;

    private static string? Current(GuardKind kind, string location, string name, IVdfStore vdf, IRegistry registry)
    {
        try
        {
            switch (kind)
            {
                case GuardKind.RegistryDword:
                    return registry.GetValueType(location, name) == RegistryValueType.Dword && registry.ReadDword(location, name) is { } d
                        ? unchecked((uint)d).ToString(CultureInfo.InvariantCulture)
                        : null;
                case GuardKind.RegistryString:
                    return registry.ReadString(location, name);
                default:
                    return vdf.Lookup(location, name) is { Kind: VdfLookupKind.Value } l ? l.Value : null;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
