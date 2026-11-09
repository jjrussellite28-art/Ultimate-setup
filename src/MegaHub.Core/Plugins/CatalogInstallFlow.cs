namespace MegaHub.Core.Plugins;

/// <summary>
/// The order of the parents' code in a catalog install: the gate is checked and its grant used up exactly once, in the catalog service's approval
/// callback (the last gate, after the statement checked out and before the download). A skin of a catalog the user added may be looked at earlier
/// (<c>early</c>, so a missing code fails before any download is tracked), but that early look must only check, never consume: the callback asks
/// the same gate again, and a grant used up twice would leave the install in an endless ask loop.
/// </summary>
public static class CatalogInstallFlow
{
    /// <param name="extraSkin">The entry is a skin of an added catalog (third-party): it has an early look.</param>
    /// <param name="early">Looks at the gate without using the grant up (throws when refused).</param>
    /// <param name="gate">Checks the gate for the request and uses the grant up (throws when refused).</param>
    /// <param name="install">Runs the install with the approval callback the catalog service asks.</param>
    public static Task<T> RunAsync<T>(bool extraSkin, Action early, Action<PluginInstallRequest> gate, Func<Func<PluginInstallRequest, bool>, Task<T>> install)
    {
        if (extraSkin) early();
        return install(request =>
        {
            gate(request);
            return true;
        });
    }
}

/// <summary>What became of a plugin that was installed from the catalog and started right away.</summary>
public enum InstalledStart
{
    /// <summary>It runs now.</summary>
    Started,
    /// <summary>It was installed, but its start failed (the error is in the list).</summary>
    Failed,
    /// <summary>It was installed but stays off: the owner switched this folder off earlier and that choice outlives an uninstall (a reinstall must not lift it). It needs no restart, only switching on.</summary>
    SwitchedOff,
    /// <summary>It starts with the next start (an earlier run could not be stopped for sure, Offline mode, or nothing could be said).</summary>
    AtRestart
}

/// <summary>Reads the list lines of a folder after the host tried to start it now; the one place that decides whether the answer says "restart".</summary>
public static class InstalledStartRules
{
    public static InstalledStart Judge(IReadOnlyList<PluginInfo> infos)
    {
        if (infos.Any(i => i.Loaded)) return InstalledStart.Started;
        // Waiting for a restart wins over everything: that start is owed, whatever else the line says.
        if (infos.Any(i => i.NeedsRestartToStart || i.SkippedForOffline)) return InstalledStart.AtRestart;
        if (infos.Any(i => !i.Enabled && i.Error is null && !i.NeedsApproval && !i.RemovedAtRestart)) return InstalledStart.SwitchedOff;
        if (infos.FirstOrDefault(i => i.Error is not null) is { NeedsApproval: false }) return InstalledStart.Failed;
        return InstalledStart.AtRestart;
    }
}
