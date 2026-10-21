using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

public class LaunchWindowPickerTests
{
    private static WindowInfo W(long h, string exe, string title, int z, bool min = false, long area = 1000) =>
        new(h, 1, exe, title, min, z, area);

    [Theory]
    [InlineData("https://www.youtube.com/x", "https")]
    [InlineData("STEAM://rungameid/5", "steam")]
    [InlineData(@"C:\game.exe", null)]
    [InlineData("notepad", null)]
    [InlineData("", null)]
    public void Scheme_is_read_from_target(string target, string? scheme) => Assert.Equal(scheme, LaunchWindowPicker.SchemeOf(target));

    [Fact]
    public void Exe_name_strips_path_and_quotes() =>
        Assert.Equal("msedge.exe", LaunchWindowPicker.ExeNameOf("\"C:\\Program Files\\Edge\\msedge.exe\""));

    [Fact]
    public void New_window_wins_over_reuse()
    {
        var before = new[] { W(1, "msedge.exe", "a", 0) };
        var after = new[] { W(1, "msedge.exe", "b", 0), W(2, "other.exe", "n", 1, area: 5000) };
        var m = LaunchWindowPicker.Choose(before, after, "msedge.exe", false);
        Assert.Equal(WindowMatchKind.New, m.Kind);
        Assert.Equal(2, m.Window!.Handle);
    }

    [Fact]
    public void Changed_title_of_handler_window_is_reused()
    {
        var before = new[] { W(1, "msedge.exe", "a", 0), W(2, "msedge.exe", "c", 1, min: true) };
        var after = new[] { W(1, "msedge.exe", "a", 0), W(2, "msedge.exe", "YouTube", 1, min: true) };
        var m = LaunchWindowPicker.Choose(before, after, "MSEDGE.EXE", false);
        Assert.Equal(WindowMatchKind.Reused, m.Kind);
        Assert.Equal(2, m.Window!.Handle);
    }

    [Fact]
    public void Unchanged_windows_only_count_when_allowed_and_topmost_wins()
    {
        var list = new[] { W(1, "chrome.exe", "a", 3), W(2, "msedge.exe", "b", 5), W(3, "msedge.exe", "c", 2) };
        Assert.Equal(WindowMatchKind.None, LaunchWindowPicker.Choose(list, list, "msedge.exe", false).Kind);
        var m = LaunchWindowPicker.Choose(list, list, "msedge.exe", true);
        Assert.Equal(3, m.Window!.Handle);
    }

    [Fact]
    public void No_handler_window_means_none()
    {
        var list = new[] { W(1, "chrome.exe", "a", 0) };
        Assert.Equal(WindowMatchKind.None, LaunchWindowPicker.Choose(list, list, "msedge.exe", true).Kind);
        Assert.Equal(WindowMatchKind.None, LaunchWindowPicker.Choose(list, list, null, true).Kind);
    }

    [Theory]
    [InlineData("https", true, true)]
    [InlineData("http", true, true)]
    [InlineData("https", false, false)]
    [InlineData("steam", true, false)]
    [InlineData(null, true, false)]
    public void Only_web_entries_are_maximized_and_only_with_the_option(string? scheme, bool option, bool maximize)
    {
        var window = new WindowInfo(7, 4242, "msedge.exe", "t", false, 0, 1000);
        var plan = LaunchWindowPicker.PlanActivation(window, scheme, option);
        Assert.Equal(maximize, plan.Maximize);
        // The foreground right goes to the process that owns the window, whatever the scheme.
        Assert.Equal(4242, plan.AllowPid);
    }
}
