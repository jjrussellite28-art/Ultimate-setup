using System.Text.RegularExpressions;
using MegaHub.Core.Plugins;
using CouchHud.Sdk;

namespace MegaHub.Core.Integrations.Tv;

/// <summary>A kind of TV Mega Hub knows by name: which official plugin brings it, how SSDP finds it.</summary>
/// <param name="Id">The provider id stored with a pairing.</param>
/// <param name="Plugin">The id of Mega Hub's own plugin (official catalog) that brings it; only that plugin may register this id.</param>
/// <param name="Name">Shown name when the plugin is not there (yet).</param>
/// <param name="SsdpTarget">The SSDP search target of this kind of TV, or null.</param>
public sealed record TvKnownProvider(string Id, string Plugin, string Name, string? SsdpTarget);

/// <summary>A provider a plugin registered, as the page lists it.</summary>
public sealed record TvProviderView(string Id, string Name, string Plugin);

/// <summary>
/// The TV providers plugins registered (SDK 5, capability "tv.control"). The registry checks every registration (id pattern, ports, the
/// known ids of Mega Hub's own TV plugins, at most two per plugin) and answers only for plugins that are running right now
/// (<see cref="IsRunning"/>): a provider of a plugin that was switched off, uninstalled or paused is not used, even while its code stays
/// loaded until the restart.
/// </summary>
public sealed partial class TvProviderRegistry
{
    public const string LgWebOs = "lg-webos";
    public const string SamsungTizen = "samsung-tizen";
    public const int MaxPerPlugin = 2;
    public const int MaxProviders = 16;

    /// <summary>The kinds of TV Mega Hub's own plugins bring. The ids are reserved for exactly these plugins.</summary>
    public static readonly IReadOnlyList<TvKnownProvider> Known =
    [
        new(LgWebOs, "webostv", "LG webOS", "urn:lge-com:service:webos-second-screen:1"),
        new(SamsungTizen, "samsungtv", "Samsung Tizen", "urn:samsung.com:device:RemoteControlReceiver:1"),
    ];

    public static TvKnownProvider? KnownById(string? id) => Known.FirstOrDefault(k => k.Id == id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];

    private sealed record Entry(string Plugin, ITvProvider Provider, string Id, string Name, IReadOnlyList<TvPort> Ports);

    /// <summary>Set by the plugin manager: is this plugin loaded, switched on, not uninstalled and not paused right now?</summary>
    public Func<string, bool>? IsRunning { get; set; }

    /// <summary>A plugin registered a provider (the TV page asks again).</summary>
    public event Action? Changed;

    /// <summary>A plugin left (see <see cref="Forget"/>): whoever holds a connection made through it must close it.</summary>
    public event Action<string>? PluginForgotten;

