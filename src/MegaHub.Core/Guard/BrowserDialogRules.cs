namespace MegaHub.Core.Guard;

/// <summary>Does a window belong to the couch browser's own process tree? <see cref="Unknown"/> means "could not be told".</summary>
public enum BrowserTree
{
    No,
    Yes,
    Unknown
}

/// <summary>What the native backstop does with a window that just appeared.</summary>
public enum BrowserDialogAction
{
    Leave,
    Close
}

/// <summary>
/// The native layer under the couch browser's script guard (see <see cref="BrowserGuard"/>): a Windows dialog box (open, save, print, color,
/// and the common item dialog, all class #32770) that appears in the web view's process tree while the couch rules apply is closed, whatever
/// the page did to get it. Pure rules; the Windows side (WinEvent hook, sweep, process tree) lives in the Shell project and only asks here.
/// </summary>
public static class BrowserDialogRules
{
    /// <summary>The window class of every standard dialog box, which is also what the file, folder, color and print dialogs are.</summary>
    public const string DialogClass = "#32770";

    /// <summary>
    /// The couch rules hold for the browser: the couch lock is on (also while it is opened for a moment), MegaHub is the shell, not PC mode,
    /// not the developer switch. Parental controls need the lock, so they are covered.
    /// </summary>
    public static bool Enforced(bool lockOn, bool pcMode, bool dev) => lockOn && !pcMode && !dev;

    /// <summary>
    /// Windows of the browser process that are no dialog and may stand on their own: IME and text services, tooltips, the message and
    /// GPU helper windows. (The browser's own popups, <c>Chrome_WidgetWin_*</c>, are not on it: they count only when owned by the host.)
    /// </summary>
    public static readonly IReadOnlyList<string> HelperClasses =
    [
        "Chrome_SystemMessageWindow", "Chrome_MessageWindow", "Intermediate D3D Window", "MSCTFIME UI", "IME", "CiceroUIWndFrame",
        "SysShadow", "Base_PowerMessageWindow", "tooltips_class32", "Chrome_RenderWidgetHostHWND"
    ];

    /// <summary>
    /// A window seen by the backstop. <paramref name="OwnedByHost"/>: its root owner is the HUD's window that holds the couch browser
    /// (GA_ROOTOWNER), so it is the pane's own popup or dialog parent chain, not something that stands alone.
    /// </summary>
    public sealed record BrowserWindow(string? Class, bool Visible, bool TopLevel, bool OwnedByHost);

    /// <summary>
    /// Close or leave a window. Only windows of the couch browser's process tree count; a window whose tree could not be told counts only
    /// when it belongs to a WebView2 process <i>and</i> its owner chain leads to the HUD's own window (fail closed for the couch browser,
    /// never for another program's WebView2, whose dialogs are left alone).
    /// Then: a dialog box (#32770) is closed whatever else is known. Any other visible top-level window is closed unless it is the pane's
    /// own (owned by the host window) or a known helper window; hidden windows and child windows are never touched (tooltips, the
    /// render widget, helper windows made visible later are met by the next event or the next sweep).
    /// </summary>
    public static BrowserDialogAction Decide(bool enforced, bool browserOpen, BrowserWindow window, string? processName, BrowserTree tree)
    {
        if (!enforced || !browserOpen) return BrowserDialogAction.Leave;
        var ours = tree switch
        {
            BrowserTree.Yes => true,
            BrowserTree.Unknown => LockedForeground.IsOwnWebViewHelper(processName) && window.OwnedByHost,
            _ => false
        };
        if (!ours) return BrowserDialogAction.Leave;
        if (string.Equals(window.Class, DialogClass, StringComparison.Ordinal)) return BrowserDialogAction.Close;
        if (!window.TopLevel || !window.Visible || window.OwnedByHost) return BrowserDialogAction.Leave;
        if (window.Class is { } c && HelperClasses.Contains(c, StringComparer.Ordinal)) return BrowserDialogAction.Leave;
        return BrowserDialogAction.Close;
    }

    /// <summary>
    /// The web view's browser process has no known id: the couch browser cannot be told from other programs, so after a short start
    /// phase it is ended instead of guarded blind.
    /// </summary>
    public static bool MustEndBlind(bool enforced, bool browserOpen, int? browserPid, TimeSpan blindFor, TimeSpan grace) =>
        enforced && browserOpen && browserPid is null && blindFor >= grace;

    /// <summary>
    /// Is the window's process the browser's, or does it descend from it? The browser's own process id comes first (the dialogs are made
    /// there), without reading any start time. <paramref name="browserPid"/> null (the pane is not running or the id cannot be read) is
    /// <see cref="BrowserTree.Unknown"/>. A process that cannot be inspected (<paramref name="readable"/> false) is Unknown, not No.
    /// </summary>
    public static BrowserTree Attribute(int pid, int? browserPid, bool readable, Func<int, int?> parentOf, Func<int, DateTime?> startOf)
    {
        if (browserPid is not { } root || root <= 0) return BrowserTree.Unknown;
        if (pid == root) return BrowserTree.Yes;
        if (ProcessTree.DescendsFrom(pid, p => p == root, parentOf, startOf)) return BrowserTree.Yes;
        return readable ? BrowserTree.No : BrowserTree.Unknown;
    }
}
