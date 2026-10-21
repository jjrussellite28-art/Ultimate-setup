using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class BrowserGuardTests
{
    [Fact]
    public void DownloadsNeverGetTheBrowsersOwnUi()
    {
        Assert.Equal((true, true), BrowserGuard.Download(allowed: false));
        Assert.Equal((true, false), BrowserGuard.Download(allowed: true));
    }

    [Theory]
    [InlineData("print")]
    [InlineData("showOpenFilePicker")]
    [InlineData("showSaveFilePicker")]
    [InlineData("showDirectoryPicker")]
    [InlineData("type === 'file'")]
    [InlineData("showPicker")]
    public void ScriptCoversEveryWayToAWindowsDialog(string needle) =>
        Assert.Contains(needle, BrowserGuard.Script);

    [Fact]
    public void EveryNeutralizedApiIsInTheScript()
    {
        foreach (var api in BrowserGuard.NeutralizedApis.Where(a => !a.Contains('['))) Assert.Contains(api, BrowserGuard.Script);
    }
}
