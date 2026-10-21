using CouchHud.Core.Platform;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Startup;

/// <summary>An autostart entry as explorer would run it at login.</summary>
public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    string Source,
    bool DisabledInWindows);

/// <summary>When an autostart entry runs while CouchHud is the shell.</summary>
public static class StartupMode
{
    /// <summary>At boot, like with the normal desktop.</summary>
    public const string Couch = "couch";

    /// <summary>Only once the user switches to PC mode.</summary>
    public const string Pc = "pc";
}

/// <summary>A RunOnce entry: runs once at the next login, then Windows deletes it.</summary>
/// <param name="ValueName">The registry value name, including a "!" or "*" prefix.</param>
/// <param name="DeleteAfterRun">"!" prefix: the value stays until the program has run.</param>
public sealed record RunOnceEntry(string ValueName, string Name, string Command, bool CurrentUser, bool DeleteAfterRun);

/// <summary>
/// Reads the autostart entries (Run keys and Startup folders) and suggests which of
/// them belong in the living room. Everything else waits until PC mode.
/// </summary>
public static class StartupCatalog
{
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Run keys explorer processes at login, with the key Task Manager uses to disable entries.</summary>
    public static (string RunKey, string Approved, string Label)[] RunKeys =>
    [
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Run", Strings.Get("startup.userKey")),
        (@"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", "Run", Strings.Get("startup.allUsersKey")),
        (@"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", Strings.Get("startup.allUsers32Key"))
    ];

    /// <summary>RunOnce keys explorer processes at login. Machine-wide entries need admin to delete.</summary>
    public static readonly (string Key, bool CurrentUser)[] RunOnceKeys =
    [
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", true),
        (@"HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce", false)
    ];

    // Things you want on a gaming couch: launchers, chat, drivers, peripherals, cooling, RGB.
    private static readonly string[] CouchHints =
    [
        "steam", "discord", "nvidia", "nvcontainer", "geforce", "amd", "radeon", "realtek", "nahimic", "dolby", "sonic",
        "logitech", "lghub", "razer", "synapse", "corsair", "icue", "steelseries", "hyperx", "ngenuity", "wooting",
        "signalrgb", "openrgb", "fancontrol", "casehud", "librehardware", "hwinfo", "afterburner", "rtss", "rivatuner",
        "ds4windows", "dualsense", "dsx", "x360ce", "xbox", "8bitdo", "sunshine", "parsec", "moonlight", "playnite",
        "voicemeeter", "equalizer", "bluetooth", "touchpad", "audio"
    ];

    // Office and sync tools: nobody needs them while gaming on the TV.
    private static readonly string[] PcHints =
    [
        "teams", "onedrive", "outlook", "skype", "slack", "zoom", "webex", "dropbox", "googledrive", "google drive",
        "icloud", "itunes", "adobe", "acrobat", "creative cloud", "ccxprocess", "office", "msedge", "edgeupdate", "cortana",
        "java", "jusched", "evernote", "notion", "spotify", "whatsapp", "telegram", "signal", "mega", "nextcloud",
        "keepass", "1password", "bitwarden"
    ];

