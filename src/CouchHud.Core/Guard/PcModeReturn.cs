namespace CouchHud.Core.Guard;

/// <summary>What the Guide (Xbox) button does in PC mode.</summary>
public enum PcModeGuide
{
    /// <summary>Not in PC mode: the couch rules apply.</summary>
    NotPcMode,
    /// <summary>PC mode is still coming up (the desktop starts): the button waits, a return now would race the start.</summary>
    Wait,
    /// <summary>Back to the couch right away.</summary>
    Return,
    /// <summary>Back to the couch once the button is held (the person gave the tap to the Game Bar or keeps it for holding).</summary>
    ReturnOnHold
}

/// <summary>
/// PC mode and the way back. CouchHUD steps aside for the Windows desktop until the person returns: the Guide button (a tap, as on the
/// couch, unless the person gave the tap away in Settings › Controller › Guide button in game; holding always works), the tray icon,
/// or a restart. Leaving PC mode never weakens the couch lock: any unlock ends and the lock's policies, the foreground guard and the
/// Game Bar and Steam guards come back. Entering keeps its own gate (the couch lock). Pure, so it is tested without a desktop.
/// </summary>
public static class PcModeReturn
{
    /// <param name="pcMode">PC mode is on.</param>
    /// <param name="entering">PC mode is still being entered (the desktop is coming up).</param>
    /// <param name="guideTapIsOurs">The person's tap setting gives the Guide tap to CouchHUD (GuideTapInGame).</param>
    public static PcModeGuide OnGuide(bool pcMode, bool entering, bool guideTapIsOurs) =>
        !pcMode ? PcModeGuide.NotPcMode
        : entering ? PcModeGuide.Wait
        : guideTapIsOurs ? PcModeGuide.Return
        : PcModeGuide.ReturnOnHold;

    /// <summary>The text key of the hint shown on entering PC mode: how to come back.</summary>
    public static string NoticeKey(bool guideTapIsOurs) => guideTapIsOurs ? "pcMode.backTap" : "pcMode.backHold";
}
