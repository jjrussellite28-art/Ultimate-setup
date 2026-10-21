using System.Diagnostics;
using CouchHud.Privileged;

namespace CouchHud.Privileged.Tests;

/// <summary>
/// The edges to Windows: the programs are started by full path, the time limit is real, nothing is stopped that is not on the list.
/// Tests that start a program run on Windows only (the project is built everywhere, run where the programs are).
/// </summary>
public class PlatformEdgeTests
{
    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return new ProcessResult(0, "", "");
        }

        public int? StartDetached(string fileName, IReadOnlyList<string> arguments) => null;
    }

    private sealed class SilentEvents : IEventSink
    {
        public void Info(string message) { }

        public void Error(string message) { }
    }

    [Theory]
    [InlineData("schtasks.exe")]
    [InlineData(@"\schtasks.exe")]
    [InlineData(@"C:schtasks.exe")]
    [InlineData(@".\schtasks.exe")]
    [InlineData(@"..\Windows\System32\schtasks.exe")]
    [InlineData("")]
    public void The_launcher_refuses_a_program_that_is_not_given_by_its_full_path(string file)
    {
        var launcher = new ProcessLauncher();
        Assert.Throws<ArgumentException>(() => launcher.Run(file, [], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => launcher.StartDetached(file, []));
    }

    [Fact]
    public void The_scheduler_and_the_firewall_name_their_programs_by_the_system_folder()
    {
        var launcher = new RecordingLauncher();
        var scheduler = new SchtasksScheduler(launcher, new SilentEvents());
        scheduler.Run(@"CouchHud\S-1-5-21-1\Policy-lock-on");
        scheduler.Query("x");
        scheduler.Delete("x");
        scheduler.Create("x", @"C:\x.xml");
        scheduler.List();
        new WindowsFirewall(launcher, new SilentEvents()).Netsh(["advfirewall", "show"]);

        Assert.Equal(6, launcher.Calls.Count);
        foreach (var call in launcher.Calls) Assert.True(Path.IsPathFullyQualified(call.File), call.File);
        Assert.All(launcher.Calls.Take(5), c => Assert.Equal(System32("schtasks.exe"), c.File, ignoreCase: true));
        Assert.Equal(System32("netsh.exe"), launcher.Calls[5].File, ignoreCase: true);
        Assert.Equal(["/Run", "/TN", @"CouchHud\S-1-5-21-1\Policy-lock-on"], launcher.Calls[0].Args);
    }

    [Fact]
    public void A_program_that_runs_too_long_is_killed_and_the_call_returns()
    {
        if (!OperatingSystem.IsWindows()) return;
        var watch = Stopwatch.StartNew();
        // ping -n 40 takes about 40 seconds and prints as it goes; the old ReadToEnd would have waited for all of it.
        var result = new ProcessLauncher().Run(System32("ping.exe"), ["-n", "40", "127.0.0.1"], TimeSpan.FromSeconds(1));
        Assert.Equal(-1, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
    }

    [Fact]
    public void Output_and_the_exit_code_come_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = new ProcessLauncher().Run(System32("hostname.exe"), [], TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Environment.MachineName, result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_scm_stops_and_starts_only_services_on_the_list()
    {
        var scm = new WindowsScm();
        // Not on GamingServices: refused before the service control manager is even asked.
        Assert.False(scm.Stop("TermService", TimeSpan.FromSeconds(1)));
        Assert.False(scm.Start("TermService", TimeSpan.FromSeconds(1)));
        Assert.False(scm.Stop("CouchHud", TimeSpan.FromSeconds(1)));
        Assert.False(scm.Stop("Spooler & calc", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_failed_logon_names_no_sid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = new WindowsLogonVerifier().Verify("couchhud-no-such-user-" + Guid.NewGuid().ToString("N")[..8], ".", "wrong-" + Guid.NewGuid().ToString("N"));
        Assert.False(result.Succeeded);
        Assert.NotEqual(0, result.Error);
        Assert.Null(result.Sid);
    }
}