    public static IReadOnlyList<StartupEntry> Read(IRegistry registry, IEnumerable<(string Folder, string Label)> startupFolders, Func<string, IEnumerable<string>> listFiles)
    {
        var entries = new List<StartupEntry>();
        foreach (var (runKey, approved, label) in RunKeys)
        {
            foreach (var (name, command) in registry.ReadValues(runKey))
            {
                if (string.IsNullOrWhiteSpace(command) || command.Contains("couchhud", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new StartupEntry($"{approved}:{name}", name, command, label, IsDisabled(registry, approved, name)));
            }
        }
        foreach (var (folder, label) in startupFolders)
        {
            foreach (var file in listFiles(folder))
            {
                var name = WinPath.FileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new StartupEntry($"StartupFolder:{name}", WinPath.FileNameWithoutExtension(name), $"\"{file}\"", label, IsDisabled(registry, "StartupFolder", name)));
            }
        }
        // The same id can appear in HKCU and HKLM; the first one wins, like in Task Manager.
        return entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Pending RunOnce entries, minus CouchHud's own restore entry (it belongs to the desktop boot).</summary>
    public static IReadOnlyList<RunOnceEntry> ReadRunOnce(IRegistry registry)
    {
        var entries = new List<RunOnceEntry>();
        foreach (var (key, currentUser) in RunOnceKeys)
        {
            foreach (var (valueName, command) in registry.ReadValues(key))
            {
                if (string.IsNullOrWhiteSpace(command) || command.Contains("couchhud", StringComparison.OrdinalIgnoreCase)) continue;
                var name = valueName.TrimStart('!', '*');
                entries.Add(new RunOnceEntry(valueName, name, command, currentUser, valueName.StartsWith('!')));
            }
        }
        return entries;
    }

    /// <summary>Like Windows: the value goes before the start, except with the "!" prefix.</summary>
    public static bool DeleteRunOnceBefore(RunOnceEntry entry) => !entry.DeleteAfterRun;

    /// <summary>With the "!" prefix the value goes after the start, and only if the start worked.</summary>
    public static bool DeleteRunOnceAfter(RunOnceEntry entry, bool started) => entry.DeleteAfterRun && started;

    /// <summary>Couch for gaming-related tools and anything unknown, PC mode for office and sync tools.</summary>
    public static string Suggest(StartupEntry entry)
    {
        var text = $"{entry.Name} {entry.Command}".ToLowerInvariant();
        if (CouchHints.Any(text.Contains)) return StartupMode.Couch;
        return PcHints.Any(text.Contains) ? StartupMode.Pc : StartupMode.Couch;
    }

    /// <summary>The mode the user chose, or couch (what Windows would do).</summary>
    public static string ModeOf(StartupEntry entry, IReadOnlyDictionary<string, string> modes) =>
        modes.TryGetValue(entry.Id, out var mode) && mode == StartupMode.Pc ? StartupMode.Pc : StartupMode.Couch;

    /// <summary>
    /// What the shell starts at boot: the enabled entries that are not waiting for PC mode, each as the command line it is run with
    /// (variables expanded, then <paramref name="adapt"/>). The registry values themselves are never touched: they belong to the
    /// program that wrote them and are right for explorer.
    /// </summary>
    public static IReadOnlyList<(StartupEntry Entry, string CommandLine)> CouchCommands(IEnumerable<StartupEntry> entries, IReadOnlyDictionary<string, string> modes, Func<string, string>? adapt = null) =>
        entries.Where(e => !e.DisabledInWindows && ModeOf(e, modes) == StartupMode.Couch)
            .Select(e => (Entry: e, CommandLine: Environment.ExpandEnvironmentVariables(e.Command)))
            .Select(x => (x.Entry, adapt is null ? x.CommandLine : adapt(x.CommandLine)))
            .ToList();

    /// <summary>File name of the program an entry starts, e.g. "Teams.exe", to find it running later.</summary>
    public static string? ProcessName(StartupEntry entry)
    {
        var (file, _) = CommandLine.Split(Environment.ExpandEnvironmentVariables(entry.Command));
        var name = WinPath.FileName(file);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? WinPath.FileNameWithoutExtension(name) : null;
    }

    /// <summary>Task Manager stores a binary flag per entry; an odd first byte means disabled.</summary>
    private static bool IsDisabled(IRegistry registry, string approvedSubKey, string name)
    {
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            var flags = registry.ReadBinary($@"{hive}\{ApprovedKey}\{approvedSubKey}", name);
            if (flags is { Length: > 0 } && (flags[0] & 1) == 1) return true;
        }
        return false;
    }
}

/// <summary>Services that can pause while gaming on the couch. Never anything the system needs.</summary>
public static class GamingServices
{
    /// <summary>Suggested for the couch: indexing, prefetching, printing, telemetry, error reports, maps, fax.</summary>
    public static IReadOnlyDictionary<string, string> Suggested => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WSearch"] = Strings.Get("svc.search"),
        ["SysMain"] = "SysMain (Superfetch)",
        ["Spooler"] = Strings.Get("svc.spooler"),
        ["DiagTrack"] = Strings.Get("svc.diagTrack"),
        ["WerSvc"] = Strings.Get("svc.wer"),
        ["MapsBroker"] = Strings.Get("svc.maps"),
        ["Fax"] = "Fax",
        ["PcaSvc"] = Strings.Get("svc.pca"),
        ["TabletInputService"] = Strings.Get("svc.tablet")
    };

    /// <summary>
    /// Whether a service may be paused at all. Only the known-harmless ones: the switch
    /// runs with admin rights and reads its list from a file the user can edit.
    /// </summary>
    public static bool IsAllowed(string name) => !string.IsNullOrWhiteSpace(name) && Suggested.ContainsKey(name);
}
