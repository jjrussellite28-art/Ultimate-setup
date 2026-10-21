using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>
/// Per-user Windows policies for the locked couch, all in HKCU and all applied through <see cref="GuardSnapshot"/>
/// so the user's own values come back exactly (also after a crash, in PC mode, on quit and on uninstall):
/// no Control Panel / Run box / Win-key shortcuts / context menus / folder options, no command prompt,
/// PowerShell, registry editor, management console or terminal started through the shell, no change of password.
/// (DisableRegistryTools is deliberately not set: it would also stop silent .reg imports of installers and cannot be
/// undone by someone who is locked out.)
/// Also the Game Bar shortcut and the Narrator start shortcut. Only meaningful for an account with a password.
/// </summary>
public static class LockHardening
{
    public const string ExplorerKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string DisallowRunKey = ExplorerKey + @"\DisallowRun";
    public const string SystemKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string WindowsSystemKey = @"HKCU\Software\Policies\Microsoft\Windows\System";
    public const string NarratorKey = @"HKCU\Software\Microsoft\Narrator\NoRoam";

    /// <summary>Started through the shell (ShellExecute) these are refused. CouchHud starts its own helpers without the shell.</summary>
    public static readonly IReadOnlyList<string> BlockedPrograms =
        ["cmd.exe", "powershell.exe", "pwsh.exe", "regedit.exe", "mmc.exe", "wt.exe"];

    private static GuardChange Dword(string key, string name, int value) =>
        new(GuardKind.RegistryDword, key, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The changes for a locked couch. DisallowRun is a list under a subkey (values named 1, 2, …): our programs
    /// are added under free numbers, the user's own entries stay, and a program that is listed already is not added twice.
    /// DisableCMD is 2 ("interactive command prompt only"), not 1: 1 also stops every batch file and every "cmd /c" of a
    /// launcher, a startup entry or a game, which CouchHud itself starts for the user.
    /// </summary>
    public static IReadOnlyList<GuardChange> Changes(IRegistry registry)
    {
        var changes = new List<GuardChange>
        {
            Dword(ExplorerKey, "NoControlPanel", 1),
            Dword(ExplorerKey, "NoRun", 1),
            Dword(ExplorerKey, "NoWinKeys", 1),
            Dword(ExplorerKey, "NoViewContextMenu", 1),
            Dword(ExplorerKey, "NoFolderOptions", 1),
            Dword(ExplorerKey, "DisallowRun", 1),
            Dword(SystemKey, "DisableChangePassword", 1),
            Dword(WindowsSystemKey, "DisableCMD", 2),
            Dword(NarratorKey, "WinEnterLaunchEnabled", 0),
            GameBarGuard.CaptureOff
        };
        var existing = registry.ReadValues(DisallowRunKey);
        var taken = new HashSet<string>(existing.Keys, StringComparer.OrdinalIgnoreCase);
        var next = 1;
        foreach (var program in BlockedPrograms)
        {
            if (existing.Values.Any(v => string.Equals(v.Trim(), program, StringComparison.OrdinalIgnoreCase))) continue;
            while (taken.Contains(next.ToString(System.Globalization.CultureInfo.InvariantCulture))) next++;
            var name = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            taken.Add(name);
            changes.Add(new GuardChange(GuardKind.RegistryString, DisallowRunKey, name, program));
        }
        return changes;
    }

    /// <summary>
    /// The keys Windows protects from the user's own processes: the Policies keys let only administrators write, everything
    /// running as the user can just read them. The other keys of the lock (Narrator, Game Bar capture) are the user's own.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedRoots = [ExplorerKey, SystemKey, WindowsSystemKey];

    public static bool IsProtectedKey(string key) =>
        ProtectedRoots.Any(root => key.Equals(root, StringComparison.OrdinalIgnoreCase) || key.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The changes of <see cref="Changes"/> that need administrator rights to write.</summary>
    public static IReadOnlyList<GuardChange> ProtectedChanges(IRegistry registry) => Changes(registry).Where(c => IsProtectedKey(c.Location)).ToList();

    /// <summary>Task Manager off: the same protected key, applied on its own (only with the option on).</summary>
    public static readonly GuardChange TaskManagerChange = Dword(SystemKey, "DisableTaskMgr", 1);

    /// <summary>
    /// Keys the policies may create, children before parents. A key that did not exist before the first apply
    /// (<see cref="MissingKeys"/>) is removed again after the restore if it is empty (<see cref="RemoveEmpty"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> TrackedKeys =
    [
        DisallowRunKey, ExplorerKey, SystemKey, WindowsSystemKey, @"HKCU\Software\Policies\Microsoft\Windows",
        NarratorKey, @"HKCU\Software\Microsoft\Narrator", GameBarGuard.CaptureKey
    ];

    public static IReadOnlyList<string> MissingKeys(IRegistry registry) => TrackedKeys.Where(k => !registry.KeyExists(k)).ToList();

    public static void RemoveEmpty(IRegistry registry, IEnumerable<string> keys)
    {
        // Children first, in the order of the tracked list; anything unknown is ignored.
        foreach (var key in TrackedKeys.Where(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                registry.DeleteKeyIfEmpty(key);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // A key we cannot remove is harmless: it is empty.
            }
        }
    }

    /// <summary>Accessibility shortcuts have a "hotkey active" flag (0x4) in the flags of every one of them.</summary>
    public const uint HotkeyActive = 0x4;

    /// <summary>The flags with the shortcut switched off; the other flags (the feature itself) stay as they are.</summary>
    public static uint WithoutHotkey(uint flags) => flags & ~HotkeyActive;

    /// <summary>
    /// What to write back on unlock: the current flags with the shortcut back on if it was on before.
    /// If the user changed the feature meanwhile (current has other flags), those stay.
    /// </summary>
    public static uint WithHotkeyRestored(uint current, uint original) => (current & ~HotkeyActive) | (original & HotkeyActive);
}
