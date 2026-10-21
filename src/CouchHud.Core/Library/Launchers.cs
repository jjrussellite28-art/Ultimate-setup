using CouchHud.Core.Platform;

namespace CouchHud.Core.Library;

/// <summary>A game client (Steam, Epic …) that CouchHud starts in the background on the couch.</summary>
/// <param name="Process">Process name without .exe, to see whether it already runs.</param>
/// <param name="Arguments">Start options that keep it quiet (tray instead of a window), if the client has any.</param>
public sealed record GameLauncher(string Id, string Name, string Exe, string Process, string Arguments);

/// <summary>
/// Finds installed game clients. They keep games updated and many games need them
/// running, so on the couch they start with CouchHud, as quietly as each allows.
/// </summary>
public static class Launchers
{
    public static IReadOnlyList<GameLauncher> Find(IRegistry registry, Func<string, bool> exists)
    {
        var list = new List<GameLauncher>();

        void Add(string id, string name, string process, string args, params string?[] candidates)
        {
            var exe = candidates.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => Environment.ExpandEnvironmentVariables(c!.Trim().Trim('"')).Replace('/', '\\'))
                .FirstOrDefault(exists);
            if (exe is not null) list.Add(new GameLauncher(id, name, exe, process, args));
        }

        // Steam: -silent starts it in the tray without the library window.
        Add("steam", "Steam", "steam", "-silent",
            registry.ReadString(@"HKCU\Software\Valve\Steam", "SteamExe"),
            @"%ProgramFiles(x86)%\Steam\steam.exe");

        // Epic: -silent keeps the launcher in the tray.
        Add("epic", "Epic Games", "EpicGamesLauncher", "-silent",
            ExeFromCommand(registry.ReadString(@"HKLM\SOFTWARE\Classes\com.epicgames.launcher\shell\open\command", "")),
            @"%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
            @"%ProgramFiles%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");

        Add("gog", "GOG Galaxy", "GalaxyClient", "/runWithoutUpdating",
            Combine(registry.ReadString(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client"), "GalaxyClient.exe"),
            @"%ProgramFiles(x86)%\GOG Galaxy\GalaxyClient.exe");

        Add("ubisoft", "Ubisoft Connect", "upc", "",
            Combine(registry.ReadString(@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir"), "upc.exe"),
            @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\upc.exe");

        Add("ea", "EA app", "EADesktop", "",
            registry.ReadString(@"HKLM\SOFTWARE\Electronic Arts\EA Desktop", "DesktopAppPath"),
            registry.ReadString(@"HKLM\SOFTWARE\Electronic Arts\EA Desktop", "ClientPath"),
            @"%ProgramFiles%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe");

        Add("battlenet", "Battle.net", "Battle.net", "",
            @"%ProgramFiles(x86)%\Battle.net\Battle.net.exe",
            @"%ProgramFiles(x86)%\Battle.net\Battle.net Launcher.exe");

        return list;
    }

    public const string Standby = "standby";
    public const string OnDemand = "demand";
    public const string Off = "off";

    /// <summary>
    /// What CouchHud does with a client on the couch: "standby" (start it quietly with CouchHud so updates run and games find it, the
    /// default), "demand" (start it when a game or action needs it) or "off" (never). Older settings only knew on and off.
    /// </summary>
    public static string ModeOf(GameLauncher launcher, IReadOnlyDictionary<string, string> modes, IReadOnlyDictionary<string, bool>? legacy = null)
    {
        if (modes.TryGetValue(launcher.Id, out var mode) && mode is Standby or OnDemand or Off) return mode;
        return legacy is not null && legacy.TryGetValue(launcher.Id, out var on) && !on ? Off : Standby;
    }

    /// <summary>CouchHud starts clients only in full integration, not in PC mode and not in a dev run. Light mode never does.</summary>
    public static bool MayStartClients(bool full, bool pcMode, bool dev) => full && !pcMode && !dev;

    /// <summary>Starts with CouchHud at the couch start.</summary>
    public static bool StartsWithCouch(string mode) => mode == Standby;

    /// <summary>Is started by CouchHud when a game of it (or an action like opening it) needs it.</summary>
    public static bool StartsOnDemand(string mode) => mode is Standby or OnDemand;

    /// <summary>"C:\x\epic.exe" %1 → C:\x\epic.exe</summary>
    internal static string? ExeFromCommand(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : CommandLine.Split(command).File;

    private static string? Combine(string? dir, string file) =>
        string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir.Trim().Trim('"'), file);
}
