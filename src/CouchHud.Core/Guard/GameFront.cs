namespace CouchHud.Core.Guard;

/// <summary>Why the HUD wants to come to the front.</summary>
public enum HudFrontRequest
{
    /// <summary>The person asked for it: the Guide button, the phone's Guide, the tray icon, leaving PC mode or the mouse mode.</summary>
    Person,
    /// <summary>The foreground guard minimized a client or a nuisance and would like the HUD back over the gap.</summary>
    GuardRecovery,
    /// <summary>A client that started on standby showed a window; the HUD covers it again.</summary>
    LauncherStandby
}

/// <summary>How handing the front to the game went.</summary>
public enum GameFocusResult
{
    InFront,
    /// <summary>The game shows no window that could take the front (still loading, tray only).</summary>
    NoWindow,
    /// <summary>Windows did not hand over the front.</summary>
    Refused
}

/// <summary>What the hand-over of a new game after a switch does at one look (see <see cref="GameFront.NewGameStep"/>).</summary>
public enum NewGameFrontStep
{
    /// <summary>Nothing to do now, look again.</summary>
    Wait,
    /// <summary>Hand the front to the game now.</summary>
    Activate,
    /// <summary>Give up: the person came back to the HUD, or the rules hold the game.</summary>
    Stop
}

/// <summary>What the HUD does at one look after the person came back to it while a new game was still starting (see <see cref="GameFront.WatchStep"/>).</summary>
public enum NewGameWatchStep
{
    Wait,
    /// <summary>The new game's first window took the front from the HUD the person came back to: the HUD takes it back, once.</summary>
    TakeBack,
    Stop
}

/// <summary>What is going on when something wants the HUD in front.</summary>
/// <param name="GameRunning">A game runs, is frozen or is starting (the guard's <see cref="ForegroundContext.GameRunning"/>).</param>
public sealed record HudFrontState(bool GameRunning, bool MouseMode = false, bool PcMode = false, bool Browsing = false, bool Suspended = false)
{
    /// <summary>The same facts as the foreground guard sees them.</summary>
    public static HudFrontState From(ForegroundContext c) => new(c.GameRunning, c.MouseMode, c.PcMode, c.Browsing, c.Suspended);
}

/// <summary>A top-level window of the running game, as Windows reports it (filled in the shell, judged here).</summary>
/// <param name="Width">Width of the window's normal place in pixels: for a minimized window the place it comes back to, not the icon.</param>
/// <param name="ZOrder">Position in the window list, front first.</param>
/// <param name="Cloaked">Hidden by the compositor (a UWP frame that is not shown, a window on another virtual desktop).</param>
/// <param name="ToolWindow">A tool window (WS_EX_TOOLWINDOW): overlays, tray helpers, never the game's main window.</param>
/// <param name="Owned">Owned by another window (a dialog of the game).</param>
public sealed record GameWindow(long Handle, int Pid, bool Visible, bool Cloaked, bool ToolWindow, bool Owned, bool Minimized, int Width, int Height, bool Titled, int ZOrder)
{
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// Who belongs in front while a game runs. CouchHUD is the boss of the couch, but once the person has gone back into the game (Resume, Alt+Tab,
/// the game's own activation) the game keeps the screen until the person asks for the HUD again (Guide button). Automatic attempts to bring
/// the HUD forward (the guard's recovery after it minimized a client, the standby clients' windows) never cover a running or starting game.
/// Pure, so the rules are tested without windows.
/// </summary>
public static class GameFront
{
    /// <summary>Smaller than this (in either direction) a window is a helper, a splash remnant or a 1×1 message window, not the game.</summary>
    public const int MinWidth = 100, MinHeight = 60;

    /// <summary>May the HUD take the front for this request now?</summary>
    public static bool HudMayTakeFront(HudFrontRequest request, HudFrontState state)
    {
        // The person's own wish always counts (the Guide button is the way back from a game, by design).
        if (request == HudFrontRequest.Person) return true;
        // Never over a game, a launcher the person is signing in to (mouse mode), the desktop, the couch browser or a capture.
        return !(state.GameRunning || state.MouseMode || state.PcMode || state.Browsing || state.Suspended);
    }

    /// <summary>
    /// One look of the hand-over to the game a switch has started. The person coming back to the HUD (any way) ends it first; a session that is
    /// not the new game yet (still starting, or another one) is waited for; then the gate (parental rules, protected profile) decides, and only
    /// a game with a window is activated. <paramref name="screenElsewhere"/>: PC mode, the couch browser or the mouse mode owns the screen
    /// (<see cref="HudFrontState"/>), which also ends it. Not seen: a window the person activates themselves (Alt+Tab, a click). Nothing is changed before the answer is <see cref="NewGameFrontStep.Activate"/>.
    /// </summary>
    public static NewGameFrontStep NewGameStep(bool personCameBack, bool sessionIsNewGame, bool gateRefuses, bool hasWindow, bool screenElsewhere = false)
    {
        if (personCameBack) return NewGameFrontStep.Stop;
        // The person went to the desktop (PC mode), the couch browser or the controller mouse: the game is not pulled over it.
        if (screenElsewhere) return NewGameFrontStep.Stop;
        if (!sessionIsNewGame) return NewGameFrontStep.Wait;
        if (gateRefuses) return NewGameFrontStep.Stop;
        return hasWindow ? NewGameFrontStep.Activate : NewGameFrontStep.Wait;
    }

    /// <summary>
    /// After the person came back to the HUD during the hand-over the game is never brought forward again, but its first window may take the
    /// front by itself (it was granted the foreground when it was started). Counter-move, narrow: only for the new game's own processes, only
    /// within <see cref="TakeBackWindow"/> after its first window was seen, only once, never over PC mode, the couch browser or the mouse mode.
    /// </summary>
    public static NewGameWatchStep WatchStep(bool screenElsewhere, bool sessionIsNewGame, bool hasWindow, TimeSpan sinceFirstWindow, bool gameInFront)
    {
        if (screenElsewhere) return NewGameWatchStep.Stop;
        if (!sessionIsNewGame || !hasWindow) return NewGameWatchStep.Wait;
        if (sinceFirstWindow > TakeBackWindow) return NewGameWatchStep.Stop;
        return gameInFront ? NewGameWatchStep.TakeBack : NewGameWatchStep.Wait;
    }

    /// <summary>How long after the new game's first window it may still be taken back from.</summary>
    public static readonly TimeSpan TakeBackWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The window to hand the front to when the person resumes the game: visible, not cloaked, no tool window, at least
    /// <see cref="MinWidth"/>×<see cref="MinHeight"/> at its normal place (so a minimized full-screen game counts with its real size),
    /// of one of the game's processes. Unowned and titled windows first, then the biggest, then the one further in front. Null when the
    /// game shows no such window (still loading, or only a tray icon).
    /// </summary>
    public static GameWindow? PickWindow(IEnumerable<GameWindow> windows, IReadOnlyCollection<int> gamePids)
    {
        var candidates = windows
            .Where(w => w.Handle != 0 && w.Pid > 0 && gamePids.Contains(w.Pid) && w.Visible && !w.Cloaked && !w.ToolWindow && w.Width >= MinWidth && w.Height >= MinHeight)
            .ToList();
        return candidates
            .OrderBy(w => w.Owned)
            .ThenByDescending(w => w.Titled)
            .ThenByDescending(w => w.Area)
            .ThenBy(w => w.ZOrder)
            .FirstOrDefault();
    }
}
