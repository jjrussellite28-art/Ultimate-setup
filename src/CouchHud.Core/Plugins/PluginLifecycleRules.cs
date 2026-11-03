namespace CouchHud.Core.Plugins;

/// <summary>How the code of a plugin answered when it was told to stop.</summary>
public enum PluginStop
{
    /// <summary>It implements <see cref="IDisposable"/> (or <see cref="IAsyncDisposable"/>) and that returned in time: its own timers, tasks and connections are ended by the plugin itself.</summary>
    Clean,

    /// <summary>It has no way to be told (no <see cref="IDisposable"/>): the host cut it off, but what it started on its own may run on.</summary>
    NoDispose,

    /// <summary>Its stop did not return in time (a plugin that hangs while stopping); the host did not wait for it.</summary>
    TimedOut,

    /// <summary>Its stop threw.</summary>
    Failed,

    /// <summary>It loaded a native library of its own. That stays in the process until CouchHUD quits, so the folder does not start again before the restart.</summary>
    NativeModule
}

/// <summary>What the user does to a plugin.</summary>
public enum PluginHotAction
{
    /// <summary>A package was installed (catalog, file) or approved, or a withdrawn plugin was accepted: its folder is new to this run.</summary>
    Start,

    Disable,
    Uninstall,

    /// <summary>A newer package replaces the files of an installed plugin.</summary>
    Update
}

/// <summary>When a change may take effect without a restart.</summary>
public enum PluginHotOutcome
{
    /// <summary>At once, completely.</summary>
    Now,

    /// <summary>At once as far as CouchHUD is concerned (tab, pages, tools, events, host access are gone), but the plugin's own background work ends with the next start.</summary>
    CutOffRestartForRest,

    /// <summary>With the next start.</summary>
    AtRestart
}

/// <summary>
/// The rules for plugins without a restart, kept apart from the loader so they can be read (and tested) on their own:
/// <list type="bullet">
/// <item>Starting a folder that was never started in this run, or whose earlier run ended cleanly, is immediate. A folder whose earlier run could
/// not be stopped for sure waits for the restart: two copies of one plugin (two clients, two timers) must never run side by side.</item>
/// <item>Stopping is immediate for CouchHUD in every case (the plugin is cut off first, then asked to stop); it is complete only when the
/// plugin's own code confirmed it.</item>
/// <item>An update always waits for the restart: the old files are held by the running code, the old code cannot be proven gone in bounded
/// time, and an update that adds capabilities is a new approval, not a swap.</item>
/// </list>
/// </summary>
public static class PluginLifecycleRules
{
    /// <summary>Did the plugin's own code end its background work?</summary>
    public static bool FullyStopped(PluginStop stop) => stop == PluginStop.Clean;

    /// <summary>Decides one change. <paramref name="earlierRun"/> is how the last run of this folder in this process ended (null: it did not run).</summary>
    public static PluginHotOutcome Decide(PluginHotAction action, PluginStop? earlierRun, bool runningNow = false, PluginStop? thisStop = null) => action switch
    {
        PluginHotAction.Start => earlierRun is null || FullyStopped(earlierRun.Value) ? PluginHotOutcome.Now : PluginHotOutcome.AtRestart,
        PluginHotAction.Disable or PluginHotAction.Uninstall => !runningNow || thisStop is null || FullyStopped(thisStop.Value) ? PluginHotOutcome.Now : PluginHotOutcome.CutOffRestartForRest,
        _ => PluginHotOutcome.AtRestart
    };

    /// <summary>The worst answer of several plugin instances in one folder.</summary>
    public static PluginStop Worst(IEnumerable<PluginStop> stops)
    {
        var worst = PluginStop.Clean;
        foreach (var s in stops)
            if (Rank(s) > Rank(worst)) worst = s;
        return worst;
    }

    private static int Rank(PluginStop s) => s switch { PluginStop.Clean => 0, PluginStop.NativeModule => 1, PluginStop.NoDispose => 2, PluginStop.Failed => 3, _ => 4 };
}
