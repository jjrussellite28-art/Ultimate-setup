using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class BrowserDialogRulesTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void RulesApplyOnlyUnderTheCouchLockOutsidePcModeAndDev(bool lockOn, bool pc, bool dev, bool expected) =>
        Assert.Equal(expected, BrowserDialogRules.Enforced(lockOn, pc, dev));

    private static BrowserDialogRules.BrowserWindow W(string? cls, bool visible = true, bool top = true, bool owned = false) => new(cls, visible, top, owned);

    private static BrowserDialogAction D(BrowserDialogRules.BrowserWindow w, BrowserTree tree, string? process = "msedgewebview2", bool enforced = true, bool open = true) =>
        BrowserDialogRules.Decide(enforced, open, w, process, tree);

    [Fact]
    public void ADialogOfTheBrowsersTreeIsClosedVisibleOrNot()
    {
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770"), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770", visible: false), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770", owned: true), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770"), BrowserTree.Yes, process: "SomethingElse"));
    }

    [Fact]
    public void ADialogOfAnotherTreeIsLeftToTheForegroundGuard()
    {
        // The HUD's own web view and a game: not ours to close from here.
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770"), BrowserTree.No));
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770"), BrowserTree.No, process: "Game"));
    }

    [Fact]
    public void AnUnknownTreeClosesOnlyAWebViewWindowOwnedByTheHud()
    {
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770", owned: true), BrowserTree.Unknown));
        Assert.Equal(BrowserDialogAction.Close, D(W("#32770", owned: true), BrowserTree.Unknown, process: "msedgewebview2.exe"));
        // Another program's WebView2 app (its dialog is not owned by the HUD), a game, a process we cannot name: left alone.
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770"), BrowserTree.Unknown));
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770", owned: true), BrowserTree.Unknown, process: "Game"));
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770", owned: true), BrowserTree.Unknown, process: null));
    }

    [Fact]
    public void AStandAloneVisibleWindowOfTheBrowserIsClosedNotJustDialogs()
    {
        // The PDF viewer's print window, a certificate chooser, a screen picker, a second browser window: not owned by the pane.
        Assert.Equal(BrowserDialogAction.Close, D(W("Chrome_WidgetWin_1"), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Close, D(W("SomeNewClass"), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Close, D(W(null), BrowserTree.Yes));
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("Chrome_WidgetWin_0")]
    [InlineData("SomeNewClass")]
    public void ThePanesOwnPopupsOwnedByTheHostStay(string cls) =>
        Assert.Equal(BrowserDialogAction.Leave, D(W(cls, owned: true), BrowserTree.Yes));

    [Fact]
    public void HiddenChildAndHelperWindowsStay()
    {
        Assert.Equal(BrowserDialogAction.Leave, D(W("Chrome_WidgetWin_1", visible: false), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Leave, D(W("Chrome_RenderWidgetHostHWND", top: false), BrowserTree.Yes));
        Assert.Equal(BrowserDialogAction.Leave, D(W("Chrome_WidgetWin_1", top: false), BrowserTree.Yes));
        foreach (var helper in BrowserDialogRules.HelperClasses) Assert.Equal(BrowserDialogAction.Leave, D(W(helper), BrowserTree.Yes));
    }

    [Fact]
    public void NothingIsClosedOutsideTheTreeWithoutTheRulesOrWithoutAnOpenBrowser()
    {
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770"), BrowserTree.Yes, enforced: false));
        Assert.Equal(BrowserDialogAction.Leave, D(W("#32770"), BrowserTree.Yes, open: false));
        Assert.Equal(BrowserDialogAction.Leave, D(W("SomeNewClass"), BrowserTree.Yes, enforced: false));
        Assert.Equal(BrowserDialogAction.Leave, D(W("SomeNewClass"), BrowserTree.No));
    }

    [Theory]
    [InlineData(true, true, null, 6, true)]
    [InlineData(true, true, null, 2, false)]
    [InlineData(true, true, 100, 60, false)]
    [InlineData(false, true, null, 60, false)]
    [InlineData(true, false, null, 60, false)]
    public void AWebViewWithoutAProcessIdIsEndedAfterTheStartPhase(bool enforced, bool open, int? pid, int seconds, bool ends) =>
        Assert.Equal(ends, BrowserDialogRules.MustEndBlind(enforced, open, pid, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(5)));

    [Fact]
    public void TheBrowserProcessItselfNeedsNoStartTime()
    {
        var tree = BrowserDialogRules.Attribute(100, 100, readable: false, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        Assert.Equal(BrowserTree.Yes, tree);
    }

    [Fact]
    public void ADescendantOfTheBrowserIsInItsTree()
    {
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 1 };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = new Dictionary<int, DateTime> { [100] = t0, [200] = t0.AddSeconds(1), [300] = t0.AddSeconds(2), [1] = t0.AddHours(-1) };
        var tree = BrowserDialogRules.Attribute(300, 100, true, p => parents.TryGetValue(p, out var q) ? q : null, p => starts.TryGetValue(p, out var s) ? s : null);
        Assert.Equal(BrowserTree.Yes, tree);
    }

    [Fact]
    public void AReadableProcessOutsideTheTreeIsNo()
    {
        var parents = new Dictionary<int, int> { [300] = 50, [50] = 1 };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tree = BrowserDialogRules.Attribute(300, 100, true, p => parents.TryGetValue(p, out var q) ? q : null, _ => t0);
        Assert.Equal(BrowserTree.No, tree);
    }

    [Fact]
    public void AnUnreadableProcessOutsideTheTreeIsUnknownNotNo()
    {
        var tree = BrowserDialogRules.Attribute(300, 100, readable: false, _ => null, _ => null);
        Assert.Equal(BrowserTree.Unknown, tree);
    }

    [Fact]
    public void WithoutABrowserProcessIdNothingCanBeAttributed()
    {
        Assert.Equal(BrowserTree.Unknown, BrowserDialogRules.Attribute(300, null, true, _ => null, _ => null));
        Assert.Equal(BrowserTree.Unknown, BrowserDialogRules.Attribute(300, 0, true, _ => null, _ => null));
    }

    [Fact]
    public void TheOldProcessIdOfAClosedBrowserIsReusedNotTrusted()
    {
        // A parent that started after its child is a reused pid: not in the tree (and the process is readable).
        var parents = new Dictionary<int, int> { [300] = 100 };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = new Dictionary<int, DateTime> { [100] = t0.AddHours(1), [300] = t0 };
        var tree = BrowserDialogRules.Attribute(300, 100, true, p => parents.TryGetValue(p, out var q) ? q : null, p => starts.TryGetValue(p, out var s) ? s : null);
        Assert.Equal(BrowserTree.No, tree);
    }
}
