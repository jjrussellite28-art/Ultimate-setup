using CouchHud.Core.Platform;
using CouchHud.Core.Security;
using CouchHud.Core.Startup;

namespace CouchHud.Core.Tests;

public sealed class ShellSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-crash-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private CrashTracker Tracker() => new(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Clean_ends_never_count()
    {
        for (var i = 0; i < 10; i++)
        {
            var t = Tracker();
            Assert.False(t.RegisterStart());
            t.MarkClean();
        }
        Assert.Equal(0, Tracker().Consecutive());
    }

    [Fact]
    public void Three_unclean_ends_in_a_row_are_a_crash_loop_however_long_the_boots_take()
    {
        Assert.False(Tracker().RegisterStart());   // run 1 starts, never ends cleanly
        Assert.False(Tracker().RegisterStart());   // counts run 1
        Assert.False(Tracker().RegisterStart());   // counts run 2
        Assert.True(Tracker().RegisterStart());    // counts run 3
        Assert.Equal(0, Tracker().Consecutive());
        Assert.False(Tracker().RegisterStart());   // fresh start after the recovery
    }

    [Fact]
    public void A_healthy_run_or_a_clean_end_resets_the_count()
    {
        Tracker().RegisterStart();
        Tracker().RegisterStart();
        Assert.Equal(1, Tracker().Consecutive());
        Tracker().MarkHealthy();
        Assert.Equal(0, Tracker().Consecutive());

        Tracker().RegisterStart();
        Tracker().RegisterStart();
        var t = Tracker();
        t.MarkClean();
        Assert.False(Tracker().RegisterStart());
        Assert.Equal(0, Tracker().Consecutive());
    }

    [Fact]
    public void Guard_allows_two_restarts_and_stops_at_the_third_within_ten_minutes()
    {
        var restarts = new GuardRestarts(_dir, () => _now);
        Assert.True(restarts.TryRegister());
        _now = _now.AddMinutes(2);
        Assert.True(restarts.TryRegister());
        _now = _now.AddMinutes(2);
        Assert.True(restarts.TryRegister());
        _now = _now.AddMinutes(2);
        Assert.False(restarts.TryRegister());
        // Old restarts expire, a healthy run resets.
        _now = _now.AddMinutes(30);
        Assert.True(restarts.TryRegister());
        restarts.Reset();
        Assert.True(restarts.TryRegister());
    }

    [Fact]
    public void Emergency_and_quit_decisions()
    {
        Assert.True(ShellDecisions.EmergencyMayLock(installedAsShell: true));
        Assert.False(ShellDecisions.EmergencyMayLock(installedAsShell: false));
        Assert.True(ShellDecisions.StartDesktopOnQuit(desktopRunning: false));
        Assert.False(ShellDecisions.StartDesktopOnQuit(desktopRunning: true));
    }

    [Fact]
    public void RunOnce_bang_entries_are_deleted_only_after_a_successful_start()
    {
        var plain = new RunOnceEntry("Setup", "Setup", "x.exe", true, false);
        var bang = new RunOnceEntry("!Later", "Later", "y.exe", true, true);
        Assert.True(StartupCatalog.DeleteRunOnceBefore(plain));
        Assert.False(StartupCatalog.DeleteRunOnceBefore(bang));
        Assert.False(StartupCatalog.DeleteRunOnceAfter(plain, started: true));
        Assert.True(StartupCatalog.DeleteRunOnceAfter(bang, started: true));
        Assert.False(StartupCatalog.DeleteRunOnceAfter(bang, started: false));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\CouchHud\\CouchHud.exe\"", "C:\\Program Files\\CouchHud\\CouchHud.exe", true)]
    [InlineData("\"c:\\program files\\couchhud\\couchhud.exe\" --x", "C:\\Program Files\\CouchHud\\CouchHud.exe", true)]
    [InlineData("C:\\Tools\\CouchHud.exe", "C:\\Tools\\CouchHud.exe", true)]
    [InlineData("\"C:\\Program Files\\CouchHud-old\\CouchHud.exe\"", "C:\\Program Files\\CouchHud\\CouchHud.exe", false)]
    [InlineData("explorer.exe", "C:\\Program Files\\CouchHud\\CouchHud.exe", false)]
    [InlineData(null, "C:\\Program Files\\CouchHud\\CouchHud.exe", false)]
    public void Shell_command_must_match_the_exe_exactly(string? command, string exe, bool expected)
    {
        Assert.Equal(expected, ShellRegistration.PointsTo(command, exe));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\CouchHud\\CouchHud.exe\"", true)]
    [InlineData("C:\\x\\couchhud.exe -a", true)]
    [InlineData("C:\\couchhud\\other.exe", false)]
    [InlineData("explorer.exe", false)]
    public void Recognises_a_couchhud_command_anywhere(string command, bool expected)
    {
        Assert.Equal(expected, ShellRegistration.NamesCouchHud(command));
    }

    [Theory]
    [InlineData("C:\\Program Files\\CouchHud\\CouchHud.exe", "C:\\Program Files", true)]
    [InlineData("C:\\Program Files\\CouchHud\\CouchHud.exe", "C:\\Program Files\\", true)]
    [InlineData("C:\\Program Files (x86)\\CouchHud\\CouchHud.exe", "C:\\Program Files", false)]
    [InlineData("C:\\Program Files\\..\\Users\\k\\CouchHud.exe", "C:\\Program Files", false)]
    [InlineData("D:\\couchhud\\src\\bin\\Release\\CouchHud.exe", "C:\\Program Files", false)]
    public void Install_folder_check_is_a_real_prefix_check(string exe, string folder, bool expected)
    {
        Assert.Equal(expected, ShellRegistration.IsUnder(exe, folder));
    }

    [Fact]
    public void RunOnce_entries_skip_couchhud_and_honour_the_prefixes()
    {
        var registry = new FakeRegistry();
        registry.Values[StartupCatalog.RunOnceKeys[0].Key] = new()
        {
            ["Setup"] = "C:\\temp\\finish.exe /s",
            ["!Later"] = "C:\\temp\\later.exe",
            ["*Safe"] = "C:\\temp\\safe.exe",
            ["CouchHudRestoreShell"] = "\"C:\\Program Files\\CouchHud\\CouchHud.exe\" --restore-shell"
        };
        registry.Values[StartupCatalog.RunOnceKeys[1].Key] = new() { ["Machine"] = "C:\\temp\\machine.exe" };

        var entries = StartupCatalog.ReadRunOnce(registry);

        Assert.Equal(new[] { "Later", "Machine", "Safe", "Setup" }, entries.Select(e => e.Name).OrderBy(n => n).ToArray());
        Assert.True(entries.Single(e => e.Name == "Later").DeleteAfterRun);
        Assert.False(entries.Single(e => e.Name == "Setup").DeleteAfterRun);
        Assert.Equal("*Safe", entries.Single(e => e.Name == "Safe").ValueName);
        Assert.Single(entries, e => !e.CurrentUser);
    }
}
