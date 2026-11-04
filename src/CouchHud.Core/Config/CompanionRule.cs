namespace CouchHud.Core.Config;

/// <summary>
/// One companion app: a library app that starts next to a game. It is named by its library id only: there is no path, no argument and
/// no program name in a rule, so a rule cannot start anything the library does not already offer as an app.
/// </summary>
public sealed class CompanionRule
{
    public const string ModeAll = "all";
    public const string ModeGames = "games";

    /// <summary>Library id of the app ("custom:wand").</summary>
    public string AppId { get; set; } = "";

    /// <summary>"all" (with every game) or "games" (with the games in <see cref="GameIds"/>). Anything else counts as "games".</summary>
    public string Mode { get; set; } = ModeGames;

    /// <summary>Library ids of the games the app starts with (mode "games").</summary>
    public List<string> GameIds { get; set; } = [];

    /// <summary>End the app when the game ends. Only a process CouchHUD started itself and that is still the same one (pid and start time).</summary>
    public bool CloseWithGame { get; set; }

    /// <summary>Start minimized and without taking the focus. A window that takes it anyway is minimized again.</summary>
    public bool Minimized { get; set; } = true;

    /// <summary>
    /// SHA-256 (lower-case hex) of the program file when the rule was set up for a program whose signature was checked (Wand). Set by the host,
    /// never by the page. At the start a different hash makes the signature check run again on the held file; empty means no signature rule.
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// SHA-256 (lower-case hex) the parent confirmed for the locked couch (any program, not only Wand). Set by the host when "also when locked"
    /// is turned on or confirmed again, never by the page. It gates starts on a locked couch only: an unlocked start does not look at it, so a
    /// program that updates itself keeps starting for adults. A rule with a <see cref="Sha256"/> (signature rule) does not need it.
    /// </summary>
    public string LockedSha256 { get; set; } = "";

    /// <summary>
    /// The parent confirmed the program although its folder (or the folder it starts in, or a folder above) can be changed by others, and
    /// chose to allow it anyway. Set by the host at confirm time only, never by the page's save; it is part of the confirmed digest, so
    /// editing it by hand breaks the pin. Without it a start on the locked couch is skipped while the folder is writable.
    /// </summary>
    public bool AllowWritableFolder { get; set; }

    /// <summary>
    /// Also start on a locked couch (the person at the controller is a guest). Off by default: an app needs the unlock, and so does its
    /// start as a companion. Even when on, only a program that could be a game at all is started (no shell, tool or browser).
    /// </summary>
    public bool WhenLocked { get; set; }
}
