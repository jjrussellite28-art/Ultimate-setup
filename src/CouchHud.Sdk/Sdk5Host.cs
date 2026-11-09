namespace CouchHud.Sdk;

// SDK 5, additive: what a plugin may ask about Mega Hub itself, read only. Nothing here carries a handle or an action; a plugin that wants
// to do something goes through the members that have their own capability and their own gates. Older hosts answer with the defaults
// (null, false, nothing happens), so a plugin built against this must cope with that.

/// <summary>How Mega Hub runs on this PC.</summary>
public enum PluginShellMode
{
    /// <summary>Mega Hub is the Windows shell (full integration).</summary>
    Shell,

    /// <summary>Mega Hub runs as an ordinary app on top of the Windows desktop.</summary>
    App
}

/// <summary>The app or game that is running now, as far as the plugin may know it.</summary>
/// <param name="Id">The library id ("steam:1245620").</param>
/// <param name="Name">Plain text without control characters, at most <see cref="MaxName"/> characters.</param>
public sealed record PluginAppInfo(string Id, string Name)
{
    public const int MaxName = 100;
}

/// <summary>
/// Mega Hub's own state, read only (capability "host.state", <see cref="IPluginContext.Host"/>). Every property is read from the host at the
/// moment it is asked, so the state at the load of the plugin is readable at once; <see cref="Changed"/> says that something moved.
/// </summary>
public interface IPluginHostState
{
    /// <summary>Mega Hub's version, e.g. "0.7.0".</summary>
    string Version { get; }

    /// <summary>Shell (Mega Hub replaces the Windows shell) or app. All properties are read from one snapshot that is at most a quarter of a second old.</summary>
    PluginShellMode Mode { get; }

    /// <summary>The user is on the Windows desktop (PC mode).</summary>
    bool PcMode { get; }

    /// <summary>
    /// The game or app Mega Hub started and watches, or null. Null as well when the entry is hidden by the user or belongs to a feature
    /// area that is switched off (Settings › Console › Areas): such an entry is not part of what a plugin may know about.
    /// </summary>
    PluginAppInfo? RunningApp { get; }

    /// <summary>When the sleep timer ends (it shuts the PC down or puts it to sleep), or null when none runs.</summary>
    DateTimeOffset? SleepTimerEnds { get; }

    /// <summary>The couch is locked (same as <see cref="IPluginContext.Locked"/>).</summary>
    bool Locked { get; }

    /// <summary>
    /// Something in the state above changed. Changes that come close together are merged into one call, at most one call every quarter
    /// of a second; read the properties again. Called on a pool thread, not while the plugin is paused by Offline mode.
    /// </summary>
    event Action? Changed;
}
