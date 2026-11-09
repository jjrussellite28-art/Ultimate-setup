using MegaHub.Core.Config;
using CouchHud.Sdk;

namespace MegaHub.Core.Plugins;

// SDK 5, additive, on the host's side: "is this service allowed now" with its change event, and Mega Hub's read-only state for plugins
// with the capability "host.state". Nothing here hands a plugin a handle or lets it do anything; it only answers questions.

/// <summary>What the shell reads about itself for plugins, before the host adds the sleep timer. Plain data.</summary>
public sealed record PluginHostSnapshot(string Version, PluginShellMode Mode, bool PcMode, PluginAppInfo? RunningApp, DateTimeOffset? SleepTimerEnds = null);

/// <summary>The rules for what of the running app a plugin may know. Pure.</summary>
public static class PluginHostRules
{
    /// <summary>
    /// The running entry as a plugin sees it: null when there is none, when the user hid it, or when its feature area is switched off
    /// (<paramref name="areas"/>); otherwise the id and the name cleaned like any plugin text (no control characters, at most
    /// <see cref="PluginAppInfo.MaxName"/> characters). A game Mega Hub does not list is not described either.
    /// </summary>
    public static PluginAppInfo? RunningApp(LibraryItem? item, AreaSet areas)
    {
        if (item is null || item.Hidden || !areas.Shows(item)) return null;
        return Clean(item.Id, item.Name);
    }

    /// <summary>
    /// The event a plugin gets for a game that started or ended: null (no event) for an entry the user hid or whose area is switched off,
    /// otherwise id and name cleaned like <see cref="RunningApp"/>; the install folder stays as it always was (a path, for compatibility).
    /// A child's profile is not filtered separately: a title the profile allows is visible to the plugins.
    /// </summary>
    public static GameEvent? EventFor(LibraryItem item, AreaSet areas, double minutes, int? processId)
    {
        if (RunningApp(item, areas) is not { } app) return null;
        return new GameEvent(app.Id, app.Name, item.InstallDir, minutes) { ProcessId = processId };
    }

    /// <summary>The id and name cleaned; null when nothing readable is left.</summary>
    public static PluginAppInfo? Clean(string? id, string? name)
    {
        var cleanId = Plain(id, 200);
        var cleanName = Plain(name, PluginAppInfo.MaxName);
        return cleanId.Length == 0 || cleanName.Length == 0 ? null : new PluginAppInfo(cleanId, cleanName);
    }

    private static string Plain(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }
}

public sealed partial class PluginManager
{
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _serviceSeen = new(StringComparer.OrdinalIgnoreCase);

    private Context NewContext(string id, string name, PluginGrant grant, PluginLife life)
    {
        var context = new Context(this, id, name, grant, life);
        lock (_gate) _contexts[id] = context;
        RecordServiceState(id);
        return context;
    }

    /// <summary>The plugin was uninstalled: the host keeps no context of it, so no event reaches it any more.</summary>
    internal void ForgetContext(string pluginId)
    {
        lock (_gate) _contexts.Remove(pluginId);
        lock (_serviceSeen)
        {
            var prefix = pluginId.ToLowerInvariant() + "/";
            foreach (var key in _serviceSeen.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) _serviceSeen.Remove(key);
        }
    }

    // ---------------------------------------------------------------- ServiceAllowed

    private static bool HasNetworkCapability(PluginGrant grant) =>
        grant.Capabilities.Contains(PluginCapabilities.NetworkInternet) || grant.Capabilities.Contains(PluginCapabilities.NetworkLan);

    /// <summary>
    /// May this declared service go out now (see <see cref="IPluginContext.ServiceAllowed"/>)? Fail closed: false for an id that is empty, not
    /// declared or not approved, for a plugin that is not loaded or has no network capability, while Offline is on, the service's switch is off
    /// or the plugin is paused, and when the shell's answer throws.
    /// </summary>
    public bool ServiceAllowedFor(string pluginId, string? serviceId)
    {
        if (string.IsNullOrEmpty(serviceId) || GrantOf(pluginId) is not { } grant || !HasNetworkCapability(grant)) return false;
        if (grant.Services?.Any(s => s.Id == serviceId) != true) return false;
        try
        {
            return ServiceOpen(pluginId, serviceId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] {pluginId}: service state unreadable ({ex.GetType().Name}), counted as off");
            return false;
        }
    }

