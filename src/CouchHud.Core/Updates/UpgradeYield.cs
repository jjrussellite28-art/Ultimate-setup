namespace CouchHud.Core.Updates;

/// <summary>What the HUD does next while an app upgrade runs.</summary>
public enum UpgradeYieldAction
{
    None,
    /// <summary>Step aside and bring <see cref="UpgradeYieldStep.Target"/> to the front.</summary>
    Yield,
    /// <summary>Nothing is waiting any more and the screen is still the upgrade's or the HUD's: the HUD comes back.</summary>
    Restore,
    /// <summary>Something waits for an answer, but the HUD does not own the screen (a game, PC mode, the browser): only say so, once.</summary>
    Note,
    /// <summary>Nothing is waiting any more, but someone else has the front now (an app the user started): the HUD stays where it is.</summary>
    Release
}

/// <summary>Whose window is in front: none, the HUD's own, one of the upgrade (the prompt, winget's children) or anyone else's.</summary>
public enum UpgradeForeground
{
    None,
    Hud,
    Upgrade,
    Other
}

/// <summary>One look at the screen while winget runs.</summary>
/// <param name="PromptWindow">A window of the elevation prompt in this session, 0 when there is none (or it shows none).</param>
/// <param name="PromptRunning">consent.exe runs in this session, with or without a window we can see.</param>
/// <param name="TreeWindows">Visible windows of processes that winget started (installers), biggest first.</param>
/// <param name="HudOwnsScreen">The HUD is what the couch sees: no game, no PC mode, no browser, no capture.</param>
/// <param name="Foreground">The window in front (0 for none).</param>
/// <param name="ForegroundKind">Whose it is, as far as the caller can tell.</param>
public readonly record struct UpgradeScene(
    long PromptWindow,
    bool PromptRunning,
    IReadOnlyList<long> TreeWindows,
    bool HudOwnsScreen = true,
    long Foreground = 0,
    UpgradeForeground ForegroundKind = UpgradeForeground.None);

public readonly record struct UpgradeYieldStep(UpgradeYieldAction Action, long Target, bool Prompt);

/// <summary>
/// The HUD covers the screen; an app upgrade may need an answer outside it: Windows' elevation prompt (consent.exe, which shows
/// only as a blinking taskbar button when the asking process is not in front, and there is no taskbar on the couch) or an
/// installer that opens a window despite <c>--silent</c>. Looked at twice a second while winget runs: when one of them is there
/// and the HUD owns the screen, the HUD steps aside for it (the prompt first, then the window it already stepped aside for as long
/// as that one is there, so two installer windows do not take turns). While a game runs, in PC mode or in the browser it never
/// steps aside: the HUD only says once that something waits. Once nothing has been there for <see cref="QuietTicks"/> looks in a
/// row (the gap between the prompt and the installer's window), the HUD comes back, only if it stepped aside, still owns the
/// screen and the front is empty, its own or the upgrade's; with someone else's window in front (an app the user started
/// meanwhile) it stays where it is. Pure, one caller.
/// </summary>
public sealed class UpgradeYield
{
    public const int QuietTicks = 3;
    private const int MaxKnown = 64;

    private bool _yielded;
    private long _target;
    private int _quiet;
    // The windows of this upgrade seen so far (a closed installer leaves the front to one of its own windows or to the HUD).
    private readonly HashSet<long> _known = [];

    public bool Yielded => _yielded;

    public UpgradeYieldStep Next(UpgradeScene scene)
    {
        Remember(scene.PromptWindow);
        foreach (var w in scene.TreeWindows) Remember(w);
        // The prompt first; else the window already in front for this upgrade while it is there; else the biggest one.
        var target = scene.PromptWindow != 0 ? scene.PromptWindow
            : _target != 0 && scene.TreeWindows.Contains(_target) ? _target
            : scene.TreeWindows.Count > 0 ? scene.TreeWindows[0] : 0;
        var waiting = scene.PromptRunning || target != 0;
        if (waiting)
        {
            _quiet = 0;
            if (target == 0) return new(UpgradeYieldAction.None, 0, scene.PromptRunning);
            var prompt = scene.PromptWindow != 0;
            // A game, PC mode, the browser: nothing is pushed over them, the note says where the question waits.
            if (!scene.HudOwnsScreen) return new(_yielded ? UpgradeYieldAction.None : UpgradeYieldAction.Note, target, prompt);
            // Someone else's window in front (an app the user started): the installer is not forced over it, the note says it waits.
            if (IsOthers(scene.Foreground, scene.ForegroundKind)) return new(_yielded ? UpgradeYieldAction.None : UpgradeYieldAction.Note, target, prompt);
            if (_yielded && target == _target) return new(UpgradeYieldAction.None, target, prompt);
            _yielded = true;
            _target = target;
            return new(UpgradeYieldAction.Yield, target, prompt);
        }
        if (!_yielded) return new(UpgradeYieldAction.None, 0, false);
        if (++_quiet < QuietTicks) return new(UpgradeYieldAction.None, 0, false);
        return Finish(scene.HudOwnsScreen, scene.Foreground, scene.ForegroundKind);
    }

    /// <summary>The run ended: back, if the HUD had stepped aside and nobody else took the front meanwhile.</summary>
    public UpgradeYieldStep End(bool hudOwnsScreen, long foreground, UpgradeForeground kind) => Finish(hudOwnsScreen, foreground, kind);

    /// <summary>
    /// Asked again on the UI thread at the moment a step happens, with the window in front right then: a Yield over someone else's
    /// window becomes a Note, a Restore over it a Release; everything else stays as decided.
    /// </summary>
    public UpgradeYieldStep Recheck(UpgradeYieldStep step, long foreground, UpgradeForeground kind)
    {
        if (!IsOthers(foreground, kind)) return step;
        return step.Action switch
        {
            UpgradeYieldAction.Yield => step with { Action = UpgradeYieldAction.Note },
            UpgradeYieldAction.Restore => step with { Action = UpgradeYieldAction.Release },
            _ => step
        };
    }

    // Someone else's window: not the HUD's, not empty, not one of this upgrade's (seen so far or recognised by the caller).
    private bool IsOthers(long foreground, UpgradeForeground kind) => kind == UpgradeForeground.Other && !(foreground != 0 && _known.Contains(foreground));

    private UpgradeYieldStep Finish(bool hudOwnsScreen, long foreground, UpgradeForeground kind)
    {
        var was = _yielded;
        _yielded = false;
        _target = 0;
        _quiet = 0;
        if (!was) return new(UpgradeYieldAction.None, 0, false);
        var ours = !IsOthers(foreground, kind);
        return new(hudOwnsScreen && ours ? UpgradeYieldAction.Restore : UpgradeYieldAction.Release, 0, false);
    }

    private void Remember(long window)
    {
        if (window != 0 && _known.Count < MaxKnown) _known.Add(window);
    }
}
