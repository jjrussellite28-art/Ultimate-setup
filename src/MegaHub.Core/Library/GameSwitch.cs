using CouchHud.Sdk;

namespace MegaHub.Core.Library;

/// <summary>What a start from the HUD does while a game may be running.</summary>
public enum GameSwitchAction
{
    /// <summary>Nothing in the way: start as always.</summary>
    Launch,
    /// <summary>This game runs already: bring it back to the front instead of starting it again.</summary>
    Resume,
    /// <summary>Another game runs and nobody said yes to closing it: ask first (the console's "X will be closed").</summary>
    Confirm,
    /// <summary>The person said yes to closing exactly the game that runs: close it, then start the new one.</summary>
    Switch,
    /// <summary>Not now: see <see cref="GameSwitchDecision.Reason"/>.</summary>
    Refuse
}

public enum GameSwitchRefusal
{
    None,
    /// <summary>The game the question named is no longer the one that runs (it ended, or another took its place): ask again.</summary>
    Changed,
    /// <summary>With parental controls a second start while one is still starting waits (as before: "close the game first").</summary>
    StartPending,
    /// <summary>With parental controls nothing else starts beside a running game (as before: "close the game first").</summary>
    GameRunning,
    /// <summary>A switch is under way already.</summary>
    Busy
}

public sealed record GameSwitchDecision(GameSwitchAction Action, GameSwitchRefusal Reason = GameSwitchRefusal.None);

/// <summary>
/// Console-style game switching: one game at a time from the HUD. Starting the running game resumes it; starting another one asks whether
/// the running one may be closed, and only a yes that names the game still running counts (no stale question closes a game that took its
/// place). Apps and websites start beside a game as before (with parental controls: not at all). Pure; the closing itself is <see cref="Guard.SafeTermination"/>'s, limited to
/// the processes the session tracker noted for the game.
/// </summary>
public static class GameSwitch
{
    /// <param name="target">What the person wants to start.</param>
    /// <param name="current">The running game session, if any.</param>
    /// <param name="startPending">A start is under way whose game has not shown up yet.</param>
    /// <param name="parental">Parental controls are on.</param>
    /// <param name="confirmedFrom">The id of the game the person agreed to close, or null when nobody was asked.</param>
    /// <param name="switching">A switch is running right now.</param>
    public static GameSwitchDecision Decide(LibraryItem target, GameSession? current, bool startPending, bool parental, string? confirmedFrom, bool switching = false)
    {
        if (switching) return new(GameSwitchAction.Refuse, GameSwitchRefusal.Busy);
        if (current is null)
        {
            // The game the question was about ended meanwhile: nothing to close, the start just goes ahead (unless parental controls wait).
            if (startPending && parental) return new(GameSwitchAction.Refuse, GameSwitchRefusal.StartPending);
            return new(GameSwitchAction.Launch);
        }
        if (current.Item.Id == target.Id || GameSessionTracker.SameGame(current.Item, target)) return new(GameSwitchAction.Resume);
        // An app or a website does not replace the game: it starts beside it, except with parental controls (one thing at a time, as before).
        if (target.Type != ItemType.Game)
            return parental ? new(GameSwitchAction.Refuse, GameSwitchRefusal.GameRunning) : new(GameSwitchAction.Launch);
        if (confirmedFrom is null) return new(GameSwitchAction.Confirm);
        return confirmedFrom == current.Item.Id
            ? new(GameSwitchAction.Switch)
            : new(GameSwitchAction.Refuse, GameSwitchRefusal.Changed);
    }
}
