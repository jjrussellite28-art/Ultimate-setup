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
    /// Also start on a locked couch (the person at the controller is a guest). Off by default: an app needs the unlock, and so does its
    /// start as a companion. Even when on, only a program that could be a game at all is started (no shell, tool or browser).
    /// </summary>
    public bool WhenLocked { get; set; }
}