    private bool RecordServiceState(string pluginId)
    {
        var any = false;
        foreach (var s in GrantOf(pluginId)?.Services ?? [])
        {
            var on = ServiceAllowedFor(pluginId, s.Id);
            any |= on;
            lock (_serviceSeen) _serviceSeen[PluginService.Key(pluginId, s.Id)] = on;
        }
        return any;
    }

    /// <summary>
    /// The shell calls this when Offline mode or a service switch changed: every loaded plugin whose services' answers changed is told
    /// (<see cref="IPluginContext.ServiceAllowedChanged"/>, also when it is paused: that is when it has to stop). A plugin whose answers
    /// stayed the same hears nothing.
    /// </summary>
    public void NotifyServicesChanged()
    {
        List<(string Id, Context Context)> all;
        lock (_gate) all = [.. _contexts.Select(c => (c.Key, c.Value))];
        foreach (var (id, context) in all)
        {
            var services = GrantOf(id)?.Services ?? [];
            if (services.Count == 0) continue;
            var changed = false;
            var any = false;
            foreach (var s in services)
            {
                var on = ServiceAllowedFor(id, s.Id);
                any |= on;
                var key = PluginService.Key(id, s.Id);
                lock (_serviceSeen)
                {
                    if (!_serviceSeen.TryGetValue(key, out var seen) || seen != on) changed = true;
                    _serviceSeen[key] = on;
                }
            }
            if (changed) context.RaiseServiceAllowedChanged(any);
        }
    }

    // ---------------------------------------------------------------- host.state

    /// <summary>Set by the shell: reads version, mode, PC mode and the running app from the live state. Null: neutral defaults (app mode, nothing running).</summary>
    public Func<PluginHostSnapshot>? HostStateReader { get; set; }

    private DateTimeOffset? _sleepTimerEnds;

    /// <summary>The sleep timer ends then (null: none). The shell sets it when a timer starts, changes or ends; a time in the past reads as none.</summary>
    public DateTimeOffset? SleepTimerEnds
    {
        get
        {
            lock (_hostGate) return _sleepTimerEnds is { } end && end > DateTimeOffset.UtcNow ? end : null;
        }
        set
        {
            lock (_hostGate)
            {
                if (_sleepTimerEnds == value) return;
                _sleepTimerEnds = value;
            }
            NotifyHostChanged();
        }
    }

    /// <summary>The state a plugin with "host.state" reads right now: the shell's answer, cleaned, plus the sleep timer. A reader that fails gives the neutral defaults.</summary>
    public PluginHostSnapshot HostSnapshot()
    {
        // One reading serves all property reads of the next quarter second: two reads in a row agree, and many plugins do not hit the shell each.
        lock (_snapshotLock)
        {
            if (_snapshot is not null && DateTime.UtcNow - _snapshotAt < SnapshotLife) return _snapshot with { SleepTimerEnds = SleepTimerEnds };
            _snapshot = ReadSnapshot();
            _snapshotAt = DateTime.UtcNow;
            return _snapshot with { SleepTimerEnds = SleepTimerEnds };
        }
    }

    private readonly object _snapshotLock = new();
    private PluginHostSnapshot? _snapshot;
    private DateTime _snapshotAt;
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromMilliseconds(250);

    /// <summary>The next read goes to the shell again (something changed).</summary>
    private void DropSnapshot()
    {
        lock (_snapshotLock) _snapshot = null;
    }

