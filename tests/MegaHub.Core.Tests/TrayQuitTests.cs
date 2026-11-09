using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>
/// The tray icon of PC mode must not outlive PC mode: its "Quit" has no code check, so a locked couch could be quit without any code. A pin on the
/// sources of the HUD (the host is not started in tests): leaving PC mode takes the icon away, and the entry quits only while PC mode lasts.
/// </summary>
public class TrayQuitTests
{
    private static string Hud()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "MegaHub.Shell", "Hud.cs"));
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, from);
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, to);
        return text[a..b];
    }

    [Fact]
    public void Leaving_PC_mode_takes_the_tray_icon_away()
    {
        var leave = Slice(Hud(), "public Task LeavePcModeAsync()", "ShowHud();");
        var flag = leave.IndexOf("_pcMode = false;", StringComparison.Ordinal);
        var dispose = leave.IndexOf("DisposeTrayNow();", StringComparison.Ordinal);
        Assert.True(flag >= 0 && dispose > flag, "the icon is disposed as PC mode ends");
        Assert.Contains("_tray = null;", Slice(Hud(), "private void DisposeTrayNow()", "private void EnsureTray()"));
    }

    [Fact]
    public void The_quit_entry_of_the_tray_works_only_in_PC_mode_and_the_icon_comes_back_with_it()
    {
        var text = Hud();
        var ensure = Slice(text, "private void EnsureTray()", "_tray = tray;");
        Assert.Contains("(_, _) => { if (_pcMode) Quit(); }", ensure);
        Assert.DoesNotContain("(_, _) => Quit()", ensure);
        // A new icon is made when PC mode starts again (the field is null after leaving).
        Assert.Contains("if (_tray is not null) return;", ensure);
    }
}