    /// <summary>
    /// Registers <paramref name="provider"/> for <paramref name="pluginId"/>. Null when taken, else the reason it was refused (for the log):
    /// a bad id or name, ports that do not pass, an id reserved for another plugin or registered by another plugin, too many.
    /// Registering the same id again from the same plugin replaces it.
    /// </summary>
    public string? Register(string pluginId, ITvProvider provider, PluginOrigin origin)
    {
        string id, name;
        IReadOnlyList<TvPort> ports;
        try
        {
            id = provider.Id;
            name = provider.Name;
            ports = [.. provider.Ports ?? []];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "provider threw";
        }
        if (!IsValidId(id)) return "id";
        if (TvLink.CheckPorts(ports) is null) return "ports";
        // The reserved kinds are Mega Hub's own: a local or third-party plugin with the right folder name is still not that plugin.
        if (KnownById(id) is not null && origin != PluginOrigin.FirstParty) return "reserved id";
        if (KnownById(id) is { } known && !known.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) return "reserved id";
        var shown = TvText.CleanLabel(name) ?? id;
        lock (_lock)
        {
            if (_entries.FirstOrDefault(e => e.Id == id) is { } other && !other.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) return "id taken";
            _entries.RemoveAll(e => e.Id == id);
            if (_entries.Count(e => e.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) >= MaxPerPlugin) return "too many";
            if (_entries.Count >= MaxProviders) return "too many";
            _entries.Add(new Entry(pluginId, provider, id, shown, ports));
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>A plugin left (hot-unloaded, switched off, uninstalled or failed to start): everything it registered goes, so a later start of the same plugin begins clean.</summary>
    public void Forget(string pluginId)
    {
        int removed;
        lock (_lock) removed = _entries.RemoveAll(e => e.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Changed?.Invoke();
        // Sessions already running through this plugin's provider are not in the registry: the controller listens and ends them.
        try { PluginForgotten?.Invoke(pluginId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private bool Running(string plugin) => IsRunning?.Invoke(plugin) ?? true;

    /// <summary>The provider for a stored pairing: registered by <paramref name="pluginId"/> (the plugin that paired it) and running. Null otherwise.</summary>
    public (ITvProvider Provider, IReadOnlyList<TvPort> Ports, string Name)? Find(string providerId, string pluginId)
    {
        Entry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == providerId && x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        return e is not null && Running(e.Plugin) ? (e.Provider, e.Ports, e.Name) : null;
    }

    /// <summary>The plugin that brings a provider id now (registered and running), or null.</summary>
    public string? PluginOf(string providerId)
    {
        Entry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == providerId);
        return e is not null && Running(e.Plugin) ? e.Plugin : null;
    }

    /// <summary>The providers that can pair now (registered and running), ordered by name.</summary>
    public IReadOnlyList<TvProviderView> Available()
    {
        List<Entry> list;
        lock (_lock) list = [.. _entries];
        return list.Where(e => Running(e.Plugin)).Select(e => new TvProviderView(e.Id, e.Name, e.Plugin)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// A plugin's session as the controller uses it: every call with its own hard timeout (also when the plugin ignores the token), every
/// answer checked (volume 0 to 100, input ids and labels, at most 32 inputs, app ids), anything else a plugin throws turned into
/// <see cref="TvError.BadAnswer"/>, and the link's own refusals (a changed certificate, a blocked port) counted over whatever the plugin
/// made of them. Disposing it closes the plugin's session and then the link (so nothing of the plugin stays connected).
/// </summary>
public sealed class HostedTvSession(ITvSession inner, TvLink link, TimeSpan callTimeout) : ITvSession
{
    public bool Open
    {
        get
        {
            try { return link.Fault is null && inner.Open; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>Runs one plugin call bounded by <paramref name="timeout"/>; see the class.</summary>
    public static async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> call, TvLink link, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            Task<T> task;
            try { task = call(cts.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { task = Task.FromException<T>(ex); }
            // WaitAsync: a plugin that never completes its task does not hold the caller.
            return await task.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (link.Fault is { } fault) throw new TvException(fault, "link");
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw ex switch
            {
                TvException { Code: TvError.Unreachable or TvError.Timeout or TvError.Denied or TvError.Unpaired or TvError.Refused or TvError.BadAnswer or TvError.CertChanged or TvError.PlainOnly or TvError.Blocked } tv => tv,
                TimeoutException or OperationCanceledException => new TvException(TvError.Timeout),
                _ => new TvException(TvError.BadAnswer, "plugin")
            };
        }
    }

    private Task<T> Guard<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct) => GuardAsync(call, link, callTimeout, ct);

    private Task Guard(Func<CancellationToken, Task> call, CancellationToken ct) => GuardAsync(async c =>
    {
        await call(c);
        return true;
    }, link, callTimeout, ct);

    public Task TurnOffAsync(CancellationToken ct) => Guard(inner.TurnOffAsync, ct);

    public async Task<TvVolume> GetVolumeAsync(CancellationToken ct)
    {
        var v = await Guard(inner.GetVolumeAsync, ct) ?? throw new TvException(TvError.BadAnswer, "volume");
        return new TvVolume(Math.Clamp(v.Volume, 0, 100), v.Muted);
    }

    public Task VolumeStepAsync(bool up, CancellationToken ct) => Guard(c => inner.VolumeStepAsync(up, c), ct);

    public Task SetVolumeAsync(int volume, CancellationToken ct) => Guard(c => inner.SetVolumeAsync(Math.Clamp(volume, 0, 100), c), ct);

    public Task SetMuteAsync(bool mute, CancellationToken ct) => Guard(c => inner.SetMuteAsync(mute, c), ct);

    public async Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct) => CleanInputs(await Guard(inner.GetInputsAsync, ct));

    /// <summary>Only inputs with a valid id, each id once, labels cleaned (the id when nothing is left), at most <see cref="TvInput.MaxInputs"/>.</summary>
    public static IReadOnlyList<TvInput> CleanInputs(IReadOnlyList<TvInput>? inputs)
    {
        var list = new List<TvInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in inputs ?? [])
        {
            if (list.Count >= TvInput.MaxInputs) break;
            if (i is null || !TvInput.IsValidId(i.Id) || !seen.Add(i.Id)) continue;
            list.Add(new TvInput(i.Id, TvText.CleanLabel(i.Label) ?? i.Id, i.Connected));
        }
        return list;
    }

    public Task SwitchInputAsync(string inputId, CancellationToken ct)
    {
        if (!TvInput.IsValidId(inputId)) throw new ArgumentException("input");
        return Guard(c => inner.SwitchInputAsync(inputId, c), ct);
    }

    public async Task<string?> GetForegroundAppAsync(CancellationToken ct) => TvText.CleanAppId(await Guard(inner.GetForegroundAppAsync, ct));

    public async Task<string?> GetActiveInputAsync(IReadOnlyList<TvInput> inputs, CancellationToken ct)
    {
        var id = await Guard(c => inner.GetActiveInputAsync(inputs, c), ct);
        return id is not null && inputs.Any(i => i.Id == id) ? id : null;
    }

    public Task<bool?> GetPowerAsync(CancellationToken ct) => Guard(inner.GetPowerAsync, ct);

    public Task TurnOnAsync(CancellationToken ct) => Guard(inner.TurnOnAsync, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The plugin's own cleanup failed or hangs: the link below closes its connections anyway.
        }
        await link.DisposeAsync();
    }
}