    private PluginHostSnapshot ReadSnapshot()
    {
        PluginHostSnapshot? read = null;
        try
        {
            read = HostStateReader?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] host state unreadable: {ex.GetType().Name}");
        }
        var app = read?.RunningApp is { } a ? PluginHostRules.Clean(a.Id, a.Name) : null;
        return new PluginHostSnapshot(read?.Version ?? CatalogRules.AppVersion.ToString(), read?.Mode ?? PluginShellMode.App, read?.PcMode ?? false, app, SleepTimerEnds);
    }

    private readonly object _hostGate = new();
    private bool _hostPending;
    private DateTime _hostLast = DateTime.MinValue;

    /// <summary>The clock of <see cref="HostChangeInterval"/> (tests pass a fake one).</summary>
    internal TimeProvider HostTime { get; set; } = TimeProvider.System;

    /// <summary>The least time between two <see cref="IPluginHostState.Changed"/> calls.</summary>
    public TimeSpan HostChangeInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The shell calls this when something of the host state changed. The calls are merged: the first one starts a short wait (so that a
    /// burst of changes ends up as one call) and every plugin with "host.state" hears about it once afterwards.
    /// </summary>
    public void NotifyHostChanged()
    {
        DropSnapshot();
        TimeSpan wait;
        lock (_hostGate)
        {
            if (_hostPending) return;
            _hostPending = true;
            wait = HostChangeInterval - (HostTime.GetUtcNow().UtcDateTime - _hostLast);
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        }
        _ = Task.Delay(wait, HostTime).ContinueWith(_ => FireHostChanged(), TaskScheduler.Default);
    }

    private void FireHostChanged()
    {
        lock (_hostGate)
        {
            _hostPending = false;
            _hostLast = HostTime.GetUtcNow().UtcDateTime;
        }
        List<(string Id, Context Context)> all;
        lock (_gate) all = [.. _contexts.Select(c => (c.Key, c.Value))];
        DropSnapshot();
        foreach (var (id, context) in all)
        {
            if (GrantOf(id) is not { } grant || !grant.Capabilities.Contains(PluginCapabilities.HostState) || IsPaused(id)) continue;
            context.RaiseHostChanged();
        }
    }

    /// <summary>Mega Hub is about to quit: the plugins' <see cref="IPluginEvents.HostStopping"/> handlers get <see cref="PluginEvents.StoppingTimeout"/> at most. Only the first call does anything.</summary>
    public int RaiseHostStopping() => Events.RaiseHostStopping();

    private sealed partial class Context
    {
        private readonly object _evLock = new();
        private Action<bool>? _serviceChanged;
        private Action? _hostChanged;
        private PluginHostView? _host;

        public bool ServiceAllowed(string serviceId)
        {
            Need(nameof(ServiceAllowed));
            return owner.ServiceAllowedFor(pluginId, serviceId);
        }

        public event Action<bool>? ServiceAllowedChanged
        {
            add { lock (_evLock) _serviceChanged += value; }
            remove { lock (_evLock) _serviceChanged -= value; }
        }

        internal void RaiseServiceAllowedChanged(bool any)
        {
            Action<bool>? handlers;
            lock (_evLock) handlers = _serviceChanged;
            PluginEvents.Dispatch(handlers, h => h(any), "service handler");
        }

        /// <summary>Null without the capability "host.state" (not an exception: a plugin may probe).</summary>
        public IPluginHostState? Host => grant.Capabilities.Contains(PluginCapabilities.HostState) ? _host ??= new PluginHostView(this, owner) : null;

        internal void RaiseHostChanged()
        {
            Action? handlers;
            lock (_evLock) handlers = _hostChanged;
            PluginEvents.Dispatch(handlers, h => h(), "host state handler");
        }

        internal void AddHostChanged(Action handler)
        {
            lock (_evLock) _hostChanged += handler;
        }

        internal void RemoveHostChanged(Action handler)
        {
            lock (_evLock) _hostChanged -= handler;
        }

        private sealed class PluginHostView(Context context, PluginManager owner) : IPluginHostState
        {
            public string Version => owner.HostSnapshot().Version;
            public PluginShellMode Mode => owner.HostSnapshot().Mode;
            public bool PcMode => owner.HostSnapshot().PcMode;
            public PluginAppInfo? RunningApp => owner.HostSnapshot().RunningApp;
            public DateTimeOffset? SleepTimerEnds => owner.SleepTimerEnds;
            public bool Locked => owner.IsLocked?.Invoke() ?? false;

            public event Action? Changed
            {
                add => context.AddHostChanged(value!);
                remove => context.RemoveHostChanged(value!);
            }
        }
    }
}
