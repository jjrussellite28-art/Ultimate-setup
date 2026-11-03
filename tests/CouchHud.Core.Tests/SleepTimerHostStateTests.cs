using System.Text.RegularExpressions;

namespace CouchHud.Core.Tests;

/// <summary>
/// The sleep timer's end reaches the plugins that hold "host.state" (IPluginHostState.SleepTimerEnds): the page reports every start, change
/// and end of its timer (TV's or phone's) through sleepTimerState, and the host passes that on to the plugin manager. The manager's side is
/// tested in PluginSdkHostTests; these pins keep the wiring from being dropped.
/// </summary>
public sealed class SleepTimerHostStateTests
{
    private static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx")))
                return File.ReadAllText(Path.Combine([dir.FullName, .. parts])).Replace("\r\n", "\n");
        throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Every_report_of_the_timer_sets_the_end_the_plugins_read()
    {
        var remote = Source("src", "CouchHud.Shell", "HudRemote.cs");
        var body = Regex.Match(remote, @"public void SetSleepTimerState\(.*?\n    }\n", RegexOptions.Singleline).Value;
        Assert.NotEmpty(body);
        // After the state is settled (PC mode ends a late report), before the phone channel is told.
        Assert.Matches(@"_sleepTimerState = null;\s*Emit\(""remote"".*?plugins\.SleepTimerEnds = _sleepTimerState is \{ \} running \? DateTimeOffset\.FromUnixTimeMilliseconds\(running\.At\) : null;.*?_remote\.Channel\?\.Publish\(""sleepTimer""\);", body.Replace("\n", " ").Replace("  ", " "));
    }

    [Fact]
    public void The_page_reports_start_and_end_of_the_timer_to_the_host()
    {
        var power = Source("ui", "core", "power.js");
        Assert.Contains("api.sleepTimerState?.({ at: sleepTimer.at, action: sleepTimer.action, token: sleepTimer.token })", power);
        // Started and stopped timers both report (start at the end of startSleepTimer, stop when one was running).
        Assert.Matches(@"sleepTimer\.handle = setTimeout\(fireSleepTimer, ms\);\s*reportSleepTimer\(\);", power);
        Assert.Contains("if (wasRunning) reportSleepTimer();", power);
    }
}
