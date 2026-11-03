using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Sdk;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>
/// plugin.json; everything optional. <c>description</c> is a text, or an object per language: <c>{ "de": "…", "en": "…" }</c>.
/// SDK 3 adds <c>id</c>, <c>capabilities</c> (what the plugin needs; null for older plugins, which keep the SDK 2 surface) and <c>license</c>.
/// For a signed package the statement counts, not this file.
/// </summary>
public sealed record PluginManifest(string? Name, string? Version, string? Author, LocalizedText? Description, string? Homepage, int Sdk = 1,
    string? Id = null, IReadOnlyList<string>? Capabilities = null, string? License = null, IReadOnlyList<PluginServiceManifest>? Services = null);

/// <summary>A text that exists in several languages. In JSON a plain string (all languages) or { "de": …, "en": … }.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(LocalizedTextConverter))]
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public static LocalizedText Plain(string text) => new(new Dictionary<string, string> { ["*"] = text });

    /// <summary>The text in <paramref name="language"/>; falls back to the other language, then to any.</summary>
    public string? For(string language)
    {
        if (Values.TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        if (Values.TryGetValue("*", out t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        var other = language == "de" ? "en" : "de";
        if (Values.TryGetValue(other, out t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        return Values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }
}

internal sealed class LocalizedTextConverter : System.Text.Json.Serialization.JsonConverter<LocalizedText>
{
    public override LocalizedText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() is { } s ? LocalizedText.Plain(s) : null;
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("description: text or { \"de\": …, \"en\": … }");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) values[key] = reader.GetString() ?? "";
            else reader.Skip();
        }
        return new LocalizedText(values);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Values, options);
}

/// <summary>A plugin folder as the settings show it, loaded or not.</summary>
public sealed record PluginInfo(
    string Folder,
    string Id,
    string Name,
    string? Version,
    string? Author,
    string? Description,
    string? Homepage,
    bool Enabled,
    bool Loaded,
    string? Error,
    bool UserInstalled)
{
    /// <summary>The description in every language the plugin.json gave; <see cref="Description"/> is the one for the active language.</summary>
    public LocalizedText? DescriptionText { get; init; }

    /// <summary>Not loaded: nobody installed this folder through CouchHud, or its files changed. Needs the user's approval.</summary>
    public bool NeedsApproval { get; init; }

    /// <summary>The code shown with the approval question; it changes with the files.</summary>
    public string? ApprovalCode { get; init; }

    /// <summary>The whole hash behind <see cref="ApprovalCode"/>: the approval sends it back and holds only for exactly these files.</summary>
    public string? ApprovalHash { get; init; }

    /// <summary>True if the folder was installed before and its files are not the installed ones any more.</summary>
    public bool Changed { get; init; }

    // ---- SDK 3 ----

    /// <summary>Where it comes from: CouchHUD's own (signed or shipped), a third-party developer ("Fremdentwickler"), or local.</summary>
    public PluginOrigin Origin { get; init; } = PluginOrigin.Local;

    /// <summary>The pinned key of a signed package (lower-case hex SHA-256), for the details.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>What it may use (granted, not merely declared).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Withdrawn by CouchHUD's signed catalog: not loaded until the parents accept it for exactly these files.</summary>
    public bool Revoked { get; init; }

    public string? RevokedReason { get; init; }

    /// <summary>Offline mode (Settings › Privacy) is on and this plugin may reach the internet (see <see cref="PluginOfflinePolicy"/>): not loaded, or kept out of sight until the next start.</summary>
    public bool PausedOffline { get; init; }

    /// <summary>Not loaded at the start because Offline mode was on (set at load time).</summary>
    public bool SkippedForOffline { get; init; }

    /// <summary>Was skipped for Offline mode and Offline is off now: it loads at the next start.</summary>
    public bool StartsAfterRestart { get; init; }

    // ---- Plugins without a restart (0.7) ----

    /// <summary>
    /// Switched on, but not started now: an earlier run of this folder in this process could not be stopped for sure (see
    /// <see cref="PluginLifecycleRules"/>), and two copies of one plugin must never run side by side. It starts with the next start.
    /// </summary>
    public bool NeedsRestartToStart { get; init; }

    /// <summary>
    /// Switched off or removed while it ran, and its own code gave no sign that it stopped (no <see cref="IDisposable"/>, or it hung): the host has
    /// cut it off completely, but what it started on its own (threads, timers, sockets) stops with the next start.
    /// </summary>
    public bool StopsAfterRestart { get; init; }

    /// <summary>
    /// The user uninstalled it, but its files are still held (a plugin that did not confirm stopping): it is cut off and never loads again, and
    /// the next start deletes the folder. The list shows this instead of "error" and offers nothing but the note.
    /// </summary>
    public bool RemovedAtRestart { get; init; }
}

/// <summary>
/// Loads plugins from plugins/&lt;name&gt;/ folders. Each folder is one plugin with its own
/// load context, so plugins can ship their own dependency versions; only the SDK
/// assembly is shared with the host. The entry assembly is &lt;name&gt;.dll; other
/// DLLs in the folder are dependencies and are resolved on demand.
/// A broken plugin is reported, never fatal, and every call into it has a timeout.
/// </summary>
public sealed partial class PluginManager(IReadOnlyList<(string Dir, bool UserInstalled)> pluginDirs, string dataDir, Func<string, bool> isEnabled)
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly List<PluginInfo> _infos = [];
    private readonly List<(string Plugin, ILibraryProvider Provider)> _providers = [];
    private readonly List<(string Plugin, PluginPage Page)> _pages = [];
    private readonly List<(string Plugin, PluginTab Tab)> _tabs = [];
    private readonly List<(string Plugin, QuickAction Action)> _actions = [];
    private readonly List<(string Plugin, PluginSetting Setting)> _settings = [];
    private readonly List<(string Plugin, PluginTool Tool, ToolSpec Spec)> _tools = [];
    private readonly List<(string Plugin, PluginFunction Function)> _functions = [];
    private readonly Dictionary<string, SettingsStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    // Plugins that run now (by folder), ids that are starting, and folders whose earlier run could not be stopped for sure (see PluginLifecycleRules).
    private readonly Dictionary<string, RunningPlugin> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginStop> _unstopped = new(StringComparer.OrdinalIgnoreCase);

    // The ids of plugins whose earlier run could not be stopped for sure: the same id in another folder (a copy) must not start a second one either.
    private readonly HashSet<string> _unstoppedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public PluginEvents Events { get; } = new();

    /// <summary>Hashes of installed plugins; a user plugin that is not (or no longer) in it waits for approval. Null: no check (tests, tools).</summary>
    public PluginTrust? Trust { get; init; }

    /// <summary>Asked by plugins (<see cref="IPluginContext.Locked"/>): is the couch locked right now?</summary>
    public Func<bool>? IsLocked { get; set; }

    /// <summary>Set by the host: is a child's profile active right now? Asked on every decision about a tool (a tool cannot ask for a code).</summary>
    public Func<bool>? IsChildProfile { get; set; }

    /// <summary>Set by the host: (plugin name, title, body).</summary>
    public Action<string, string, string>? Notifier { get; set; }

    /// <summary>Set by the host: opens an http(s) address in the couch browser (plugin name, address); false when refused.</summary>
    public Func<string, string, bool>? UrlOpener { get; set; }

    // ---- SDK 3 host services ----

    /// <summary>Restricted programs, startup adapters and the command-line checks.</summary>
    public PluginHostServices Host { get; } = new();

    /// <summary>The downloads page (plugin lines go there as <see cref="DownloadKind.Plugin"/>); null: plugins get no downloads.</summary>
    public DownloadCenter? DownloadCenter { get; set; }

    public IPluginNetwork Network { get; set; } = new PluginNetworkState();

    /// <summary>Fan speeds and temperatures one of CouchHUD's own plugins publishes for the others ("sensors.publish" and "sensors.read").</summary>
    public PluginSensorBoard SensorBoard { get; } = new();

    /// <summary>Set by the shell: runs a checked, confirmed program (plugin name, canonical exe, arguments, timeout).</summary>
    public Func<string, string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PluginCliResult>>? CliRunner { get; set; }

    /// <summary>Set by the shell: hands a setup to Windows after its own checks (plugin id, plugin name, path, sha256).</summary>
    public Func<string, string, string, string, CancellationToken, Task<bool>>? SetupRunner { get; set; }

    /// <summary>
    /// Set by the shell: encrypts a plugin's secret for the Windows user (plugin id, plain value, stored text) and decrypts it again
    /// (plugin id, stored text, plain value; empty when it cannot). The plugin id is part of the encryption, so a stored value of one
    /// plugin means nothing to another. Without it secrets are not kept at all (<see cref="IPluginSettings.SetSecret"/> stores nothing).
    /// </summary>
    public Func<string, string, string>? ProtectSecret { get; set; }

    /// <inheritdoc cref="ProtectSecret"/>
    public Func<string, string, string>? UnprotectSecret { get; set; }

    /// <summary>
    /// Set by the shell, for plugins with the capability "browser.signin": opens a sign-in page in the couch browser and hands the redirect
    /// back (plugin id, plugin name, page address, redirect host, receiver); false when refused. See <see cref="IPluginSignIn"/>.
    /// </summary>
    public Func<string, string, string, string, Action<string, string>, bool>? SignInOpener { get; set; }

    /// <summary>Set by the shell: ends the sign-in of a plugin (plugin id).</summary>
    public Action<string>? SignInCanceller { get; set; }

    /// <summary>A plugin said a page's rows changed (plugin id, page id).</summary>
    public event Action<string, string>? PageInvalidated;

    /// <summary>
    /// The TV providers of the plugins (SDK 5, "tv.control"). It answers only for plugins that run right now (<see cref="IsRunning"/>);
    /// the core's TV controller is the only one that talks to a provider.
    /// </summary>
    public CouchHud.Core.Integrations.Tv.TvProviderRegistry TvProviders =>
        LazyInitializer.EnsureInitialized(ref _tvProviders, () => new CouchHud.Core.Integrations.Tv.TvProviderRegistry { IsRunning = RunsTvProvider });

    private CouchHud.Core.Integrations.Tv.TvProviderRegistry? _tvProviders;

    /// <summary>
    /// Is this plugin loaded, switched on, not uninstalled, not paused, and does it still hold "tv.control"? (A plugin that was switched off or
    /// uninstalled may stay loaded until the restart; its TV provider is not used from that moment on.)
    /// </summary>
    public bool RunsTvProvider(string pluginId)
    {
        bool loaded;
        lock (_gate) loaded = _infos.Any(i => i.Loaded && i.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (!loaded || GrantOf(pluginId) is not { } grant || !grant.Capabilities.Contains(PluginCapabilities.TvControl) || grant.Sdk < TvSdk.MinSdk) return false;
        return isEnabled(grant.Folder) && Trust?.IsRemoved(grant.Folder) != true && !Paused(grant.Capabilities);
    }

    /// <summary>A plugin registered a tab, or the plugins finished loading: the tab bar asks again (<see cref="ResolveTabs"/>).</summary>
    public event Action? TabsChanged;

    /// <summary>
    /// The plugin tabs to show now (see <see cref="PluginTabRules.Resolve"/>): only plugins that are loaded and hold the "tabs" capability,
    /// only for pages that exist, with the area switches and the profile applied.
    /// </summary>
    public IReadOnlyList<PluginTabView> ResolveTabs(AreaSet areas, bool childProfile)
    {
        List<PluginTabRules.Registered> registered;
        HashSet<string> loaded;
        lock (_gate)
        {
            registered = _tabs.Where(t => _grants.ContainsKey(t.Plugin))
                .Select(t => new PluginTabRules.Registered(t.Plugin, _grants[t.Plugin].Origin, _grants[t.Plugin].Capabilities, t.Tab)).ToList();
            loaded = _infos.Where(i => i.Loaded).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        // The state now, not the one at load time: a plugin that was switched off or uninstalled may stay loaded until the restart, but its
        // tab goes at once (the host raises TabsChanged on those changes).
        bool Running(string plugin)
        {
            if (!loaded.Contains(plugin) || GrantOf(plugin) is not { } grant) return false;
            return isEnabled(grant.Folder) && Trust?.IsRemoved(grant.Folder) != true && !Paused(grant.Capabilities);
        }
        return PluginTabRules.Resolve(registered, Running, (plugin, page) => FindPage(plugin, page) is not null, Strings.Language, areas, childProfile, locked: IsLocked?.Invoke() ?? false, levelOf: LevelOf);
    }

    /// <summary>A plugin was switched on or off, approved, accepted, installed or uninstalled: the tab bar asks again.</summary>
    public void RaiseTabsChanged()
    {
        // The same moments (switched off, uninstalled, Offline flipped) end a publisher's sensor values.
        PurgeInactiveSensors();
        TabsChanged?.Invoke();
    }

    /// <summary>Is this plugin loaded, switched on, not removed and not paused by Offline mode? Only such a plugin's sensor values are handed out.</summary>
    public bool IsActive(string pluginId)
    {
        if (GrantOf(pluginId) is not { } grant) return false;
        return isEnabled(grant.Folder) && Trust?.IsRemoved(grant.Folder) != true && !Paused(grant.Capabilities);
    }

    /// <summary>Takes the sensor values of every plugin that is no longer active off the board (disabled, uninstalled, unloaded, paused).</summary>
    public void PurgeInactiveSensors() => SensorBoard.Retain(IsActive);

    /// <summary>The revocation list of CouchHUD's signed catalog (from its cache), read before the plugins load.</summary>
    public IReadOnlyList<CatalogRevocation> Revocations { get; set; } = [];

    /// <summary>Settings a plugin starts with when it has none yet (plugin id → key → value), e.g. what CouchHUD kept for CaseHUD before it was a plugin.</summary>
    public Func<string, IReadOnlyDictionary<string, string?>?>? SettingSeeds { get; set; }

    /// <summary>A seed went into the plugin's settings (plugin id): the host drops it so it is used only once.</summary>
    public Action<string>? SeedApplied { get; set; }

    private readonly Dictionary<string, PluginGrant> _grants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The services a plugin may use: the ones its plugin.json declares and that pass the checks; for a third-party plugin only within what its
    /// signed statement declared when it was installed (the trust list's record), so a changed plugin.json cannot widen the hosts.
    /// </summary>
    internal static IReadOnlyList<PluginService> DeclaredServices(string folder, PluginOrigin origin, PluginManifest? manifest, PluginTrustEntry? entry)
    {
        void Log(string m) => Console.Error.WriteLine($"[plugins] {folder}: {m}");
        var parsed = PluginNetRules.Parse(manifest?.Services, manifest?.Sdk ?? 1, Log);
        return origin == PluginOrigin.ThirdParty ? PluginNetRules.Restrict(parsed, entry?.Services, Log) : parsed;
    }

    /// <summary>What a loaded plugin got: its origin and the capabilities it may use.</summary>
    public sealed record PluginGrant(string Folder, PluginOrigin Origin, IReadOnlySet<string> Capabilities, int Sdk = 1, IReadOnlyList<PluginService>? Services = null);

    /// <summary>The SDK version a loaded plugin declared in its plugin.json (1 when it declared none or is not loaded). What needs a newer SDK (page actions) is ignored below it.</summary>
    public int SdkOf(string pluginId) => GrantOf(pluginId)?.Sdk ?? 1;

    /// <summary>The grant of a loaded plugin (by id); null when it is not loaded.</summary>
    public PluginGrant? GrantOf(string pluginId)
    {
        lock (_gate) return _grants.GetValueOrDefault(pluginId);
    }

    /// <summary>The origin of a loaded plugin; a plugin that is not loaded counts as local (the strictest rules for a page that is not its own).</summary>
    public PluginOrigin OriginOf(string pluginId) => GrantOf(pluginId)?.Origin ?? PluginOrigin.Local;

    // Plugins may load on a background thread while the UI is already up: lists are only touched under this lock.
    private readonly object _gate = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when all plugins are loaded (or failed to).</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>Set by the host: Offline mode is on now (Settings › Privacy). Plugins that may reach the internet are then not loaded (<see cref="PluginOfflinePolicy"/>).</summary>
    public Func<bool>? OfflineNow { get; set; }

    private bool Paused(IEnumerable<string>? capabilities) => PluginOfflinePolicy.Pauses(OfflineNow?.Invoke() == true, capabilities);

    /// <summary>
    /// Is this loaded plugin paused (Offline mode on and it may reach the internet)? A paused plugin is fenced: the host lists none of its
    /// provider items, pages, quick actions or tools, calls none of them, delivers no events to it and refuses its calls into the host. What the
    /// host cannot fence is code the plugin already started on its own (its threads and sockets): that stops completely only with the restart.
    /// </summary>
    // Registrations (pages, actions, tools, providers, tabs, functions) made while a plugin is paused are dropped, not queued: the plugin registers
    // again after the restart that loads it. What it registered before the pause comes back when Offline goes off.
    public bool IsPaused(string pluginId) => Paused(GrantOf(pluginId)?.Capabilities);

    // A plugin that is still starting (its Initialize has not returned) is not visible to anybody: what it registered so far shows up in the
    // lists in one step with the end of its start, or not at all when the start fails (see LoadAssembly).
    private List<T> Live<T>(List<T> list, Func<T, string> plugin, bool includePaused = false)
    {
        lock (_gate) return list.Where(x => !_initializing.Contains(plugin(x)) && (includePaused || !IsPaused(plugin(x)))).ToList();
    }

    public IReadOnlyList<PluginInfo> Plugins => ListedPlugins();

    private IReadOnlyList<PluginInfo> ListedPlugins()
    {
        // One read of the removal marks per listing: a folder with a mark is on its way out, whatever else its entry says.
        HashSet<string> removed;
        try
        {
            removed = new HashSet<string>(Trust?.RemovedFolders() ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return Snapshot(_infos)
        .Select(i => i with
        {
            RemovedAtRestart = removed.Contains(i.Folder),
            Description = i.DescriptionText is { } d ? d.For(Strings.Language) : i.Description,
            PausedOffline = Paused(i.Capabilities),
            StartsAfterRestart = !removed.Contains(i.Folder) && ((i.SkippedForOffline && !Paused(i.Capabilities)) || i.NeedsRestartToStart)
        }).ToList();
    }

    /// <summary>The folder was uninstalled and its files are gone: it is not listed any more.</summary>
    public void DropListing(string folder)
    {
        Remove(_infos, i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The user uninstalled this folder and the removal is marked (it never loads again, the next start deletes it).</summary>
    public bool IsUninstalled(string folder) => Trust?.IsRemoved(folder) == true;
    public IReadOnlyList<ILibraryProvider> LibraryProviders => Live(_providers, p => p.Plugin, includePaused: true).Select(p => p.Provider).ToList();
    public IReadOnlyList<(string Plugin, PluginPage Page)> Pages => Live(_pages, p => p.Plugin);
    public IReadOnlyList<(string Plugin, QuickAction Action)> QuickActions => Live(_actions, a => a.Plugin);
    public IReadOnlyList<(string Plugin, PluginSetting Setting)> Settings => Live(_settings, s => s.Plugin);

    /// <summary>The functions the plugins declared (<see cref="IPluginContext.RegisterFunctions"/>).</summary>
    public IReadOnlyList<(string Plugin, PluginFunction Function)> Functions => Live(_functions, f => f.Plugin);

    /// <summary>Set by the host: the level the owner stored for a function (plugin id, function id) as its text, or null. Asked on every decision.</summary>
    public Func<string, string, string?>? StoredLevel { get; set; }

    /// <summary>
    /// The level that applies to a function of a plugin now (<see cref="PluginFunctionRules.Effective"/>): null for an untagged item (no function id),
    /// admin for an id the plugin never declared (fail closed), the owner's stored choice otherwise, else the plugin's suggestion (own plugins only
    /// above admin). A damaged store counts as nothing stored.
    /// </summary>
    public PluginLevel? LevelOf(string pluginId, string? functionId)
    {
        if (string.IsNullOrEmpty(functionId)) return null;
        PluginFunction? declared;
        lock (_gate) declared = _functions.FirstOrDefault(f => f.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && f.Function.Id == functionId).Function;
        string? stored = null;
        if (declared is null) LogUndeclared(pluginId, functionId);
        if (declared is not null)
        {
            try { stored = StoredLevel?.Invoke(pluginId, functionId); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { stored = null; }
        }
        return PluginFunctionRules.Effective(declared, stored, PluginPolicy.IsOwn(OriginOf(pluginId)));
    }

    private static readonly CouchHud.Core.Guard.LogThrottle UndeclaredLog = new(TimeSpan.FromMinutes(10));

    /// <summary>An item names a function the plugin never declared: it counts as admin only; said once in a while, with the text cleaned (it comes from the plugin).</summary>
    private static void LogUndeclared(string pluginId, string functionId)
    {
        var clean = new string(functionId.Where(c => !char.IsControl(c)).Take(40).ToArray());
        var line = $"[plugins] {pluginId}: an item names the function '{clean}' that the plugin never declared (treated as admin only)";
        if (UndeclaredLog.Allow(line, DateTime.UtcNow, out _)) Console.Error.WriteLine(line);
    }

    /// <summary>The ids as the plugin declared them (the stored levels are kept under these); false when the plugin does not declare the function.</summary>
    public bool TryCanonicalFunction(string pluginId, string functionId, out string canonicalPlugin, out string canonicalFunction)
    {
        foreach (var (plugin, function) in Functions)
        {
            if (!plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) || !function.Id.Equals(functionId, StringComparison.OrdinalIgnoreCase)) continue;
            canonicalPlugin = plugin;
            canonicalFunction = function.Id;
            return true;
        }
        canonicalPlugin = canonicalFunction = "";
        return false;
    }

    /// <summary>
    /// Every id a plugin folder may be known by: the folder name (what a plugin that did not load is listed under), the id in its plugin.json,
    /// the id in the trust list, and the id of a loaded one. A stored choice for a plugin is kept under its real id, which is none of these alone.
    /// </summary>
    public IReadOnlySet<string> PluginIdsOf(string folder)
    {
        // Without a readable trust list the origin of the folder is unknown and reserved ids cannot be told from CouchHUD's own plugin. This lookup
        // does not throw; whoever decides to forget something on its basis goes through InstalledPluginIds / PluginIdsToForget (RequireReadableTrust).
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folder };
        if (Trust?.Entry(folder)?.PluginId is { Length: > 0 } trusted) ids.Add(trusted);
        foreach (var (dir, _) in pluginDirs)
        {
            try
            {
                if (ReadManifest(Path.Combine(dir, folder))?.Id is { Length: > 0 } manifestId) ids.Add(manifestId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A folder that cannot be read keeps the ids it has.
            }
        }
        foreach (var info in Snapshot(_infos).Where(i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))) ids.Add(info.Id);
        // Only CouchHUD's own plugins may be known by a reserved id: a folder of anyone else that claims "homeassistant" (by its name or its
        // plugin.json) neither keeps the owner's levels for the real one alive nor wipes them when it is removed.
        if (Trust?.Entry(folder)?.Origin != PluginOrigin.FirstParty) ids.RemoveWhere(id => PluginNames.IsReserved(id));
        return ids;
    }

    /// <summary>
    /// Fail-safe for everything that forgets a stored choice: when the trust list cannot be read (damaged, locked, tampered with) the origins and the
    /// removal marks are unknown, so the plugin ids cannot be judged and nothing may be forgotten on that basis. Throws then; the caller keeps all choices.
    /// </summary>
    private void RequireReadableTrust()
    {
        if (Trust is { Readable: false }) throw new IOException("the list of approved plugins cannot be read");
        // A deleted list reads as an empty one, which is right for a fresh install and wrong for plugins that are already there: with plugin folders
        // on the disk and no list at all, the origins (CouchHUD's own plugins) are unknown, so nothing is judged gone on that basis.
        if (Trust is { HasStore: false } && AnyPluginFolder()) throw new IOException("the list of approved plugins is missing");
    }

    private bool AnyPluginFolder()
    {
        foreach (var (dir, _) in pluginDirs)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any()) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<(string Folder, IReadOnlySet<string> Ids)> InstalledFolders()
    {
        RequireReadableTrust();
        foreach (var (dir, _) in pluginDirs)
        {
            string[] folders;
            try
            {
                folders = Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: nothing is judged gone (the caller keeps everything it cannot name).
                throw new IOException("plugin folders unreadable", ex);
            }
            foreach (var path in folders)
            {
                var folder = Path.GetFileName(path);
                if (Trust?.IsRemoved(folder) == true) continue;
                yield return (folder, PluginIdsOf(folder));
            }
        }
    }

    /// <summary>
    /// The ids to forget when this folder is uninstalled: every id it was known by, except those another installed folder also holds (two folders
    /// that claim one id must not wipe the other's choices).
    /// </summary>
    public IReadOnlySet<string> PluginIdsToForget(string folder)
    {
        RequireReadableTrust();
        var ids = new HashSet<string>(PluginIdsOf(folder), StringComparer.OrdinalIgnoreCase);
        foreach (var (other, otherIds) in InstalledFolders())
            if (!other.Equals(folder, StringComparison.OrdinalIgnoreCase)) ids.ExceptWith(otherIds);
        return ids;
    }

    /// <summary>
    /// The ids of every plugin that is still installed: a folder is there and is not marked for removal. Whether it loaded, is switched off,
    /// waits for approval or is withdrawn does not matter: the owner's choices for it stay until it is really gone.
    /// </summary>
    public IReadOnlySet<string> InstalledPluginIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, folderIds) in InstalledFolders()) ids.UnionWith(folderIds);
        return ids;
    }

    /// <summary>Does the plugin declare this function?</summary>
    public bool HasFunction(string pluginId, string functionId) => Functions.Any(f => f.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && f.Function.Id == functionId);

    /// <summary>Plugin tools as the assistant sees them, names prefixed with the plugin id.</summary>
    public IReadOnlyList<ToolSpec> ToolSpecs => Live(_tools, t => t.Plugin).Select(t => ToolSpecNow(t.Plugin, t.Tool, t.Spec)).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>A tool tagged with a function: off hides it; restricted lets it run on the locked couch; admin keeps it off the locked couch. Untagged: as registered.</summary>
    private ToolSpec? ToolSpecNow(string plugin, PluginTool tool, ToolSpec spec)
    {
        if (LevelOf(plugin, tool.Function) is not { } level) return spec;
        // A tool cannot be asked for a code: admin in a child's profile is refused like the locked couch, off is gone for everybody.
        if (PluginFunctionRules.Refusal(level, IsLocked?.Invoke() ?? false, IsChildProfile?.Invoke() ?? false) is not null) return null;
        return spec with { AllowedWhileLocked = level == PluginLevel.Restricted };
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_gate) return [.. list];
    }

    private void Add<T>(List<T> list, T item, PluginLife? life = null)
    {
        // The check and the add are one step under the lock the stop takes too: nothing is registered by a plugin that was cut off.
        lock (_gate)
        {
            if (life?.Dead == true) return;
            list.Add(item);
        }
    }

    private void Remove<T>(List<T> list, Predicate<T> match)
    {
        lock (_gate) list.RemoveAll(match);
    }

    /// <summary>Puts the item where the one it replaces was (same place in the lists), or adds it.</summary>
    private void Replace<T>(List<T> list, T item, Predicate<T> same, PluginLife? life = null)
    {
        lock (_gate)
        {
            if (life?.Dead == true) return;
            var i = list.FindIndex(same);
            if (i >= 0) list[i] = item;
            else list.Add(item);
        }
    }

    /// <summary>Loads on a worker thread, so a slow first start (disk, virus scanner) never keeps the HUD from showing.</summary>
    public Task StartLoading() => Task.Run(LoadAll);

    public void LoadAll()
    {
        try
        {
            // One load or stop at a time (the start and a plugin switched on by hand never run side by side).
            _lifecycle.Wait();
            try
            {
                LoadAllCore();
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        finally
        {
            _loaded.TrySetResult();
            TabsChanged?.Invoke();
        }
    }

    private void LoadAllCore()
    {
        foreach (var (pluginsDir, userInstalled) in pluginDirs)
        {
            if (!Directory.Exists(pluginsDir)) continue;
            if (!userInstalled)
            {
                // Since 0.6 nothing is shipped next to the exe: every plugin, CouchHUD's own included, comes from the signed catalog and is
                // pinned. A folder there (left by an older setup, or put there by something else) is never loaded.
                Console.Error.WriteLine($"[plugins] {pluginsDir}: bundled plugins are not loaded any more (install them from the catalog)");
                continue;
            }
            // A plugin installed from a signature is loaded before a local one: when two folders claim one id, the signed one keeps it
            // whatever the folder names are, and the local one is told the id is taken.
            foreach (var dir in Directory.EnumerateDirectories(pluginsDir)
                .OrderBy(d => Trust?.Entry(Path.GetFileName(d))?.Origin is PluginOrigin.FirstParty or PluginOrigin.ThirdParty ? 0 : 1)
                .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                // Leftovers of an interrupted install (.couchhud-new-…) are not plugins.
                if (folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
                LoadFolder(dir, folder);
            }
        }
    }

    private void LoadFolder(string dir, string folder)
    {
        // A folder is listed once: loading it again (a plugin switched on, installed or approved without a restart) replaces what was listed.
        Remove(_infos, i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        // Removed by the user while it was loaded: it goes at this start (or the next one) and is never offered for approval.
        if (Trust?.IsRemoved(folder) == true)
        {
            Console.Error.WriteLine($"[plugins] {folder}: removed, not loaded");
            // Listed as what it is (the list marks it from the removal mark), never as an entry that could be switched on.
            var gone = ReadManifest(dir);
            Add(_infos, Info(folder, folder, PluginText.Title(gone?.Name, folder), gone, enabled: false, loaded: false, null, true));
            return;
        }
        var manifest = ReadManifest(dir);
        var name = PluginText.Title(manifest?.Name, folder);
        // The files are held (nobody can change them) from the hash that is compared with the trust list until the plugin is stopped: the code
        // that is judged is the code that is loaded. Whatever ends this method early lets them go again.
        PluginFolderSnapshot? held = null;
        var keep = false;
        var attempt = new StartAttempt();
        try
        {
            try
            {
                held = PluginFolderSnapshot.Open(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that is being written, or a link in the folder: the files cannot be held still, so nothing is judged and nothing loads
                // (fail closed; the next start or switching it on again tries once more).
                Console.Error.WriteLine($"[plugins] {folder}: {ex.Message}");
                Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get("plugin.filesBusy"), true));
                return;
            }
            if (held is null) return;
            PluginTrustEntry? entry = null;
            if (Trust is not null)
            {
                var trust = Trust.Check(folder, held);
                if (trust != PluginTrustState.Trusted)
                {
                    // Dropped into the folder by something else, or changed since: not run until the user says so.
                    Console.Error.WriteLine($"[plugins] {folder}: {trust}, waiting for approval");
                    if (trust == PluginTrustState.Fault)
                    {
                        // The list itself cannot be trusted: nothing to approve against until it is repaired or removed.
                        Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get("plugin.trustFault"), true));
                        return;
                    }
                    var shown = SafeHash(dir);
                    var code = PluginTrust.ShortCode(shown);
                    Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get(trust == PluginTrustState.Changed ? "plugin.changed" : "plugin.unknown"), true)
                        with { NeedsApproval = true, ApprovalCode = code, ApprovalHash = shown.Length == 64 ? shown : null, Changed = trust == PluginTrustState.Changed, Origin = Trust.Entry(folder)?.Origin ?? PluginOrigin.Local });
                    return;
                }
                entry = Trust.Entry(folder);
            }

            var origin = entry?.Origin ?? PluginOrigin.Local;
            var granted = PluginPolicy.Granted(origin, manifest?.Capabilities, manifest?.Sdk ?? 1, entry?.Capabilities);
            var info = Info(folder, folder, name, manifest, enabled: true, loaded: false, null, true) with
            {
                Origin = origin,
                KeyFingerprint = entry?.KeyFingerprint,
                Capabilities = [.. granted.Order(StringComparer.Ordinal)]
            };

            // Withdrawn by CouchHUD's signed catalog: only the parents can let exactly these files run again.
            var id = entry?.PluginId ?? manifest?.Id ?? folder;
            var version = entry?.Version ?? manifest?.Version;
            if (Revocations.FirstOrDefault(r => r.Matches(id, version, entry?.KeyFingerprint)) is { } revoked
                && (entry?.RevocationAccepted is null || entry.RevocationAccepted != entry.Hash))
            {
                var reason = PluginSettingRules.LogLine(revoked.Reason);
                Console.Error.WriteLine($"[plugins] {folder}: revoked ({reason})");
                Add(_infos, info with { Error = Strings.Get("plugin.revoked", name), Revoked = true, RevokedReason = reason });
                return;
            }

            if (!isEnabled(folder))
            {
                Add(_infos, info with { Enabled = false });
                return;
            }
            // Offline mode: a plugin that may reach the internet does not start (it starts at the first launch after Offline is off).
            if (Paused(granted))
            {
                Console.Error.WriteLine($"[plugins] {folder}: paused (offline mode)");
                Add(_infos, info with { PausedOffline = true, SkippedForOffline = true });
                return;
            }
            try
            {
                if (manifest is not null && manifest.Sdk > SdkVersion.Current)
                {
                    throw new InvalidOperationException(Strings.Get("plugin.needsNewer", manifest.Sdk));
                }
                var dll = FindEntryAssembly(dir) ?? throw new InvalidOperationException(Strings.Get("plugin.dllMissing", folder));
                if (!held.Contains(dll)) throw new InvalidOperationException(Strings.Get("plugin.dllMissing", folder));
                var running = LoadAssembly(dll, name, new PluginGrant(folder, origin, granted, manifest?.Sdk ?? 1, DeclaredServices(folder, origin, manifest, entry)), entry?.PluginId, held, loadedId => info with { Id = loadedId, Loaded = true }, attempt);
                lock (_gate) _running[folder] = running;
                keep = true;
            }
            catch (RestartRequiredException)
            {
                // The same plugin id still runs from an earlier start that could not be stopped for sure: it starts with the next start.
                Console.Error.WriteLine($"[plugins] {folder}: the plugin id is still in use by a run that could not be stopped, starts after the restart");
                Add(_infos, info with { NeedsRestartToStart = true });
                keep = attempt.KeepFiles;
            }
            catch (Exception ex)
            {
                // A broken plugin must never keep the shell from starting. What a plugin says is cleaned (one line, no control characters).
                var message = PluginSettingRules.LogLine(ex.Message);
                Console.Error.WriteLine($"[plugins] {PluginSettingRules.LogLine(dir)}: {message}");
                Add(_infos, info with { Error = message });
                keep = attempt.KeepFiles;
            }
        }
        finally
        {
            if (!keep) held?.Dispose();
        }
    }

    private static string SafeHash(string dir)
    {
        try
        {
            return PluginTrust.HashFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private string UserFolder(string folder)
    {
        if (folder.Length == 0 || folder.Trim('.').Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        return pluginDirs.Where(d => d.UserInstalled).Select(d => Path.Combine(d.Dir, folder)).FirstOrDefault(Directory.Exists)
               ?? throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
    }

    /// <summary>
    /// The user said yes to this folder (the host's gate: expert mode and the parents' code): its files are remembered as they are now, as a
    /// local plugin with the capabilities its plugin.json declares (an SDK 2 plugin: the SDK 2 surface), and it loads at the next start.
    /// Never more than a local plugin may have: no first-party capability, whatever the file says. A plugin that was pinned to a key loses
    /// the pin (and with it the updates from its developer) and is local from then on.
    /// The approval holds for exactly the files whose code the user was shown (<paramref name="shownHash"/>, <see cref="PluginInfo.ApprovalHash"/>):
    /// files swapped in between are refused, the code is checked like a local package (<see cref="PluginLint.CheckFolder"/>), and the hash
    /// recorded is the one that was shown, so anything changed afterwards is "changed" again.
    /// </summary>
    public void Approve(string folder, string? shownHash)
    {
        if (Trust is null) return;
        RefuseWhileRemoving(folder);
        var dir = UserFolder(folder);
        RequireShown(dir, shownHash);
        var manifest = ReadManifest(dir);
        if (PluginPolicy.DeclaredMessage(PluginOrigin.Local, manifest?.Capabilities) is { } problem) throw new InvalidOperationException(problem);
        var caps = manifest?.Capabilities is { } declared ? declared.ToList() : null;
        PluginLint.CheckFolder(dir, PluginOrigin.Local, caps ?? [], manifest?.Sdk ?? 1);
        // The check read the files once more: they must still be the ones that were shown.
        RequireShown(dir, shownHash);
        Trust.Record(folder, shownHash!, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local, Capabilities = caps });
    }

    /// <summary>
    /// Looks at an unsigned package (a local install) BEFORE anything is written, staged, trusted or registered, and refuses what would take
    /// over or break a plugin that is already there: a name or id that is reserved for CouchHUD's own plugins (or listed in its signed index,
    /// <paramref name="firstPartyIds"/>), a folder that is installed from a signature (CouchHUD's own or a third party's) or ships with the
    /// program, an id another installed folder already holds, and capabilities a local plugin can never have (named in the message). Throws
    /// an <see cref="InvalidOperationException"/> with the reason; the unreadable trust list refuses too (nothing can be judged then).
    /// </summary>
    public void RefuseLocalPackage(string zipPath, IEnumerable<string>? firstPartyIds)
    {
        using var copy = PackageInstaller.ReadCapped(zipPath);
        RefuseLocalPackage(copy, Path.GetFileNameWithoutExtension(zipPath), firstPartyIds);
    }

    /// <summary>The same on the one private copy of the package that is also linted and installed (no second look at a file that could change).</summary>
    public void RefuseLocalPackage(Stream zip, string zipName, IEnumerable<string>? firstPartyIds)
    {
        var own = (firstPartyIds ?? []).ToList();
        try
        {
            RequireReadableTrust();
            CheckLocalPackage(zip, zipName, own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list of approved plugins or a plugin folder cannot be read: nothing can be judged, so nothing is installed.
            throw new InvalidOperationException(Strings.Get("plugin.trustFault"), ex);
        }
    }

    private void CheckLocalPackage(Stream zip, string zipName, List<string> own)
    {
        PluginManifest? manifest;
        try
        {
            manifest = CouchHud.Core.Updates.AutoUpdateRules.ReadManifest(zip);
        }
        catch (CatalogException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        zip.Position = 0;
        var folder = PackageInstaller.PlannedName(zip, zipName);
        if (folder is null) throw new InvalidOperationException(Strings.Get("package.badName"));
        // The id decides whose data, pages and settings these are, so a reserved id is refused whatever the folder is called; the folder name only
        // by its look (the id check for non-ASCII letters is for ids: a folder called "Überblick" is fine).
        var claimed = new List<string>();
        if (folder.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ') && PluginNames.IsReserved(folder, own)) claimed.Add(folder);
        if (manifest?.Id is { Length: > 0 } manifestId && PluginNames.IsReserved(manifestId, own)) claimed.Add(manifestId);
        if (claimed.Count > 0)
        {
            // "The existing plugin was left untouched" only when there is one of that name.
            var present = claimed.Any(c => Trust?.Entry(c) is not null || pluginDirs.Any(d => Directory.Exists(Path.Combine(d.Dir, c)))
                || InstalledFolders().Any(f => PluginNames.Skeleton(f.Item1) == PluginNames.Skeleton(c) || f.Item2.Any(i => PluginNames.Skeleton(i) == PluginNames.Skeleton(c))));
            throw new InvalidOperationException(Strings.Get(present ? "plugin.local.reserved" : "plugin.local.reservedNone", string.Join(", ", claimed.Select(c => PluginText.Strip(c)))));
        }

        // Installed from a signature, or shipped with the program: a local package never replaces it (uninstall it first, if that is meant).
        // Also when an update for the folder is staged (it is applied at the next start).
        var entry = Trust?.Entry(folder);
        if (entry is { Origin: PluginOrigin.FirstParty or PluginOrigin.ThirdParty } || Trust?.PendingFor(folder) is not null || Trust?.IsRemoved(folder) == true
            || pluginDirs.Any(d => !d.UserInstalled && Directory.Exists(Path.Combine(d.Dir, folder))))
            throw new InvalidOperationException(Strings.Get(Trust?.IsRemoved(folder) == true ? "plugin.local.signedRemoving" : "plugin.local.signedInstalled", PluginText.Strip(folder)));

        // An id that another installed plugin holds would be refused at load anyway; refused here, before the other one is disturbed.
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folder };
        if (manifest?.Id is { Length: > 0 } id2) ours.Add(id2);
        foreach (var (other, otherIds) in InstalledFolders())
            if (!other.Equals(folder, StringComparison.OrdinalIgnoreCase) && otherIds.Overlaps(ours))
                throw new InvalidOperationException(Strings.Get("plugin.local.idTaken", PluginText.Strip(other)));

        if (PluginPolicy.DeclaredMessage(PluginOrigin.Local, manifest?.Capabilities) is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// A package the user installed through the host's gate was just unpacked: the files as they were unpacked (<paramref name="unpackedHash"/>,
    /// taken before they moved into place) are the approved ones, as a local plugin with the capabilities its plugin.json declares.
    /// </summary>
    public void ApproveInstalled(string folder, string unpackedHash)
    {
        if (Trust is null) return;
        RefuseWhileRemoving(folder);
        var dir = UserFolder(folder);
        var manifest = ReadManifest(dir);
        if (PluginPolicy.DeclaredMessage(PluginOrigin.Local, manifest?.Capabilities) is { } problem) throw new InvalidOperationException(problem);
        var caps = manifest?.Capabilities is { } declared ? declared.ToList() : null;
        PluginLint.CheckFolder(dir, PluginOrigin.Local, caps ?? [], manifest?.Sdk ?? 1);
        RequireShown(dir, unpackedHash);
        Trust.Record(folder, unpackedHash, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local, Capabilities = caps });
    }

    /// <summary>
    /// A folder the user uninstalled (its removal is marked, the files are still held) is not approved, accepted or recorded again: the record
    /// would lift the mark and the folder would run again instead of being deleted at the next start. Before any write to the trust list.
    /// </summary>
    private void RefuseWhileRemoving(string folder)
    {
        if (IsUninstalled(folder)) throw new InvalidOperationException(Strings.Get("plugin.removedPending"));
    }

    private static void RequireShown(string dir, string? shownHash)
    {
        var now = SafeHash(dir);
        if (shownHash is not { Length: 64 } || now.Length != 64
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(shownHash.ToLowerInvariant()), System.Text.Encoding.ASCII.GetBytes(now)))
            throw new InvalidOperationException(Strings.Get("plugin.approveMismatch"));
    }

    /// <summary>The parents let a revoked plugin run again, for exactly the files it has now (the host's gate asks for their code every time).</summary>
    public void AcceptRevocation(string folder)
    {
        if (Trust is null) return;
        RefuseWhileRemoving(folder);
        Trust.AcceptRevocation(folder, UserFolder(folder));
    }

    /// <summary>
    /// The parents confirmed a program whose command line this plugin may run (<see cref="IPluginContext.RunTrustedCliAsync"/>): only an
    /// installed program below Program Files (canonical, no links). Returns the canonical path.
    /// </summary>
    public string ConfirmExe(string folder, string exe)
    {
        if (Trust is null) throw new InvalidOperationException(Strings.Get("plugin.trustFault"));
        UserFolder(folder);
        var canonical = Host.Canonical(exe) ?? throw new ArgumentException(Strings.Get("plugin.exeNotTrusted"));
        Trust.ConfirmExe(folder, canonical);
        return canonical;
    }

    private static PluginInfo Info(string folder, string id, string name, PluginManifest? m, bool enabled, bool loaded, string? error, bool user) =>
        new(folder, id, name, m?.Version, m?.Author, m?.Description?.For(Strings.Language), m?.Homepage, enabled, loaded, error, user) { DescriptionText = m?.Description };

    internal static PluginManifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, "plugin.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            return null;
        }
    }

    internal static string? FindEntryAssembly(string dir)
    {
        var byName = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
        if (File.Exists(byName)) return byName;
        // Fallback: the one assembly that comes with a .deps.json (what "dotnet publish" produces).
        var withDeps = Directory.EnumerateFiles(dir, "*.deps.json")
            .Select(d => Path.Combine(dir, Path.GetFileName(d)[..^".deps.json".Length] + ".dll"))
            .Where(File.Exists)
            .ToList();
        return withDeps.Count == 1 ? withDeps[0] : null;
    }

    /// <summary>Set by the host: the ids of CouchHUD's own plugins from the signed index (reserved for everyone else, like the fixed list).</summary>
    public Func<IEnumerable<string>>? FirstPartyIds { get; set; }

    private IEnumerable<string> FirstPartyIdsNow()
    {
        try
        {
            return FirstPartyIds?.Invoke() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <param name="expectedId">The id in the signed statement: the plugin must answer with exactly that one.</param>
    /// <param name="held">The files the hash was taken from: the load context loads nothing else.</param>
    /// <param name="listed">The info line of an id that started; added in the same step that makes the plugin's registrations visible.</param>
    private RunningPlugin LoadAssembly(string dll, string name, PluginGrant grant, string? expectedId, PluginFolderSnapshot held, Func<string, PluginInfo> listed, StartAttempt attempt)
    {
        var context = new PluginLoadContext(dll, grant.Origin == PluginOrigin.FirstParty, held);
        var running = new RunningPlugin(grant.Folder, context, held);
        try
        {
            var assembly = context.LoadChecked(Path.GetFullPath(dll));
            var types = assembly.GetExportedTypes()
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

            foreach (var type in types)
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                var id = plugin.Id;
                // The id decides whose pages, settings, seeds and tools these are: a plugin cannot take another one's, nor one of CouchHUD's own
                // names unless its statement is signed with CouchHUD's key.
                // The id also names the plugin's data folder: plain characters only (no "..", no separators).
                if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z")
                    || (expectedId is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
                    || (!PluginPolicy.IsOwn(grant.Origin) && PluginNames.IsReserved(id, FirstPartyIdsNow())))
                    throw new InvalidOperationException(Strings.Get("plugin.idMismatch", id ?? ""));
                lock (_gate)
                {
                    if (_unstoppedIds.Contains(id)) throw new RestartRequiredException();
                    if (_grants.ContainsKey(id)) throw new InvalidOperationException(Strings.Get("plugin.idTaken", id));
                    _grants[id] = grant;
                    // Invisible to the lists until the plugin has started (see Live).
                    _initializing.Add(id);
                }
                var life = new PluginLife();
                running.Add(id, plugin, life);
                InitializeWithTimeout(plugin, NewContext(id, name, grant, life), id);
                Console.WriteLine($"[plugins] loaded {plugin.Id} from {Path.GetFileName(dll)}");
            }
            if (running.Ids.Count == 0) throw new InvalidOperationException(Strings.Get("plugin.noIPlugin"));
            // Started: everything it registered and its info line become visible in one step.
            lock (_gate)
            {
                foreach (var id in running.Ids)
                {
                    _initializing.Remove(id);
                    _infos.Add(listed(id));
                }
            }
            return running;
        }
        catch
        {
            // A plugin that did not start leaves nothing behind: no grant, no page, tool, tab or provider, nothing it registered before it failed.
            // Cut off and unregistered in one step under the lock (a registration racing with this is refused), then asked to stop like any other.
            lock (_gate)
            {
                foreach (var life in running.Lives) life.Kill();
                foreach (var id in running.Ids) ForgetLists(id);
            }
            foreach (var id in running.Ids) ForgetExternal(id);
            var stop = running.Instances.Count == 0 ? PluginStop.Clean : StopInstancesAsync(running.Instances).GetAwaiter().GetResult();
            if (PluginLifecycleRules.FullyStopped(stop))
            {
                context.Unload();
            }
            else
            {
                // Its own code did not confirm: the files stay held, and neither the folder nor the id starts again in this run.
                lock (_gate)
                {
                    _unstopped[grant.Folder] = stop;
                    _unstoppedIds.UnionWith(running.Ids);
                }
                attempt.KeepFiles = true;
            }
            throw;
        }
    }

    /// <summary>How long a plugin may take to start (its <see cref="IPlugin.Initialize"/>); a longer one counts as failed and is cut off.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private void InitializeWithTimeout(IPlugin plugin, IPluginContext context, string id)
    {
        // On a worker, so a plugin whose start hangs keeps neither the start of CouchHUD nor a plugin switched on by hand waiting for ever. The
        // plugin's thread cannot be taken away from it; its context is cut off (PluginLife.Kill), so whatever it does later reaches nothing.
        var start = Task.Run(() => plugin.Initialize(context));
        try
        {
            if (!start.Wait(InitializeTimeout)) throw new TimeoutException(Strings.Get("plugin.initTimeout", id));
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
        }
    }

    /// <summary>The lists and the grant of an id. Called under <c>_gate</c>, in the same step that kills the instance's context.</summary>
    private void ForgetLists(string pluginId)
    {
        lock (_gate)
        {
            _grants.Remove(pluginId);
            _initializing.Remove(pluginId);
            _providers.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _pages.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _tabs.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _actions.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _settings.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _tools.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            _functions.RemoveAll(x => x.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ForgetExternal(string pluginId)
    {
        lock (_stores) _stores.Remove(pluginId);
        // Hot unload (and a failed start) must leave nothing the plugin put into the host: its context hears no more events, its sensor values
        // are off the board and its TV providers are out of the registry.
        ForgetContext(pluginId);
        SensorBoard.Forget(pluginId);
        _tvProviders?.Forget(pluginId);
        try
        {
            Host.Forget(pluginId);
            SignInCanceller?.Invoke(pluginId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] {PluginSettingRules.LogLine(pluginId)}: cleaning up failed: {PluginSettingRules.LogLine(ex.Message)}");
        }
    }

    // -----------------------------------------------------------------------
    // Plugins without a restart: start one folder now, stop one now.
    //
    // Both go through the code of the start: LoadNow runs LoadFolder (the trust check on the held files, the revocation list, the switch, Offline
    // mode, the SDK, the id rules, the grant from PluginPolicy.Granted), the very same method LoadAll calls for every folder. The host's gate
    // (RequirePluginChange: the unlocked couch, no child's profile, expert mode, the parents' code) comes before in the caller, exactly as before.

    /// <summary>How long a plugin gets to stop (<see cref="IDisposable.Dispose"/>); a plugin that hangs is not waited for longer.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The result of <see cref="StopAsync"/>.</summary>
    /// <param name="WasRunning">The folder ran in this process.</param>
    /// <param name="Stop">How the plugin's own code answered (<see cref="PluginStop.Clean"/> when nothing ran).</param>
    /// <param name="Collected">The load context was let go and the runtime gave the files back (only after a clean stop).</param>
    public sealed record StopResult(bool WasRunning, PluginStop Stop, bool Collected)
    {
        public bool FullyStopped => PluginLifecycleRules.FullyStopped(Stop);
    }

    /// <summary>Does a plugin of this folder run now?</summary>
    public bool IsRunning(string folder)
    {
        lock (_gate) return _running.ContainsKey(folder);
    }

    /// <summary>How the last run of this folder ended when it could not be stopped for sure; null when it can start (again) now.</summary>
    public PluginStop? UnstoppedRun(string folder)
    {
        lock (_gate) return _unstopped.TryGetValue(folder, out var stop) ? stop : null;
    }

    /// <summary>
    /// Starts the plugin of a user folder now (a package that was just installed, a folder the user approved, a plugin that was switched on, a
    /// withdrawn one the parents accepted). The folder is judged by the same code as at the start. Returns the info lines of the folder as they
    /// are listed afterwards. Does nothing when it runs already. A folder whose earlier run in this process could not be stopped for sure is
    /// listed as starting with the next start instead (<see cref="PluginInfo.NeedsRestartToStart"/>).
    /// </summary>
    public async Task<IReadOnlyList<PluginInfo>> LoadNowAsync(string folder)
    {
        var dir = UserFolder(folder);
        // Uninstalled: it never starts again (also not as "starts after restart"), the folder goes at the next start. Nothing is loaded or listed anew.
        if (IsUninstalled(folder)) return ListedPlugins().Where(i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                bool already;
                PluginStop? earlier;
                lock (_gate)
                {
                    already = _running.ContainsKey(folder);
                    earlier = _unstopped.TryGetValue(folder, out var e) ? e : null;
                }
                if (!already)
                {
                    if (PluginLifecycleRules.Decide(PluginHotAction.Start, earlier) == PluginHotOutcome.AtRestart)
                    {
                        Remove(_infos, i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
                        Add(_infos, Info(folder, folder, PluginText.Title(ReadManifest(dir)?.Name, folder), ReadManifest(dir), enabled: isEnabled(folder), loaded: false, null, true) with { NeedsRestartToStart = true });
                    }
                    else
                    {
                        LoadFolder(dir, folder);
                    }
                }
                return ListedPlugins().Where(i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
            }).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            TabsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Stops the plugin of a folder now. First it is cut off (every call into the host is ignored from this moment, its tabs, pages, settings,
    /// quick actions, tools, functions and providers are gone, the events no longer reach it), then its own code is asked to stop
    /// (<see cref="IDisposable"/> / <see cref="IAsyncDisposable"/>) and given <see cref="StopTimeout"/>. A plugin that hangs or throws while
    /// stopping is not waited for: the caller gets the answer after the timeout at the latest, CouchHUD is never blocked. After a clean stop the
    /// load context is released and the files are let go; after any other answer they stay held (the code may still be running) and the folder
    /// may not be started again before the next start.
    /// </summary>
    public async Task<StopResult> StopAsync(string folder)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            RunningPlugin? running;
            lock (_gate)
            {
                if (!_running.Remove(folder, out running)) return new StopResult(false, PluginStop.Clean, true);
            }
            // 1. Cut off: nothing the plugin does from here on reaches the host, and the host lists nothing of it.
            // Killing the context and taking away what it registered is one step under the lock every registration takes.
            lock (_gate)
            {
                foreach (var life in running.Lives) life.Kill();
                foreach (var id in running.Ids) ForgetLists(id);
            }
            foreach (var id in running.Ids) ForgetExternal(id);
            TabsChanged?.Invoke();
            // 2. Ask it to stop, with a time limit.
            var stop = await StopInstancesAsync(running.Instances).ConfigureAwait(false);
            // A native library the plugin loaded stays in the process: this folder does not start again before the restart.
            if (running.Context.LoadedNative) stop = PluginLifecycleRules.Worst([stop, PluginStop.NativeModule]);
            // 3. Let go of what is safe to let go of.
            var collected = false;
            if (PluginLifecycleRules.FullyStopped(stop))
            {
                // This method must not keep the plugin alive while it waits for the runtime to let go of it: the reference is dropped inside.
                var ids = running.Ids.ToList();
                collected = await ReleaseAsync(UnloadAndForget(ref running)).ConfigureAwait(false);
                lock (_gate)
                {
                    _unstopped.Remove(folder);
                    foreach (var id in ids) _unstoppedIds.Remove(id);
                }
            }
            else
            {
                lock (_gate)
                {
                    _unstopped[folder] = stop;
                    _unstoppedIds.UnionWith(running.Ids);
                }
            }
            lock (_gate)
            {
                for (var i = 0; i < _infos.Count; i++)
                {
                    if (!_infos[i].Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) continue;
                    _infos[i] = _infos[i] with { Loaded = false, Enabled = isEnabled(folder), StopsAfterRestart = !PluginLifecycleRules.FullyStopped(stop) };
                }
            }
            return new StopResult(true, stop, collected);
        }
        finally
        {
            _lifecycle.Release();
            TabsChanged?.Invoke();
        }
    }

    private async Task<PluginStop> StopInstancesAsync(IReadOnlyList<IPlugin> instances)
    {
        var answers = new List<PluginStop>();
        foreach (var plugin in instances)
        {
            Task work;
            if (plugin is IAsyncDisposable asyncDisposable) work = Task.Run(async () => await asyncDisposable.DisposeAsync());
            else if (plugin is IDisposable disposable) work = Task.Run(disposable.Dispose);
            else
            {
                answers.Add(PluginStop.NoDispose);
                continue;
            }
            var finished = await Task.WhenAny(work, Task.Delay(StopTimeout)).ConfigureAwait(false);
            if (finished != work)
            {
                // Hangs while stopping: not waited for. Its thread keeps running; nothing it does reaches the host any more.
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                answers.Add(PluginStop.TimedOut);
            }
            else answers.Add(work.IsFaulted ? PluginStop.Failed : PluginStop.Clean);
            if (work.IsFaulted) Console.Error.WriteLine($"[plugins] stopping failed: {PluginSettingRules.LogLine(work.Exception?.GetBaseException().Message)}");
        }
        return PluginLifecycleRules.Worst(answers);
    }

    /// <summary>
    /// Waits (a little) for the runtime to give the released load context back; true when it did. Never unbounded: the collection runs on a helper
    /// task that is waited for at most a second per round, a finalizer that hangs cannot hold the lock for load and stop operations.
    /// </summary>
    private static async Task<bool> ReleaseAsync(WeakReference weak)
    {
        for (var i = 0; i < 4 && weak.IsAlive; i++)
        {
            var round = Task.Run(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            });
            try
            {
                await round.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }
            await Task.Delay(30).ConfigureAwait(false);
        }
        return !weak.IsAlive;
    }

    /// <summary>Releases the load context and the held files of a plugin that confirmed its stop, and drops the caller's reference to it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference UnloadAndForget(ref RunningPlugin? running)
    {
        var weak = new WeakReference(running!.Context, trackResurrection: true);
        running.Instances.Clear();
        running.Context.Unload();
        running.Files?.Dispose();
        running = null;
        return weak;
    }

    public IPluginSettings SettingsOf(string pluginId)
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(pluginId, out var store))
            {
                // Seeds only for CouchHUD's own plugins (what the core kept for CaseHUD before it became one).
                var seed = PluginPolicy.IsOwn(OriginOf(pluginId)) ? SettingSeeds?.Invoke(pluginId) : null;
                store = new SettingsStore(Path.Combine(PluginDataDir(pluginId), "settings.json"), () => Snapshot(_settings).Where(s => s.Plugin == pluginId).Select(s => s.Setting),
                    seed, () => SeedApplied?.Invoke(pluginId),
                    ProtectSecret is { } protect ? plain => protect(pluginId, plain) : null,
                    UnprotectSecret is { } unprotect ? stored => unprotect(pluginId, stored) : null);
                _stores[pluginId] = store;
            }
            return store;
        }
    }

    private string PluginDataPath(string pluginId) => Path.Combine(dataDir, "plugin-data", pluginId);

    private string PluginDataDir(string pluginId)
    {
        var dir = Path.Combine(dataDir, "plugin-data", pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -----------------------------------------------------------------------
    // Calls into plugins: always with a timeout, errors become messages.

    public static async Task<T> SafeAsync<T>(string what, Func<CancellationToken, Task<T>> call, TimeSpan? timeout = null)
    {
        var limit = timeout ?? CallTimeout;
        using var cts = new CancellationTokenSource(limit);
        try
        {
            return await Task.Run(() => call(cts.Token), cts.Token).WaitAsync(limit);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(ex is TimeoutException or OperationCanceledException
                ? Strings.Get("plugin.noAnswer", what)
                : $"{what}: {ex.Message}");
        }
    }

    public (PluginPage Page, string Plugin)? FindPage(string pluginId, string pageId)
    {
        if (PluginSettingRules.IsReservedPageId(pageId)) return null;
        var hit = Live(_pages, p => p.Plugin).FirstOrDefault(p => p.Plugin == pluginId && p.Page.Id == pageId);
        return hit.Page is null ? null : (hit.Page, hit.Plugin);
    }

    public QuickAction? FindAction(string pluginId, string actionId) =>
        Live(_actions, a => a.Plugin).FirstOrDefault(a => a.Plugin == pluginId && a.Action.Id == actionId).Action;

    public bool HasTool(string name) => Live(_tools, t => t.Plugin).Any(t => t.Spec.Name == name);

    public ToolSpec? FindTool(string name) => Live(_tools, t => t.Plugin).FirstOrDefault(t => t.Spec.Name == name).Spec;

    public Task<string> CallToolAsync(string name, JsonElement args)
    {
        var tool = Live(_tools, t => t.Plugin).FirstOrDefault(t => t.Spec.Name == name);
        if (tool.Tool is null) throw new ArgumentException(Strings.Get("plugin.unknownTool", name));
        // Off for everyone: refused here too, whatever the caller (assistant, MCP) believes it saw.
        if (LevelOf(tool.Plugin, tool.Tool.Function) is { } toolLevel
            && PluginFunctionRules.Refusal(toolLevel, IsLocked?.Invoke() ?? false, IsChildProfile?.Invoke() ?? false) is { } why)
            throw new ArgumentException(Strings.Get(why));
        return SafeAsync($"Plugin {tool.Plugin}", ct => tool.Tool.Run(args, ct));
    }

    /// <summary>Plugin tool names: "plugin_&lt;plugin&gt;_&lt;tool&gt;" with only [a-z0-9_]. The prefix keeps them apart from the built-in tools.</summary>
    internal static string ToolName(string pluginId, string tool) =>
        new string($"{ToolCatalog.PluginPrefix}{pluginId}_{tool}".ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    // Plugin code that hangs must not pile up threads: one Detail call per action at a time.
    private readonly Dictionary<(string, string), Task<string?>> _details = [];
    public TimeSpan DetailTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The second line of a guide-menu entry, asked from the plugin on a worker thread with a short timeout:
    /// never on the UI thread, and a plugin that does not answer simply has no second line.
    /// </summary>
    public async Task<string?> DetailAsync(string plugin, QuickAction action)
    {
        if (action.Detail is not { } detail || IsPaused(plugin)) return null;
        Task<string?> call;
        lock (_details)
        {
            if (_details.TryGetValue((plugin, action.Id), out var running) && !running.IsCompleted) return null;
            call = Task.Run(() => detail());
            _details[(plugin, action.Id)] = call;
        }
        try
        {
            return await call.WaitAsync(DetailTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] {plugin}: {(ex is TimeoutException ? "detail did not answer" : ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// What a plugin sees of CouchHUD. Every member checks its capability (<see cref="PluginPolicy.ContextMembers"/>) on every call; a
    /// missing one throws <see cref="PluginCapabilityException"/>. A test makes sure no member of <see cref="IPluginContext"/> is left out.
    /// </summary>
    /// <summary>For tests: the context a plugin with this grant would get (the real one, nothing mocked).</summary>
    internal IPluginContext ContextFor(string pluginId, PluginGrant grant, bool markLoaded = false)
    {
        lock (_gate)
        {
            _grants[pluginId] = grant;
            if (markLoaded) _infos.Add(Info(grant.Folder, pluginId, pluginId, null, enabled: true, loaded: true, null, true));
        }
        return NewContext(pluginId, pluginId, grant, new PluginLife());
    }

    private sealed partial class Context(PluginManager owner, string pluginId, string pluginName, PluginGrant grant, PluginLife life) : IPluginContext
    {
        private IPluginDownloads? _downloads;

        private void Need(string member)
        {
            // A member that is not in the table is refused: a new member cannot slip in without a gate.
            if (!PluginPolicy.ContextMembers.TryGetValue(member, out var cap)) throw new PluginCapabilityException(Strings.Get("plugin.cap.denied", pluginName, member));
            if (cap is not null && !grant.Capabilities.Contains(cap)) throw new PluginCapabilityException(Strings.Get("plugin.cap.denied", pluginName, cap));
        }

        private readonly HashSet<string> _toldPaused = [];

        /// <summary>
        /// Paused by Offline mode (after the capability check): the action does nothing and says so once in the log, never an exception (the plugin's own
        /// background thread would not expect one). The harmless members (Log, Language, Locked, DataDirectory, Settings, Events) never ask.
        /// </summary>
        private bool Fenced(string member)
        {
            Need(member);
            // Stopped (switched off, removed, failed to start): the same quiet fence as Offline mode, for this instance of the plugin only
            // (a later start of the same plugin gets a new context; a thread of the old one stays cut off).
            if (life.Dead)
            {
                bool firstDead;
                lock (_toldPaused) firstDead = _toldPaused.Add(member);
                if (firstDead) Console.Error.WriteLine($"[plugins] {pluginId}: {member} ignored, the plugin was stopped");
                return true;
            }
            if (!owner.IsPaused(pluginId)) return false;
            bool first;
            lock (_toldPaused) first = _toldPaused.Add(member);
            if (first) Console.Error.WriteLine($"[plugins] {pluginId}: {member} ignored while paused (offline mode)");
            return true;
        }

        public string DataDirectory
        {
            get
            {
                Need(nameof(DataDirectory));
                // A stopped plugin gets the name but no folder is created for it.
                return life.Dead ? owner.PluginDataPath(pluginId) : owner.PluginDataDir(pluginId);
            }
        }

        public IPluginSettings Settings
        {
            get
            {
                Need(nameof(Settings));
                // After the stop it reads nothing and writes nothing, and the store is not created again for it.
                return _settings ??= new GuardedSettings(() => life.Dead, () => owner.SettingsOf(pluginId));
            }
        }

        public IPluginEvents Events
        {
            get
            {
                Need(nameof(Events));
                if (_events is null)
                {
                    var events = new PausableEvents(owner.Events, () => life.Dead || owner.IsPaused(pluginId));
                    // When the instance is cut off every subscription it made is taken back, and it can subscribe no more.
                    life.OnKill(events.Close);
                    _events = events;
                }
                return _events;
            }
        }

        private IPluginEvents? _events;
        private IPluginSettings? _settings;

        public void RegisterLibraryProvider(ILibraryProvider provider)
        {
            if (Fenced(nameof(RegisterLibraryProvider))) return;
            owner.Add(owner._providers, (pluginId, new PausableProvider(provider, () => life.Dead || owner.IsPaused(pluginId), pluginId)), life);
        }

        public void Log(string message)
        {
            Need(nameof(Log));
            Console.WriteLine($"[{pluginId}] {PluginSettingRules.LogLine(message)}");
        }

        public string Language
        {
            get
            {
                Need(nameof(Language));
                return Strings.Language;
            }
        }

        public bool Locked
        {
            get
            {
                Need(nameof(Locked));
                return owner.IsLocked?.Invoke() ?? false;
            }
        }

        // Registering an id again replaces the earlier one (a plugin re-registers its texts when the language changes).
        public void RegisterSettings(params PluginSetting[] settings)
        {
            if (Fenced(nameof(RegisterSettings))) return;
            var skipped = 0;
            foreach (var s in settings)
            {
                if (s is null) continue;
                // A plugin may register a bounded number of settings (a replacement of a key it already has always goes through).
                lock (owner._gate)
                {
                    if (life.Dead) return;
                    var known = owner._settings.Any(x => x.Plugin == pluginId && x.Setting.Key == s.Key);
                    if (!known && owner._settings.Count(x => x.Plugin == pluginId) >= PluginSettingRules.MaxSettingsPerPlugin)
                    {
                        skipped++;
                        continue;
                    }
                }
                owner.Replace(owner._settings, (Plugin: pluginId, Setting: s), x => x.Plugin == pluginId && x.Setting.Key == s.Key, life);
            }
            if (skipped > 0) Console.Error.WriteLine($"[plugins] {PluginSettingRules.LogText(pluginId)}: {skipped} settings not registered (at most {PluginSettingRules.MaxSettingsPerPlugin} per plugin)");
        }

        public void RegisterPage(PluginPage page)
        {
            if (Fenced(nameof(RegisterPage))) return;
            // Ids starting with "$" belong to the core (the settings page of a plugin has a visit under "$settings"): a plugin page cannot take one.
            if (PluginSettingRules.IsReservedPageId(page.Id))
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: page not registered (ids starting with '$' are reserved)");
                return;
            }
            owner.Replace(owner._pages, (Plugin: pluginId, Page: page), x => x.Plugin == pluginId && x.Page.Id == page.Id, life);
        }

        public void RegisterTab(PluginTab tab)
        {
            if (Fenced(nameof(RegisterTab))) return;
            // The same checks as when the tabs are listed (PluginTabRules.Resolve), so a refused tab is told to the log at once.
            if (PluginTabRules.Problem(tab, PluginPolicy.IsOwn(grant.Origin)) is { } problem)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: tab not registered ({problem})");
                return;
            }
            lock (owner._gate)
            {
                if (life.Dead) return;
                var at = owner._tabs.FindIndex(x => x.Plugin == pluginId && x.Tab.Id == tab.Id);
                if (at < 0 && owner._tabs.Count(x => x.Plugin == pluginId) >= PluginTabRules.MaxPerPlugin)
                {
                    Console.Error.WriteLine($"[plugins] {pluginId}: tab {tab.Id} not registered (at most {PluginTabRules.MaxPerPlugin} tabs per plugin)");
                    return;
                }
                if (at >= 0) owner._tabs[at] = (pluginId, tab);
                else owner._tabs.Add((pluginId, tab));
            }
            owner.TabsChanged?.Invoke();
        }

        public void RegisterTvProvider(ITvProvider provider)
        {
            if (Fenced(nameof(RegisterTvProvider))) return;
            if (provider is null) return;
            // The members are new in SDK 5: a plugin that declared less was not built for them (and the lint said so at install).
            if (grant.Sdk < TvSdk.MinSdk)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: TV provider not registered (needs sdk {TvSdk.MinSdk})");
                return;
            }
            if (owner.TvProviders.Register(pluginId, provider, grant.Origin) is { } refused)
                Console.Error.WriteLine($"[plugins] {pluginId}: TV provider not registered ({refused})");
        }

        public void RegisterFunctions(params PluginFunction[] functions)
        {
            if (Fenced(nameof(RegisterFunctions))) return;
            foreach (var f in functions ?? [])
            {
                var de = PluginFunctionRules.CleanLabel(f?.LabelDe);
                var en = PluginFunctionRules.CleanLabel(f?.LabelEn);
                if (f is null || !PluginFunctionRules.ValidId(f.Id) || (de is null && en is null))
                {
                    Console.Error.WriteLine($"[plugins] {pluginId}: function not registered (id or label)");
                    continue;
                }
                var clean = f with { LabelDe = de ?? en!, LabelEn = en ?? de! };
                lock (owner._gate)
                {
                    if (life.Dead) break;
                    var at = owner._functions.FindIndex(x => x.Plugin == pluginId && x.Function.Id == clean.Id);
                    if (at < 0 && owner._functions.Count(x => x.Plugin == pluginId) >= PluginFunctionRules.MaxPerPlugin)
                    {
                        Console.Error.WriteLine($"[plugins] {pluginId}: function {clean.Id} not registered (at most {PluginFunctionRules.MaxPerPlugin})");
                        continue;
                    }
                    if (at >= 0) owner._functions[at] = (pluginId, clean);
                    else owner._functions.Add((pluginId, clean));
                }
            }
        }

        public void RegisterQuickAction(QuickAction action)
        {
            if (Fenced(nameof(RegisterQuickAction))) return;
            owner.Replace(owner._actions, (Plugin: pluginId, Action: action), x => x.Plugin == pluginId && x.Action.Id == action.Id, life);
        }

        public void RegisterTool(PluginTool tool)
        {
            if (Fenced(nameof(RegisterTool))) return;
            JsonObject schema;
            try
            {
                schema = JsonNode.Parse(tool.InputSchema) as JsonObject ?? new JsonObject { ["type"] = "object" };
            }
            catch (JsonException)
            {
                schema = new JsonObject { ["type"] = "object" };
            }
            var name = ToolName(pluginId, tool.Name);
            // A name that is taken (by a built-in tool or another plugin tool) is refused, never replaced.
            if (ToolCatalog.Find(name) is not null || owner.HasTool(name))
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: tool {name} already exists, not registered");
                return;
            }
            // Sensitive: over MCP even a read-only plugin tool is confirmed on the TV. A plugin that is not CouchHUD's own counts as
            // writing, so the TV asks before each of its tools runs, whatever it says about itself.
            owner.Add(owner._tools, (pluginId, tool, new ToolSpec(name, $"[{pluginName}] {tool.Description}", schema, PluginPolicy.ToolWrites(grant.Origin, tool.Writes), Sensitive: true)
            {
                AllowedWhileLocked = PluginPolicy.ToolAllowedWhileLocked(grant.Origin, tool.AllowedWhileLocked)
            }), life);
        }

        public void RemoveQuickAction(string id)
        {
            if (Fenced(nameof(RemoveQuickAction))) return;
            owner.Remove(owner._actions, x => x.Plugin == pluginId && x.Action.Id == id);
        }

        private IPluginSignIn? _signIn;

        public IPluginSignIn? SignIn
        {
            get
            {
                if (Fenced(nameof(SignIn))) return null;
                return owner.SignInOpener is null ? null : _signIn ??= new PluginSignIn(owner, pluginId, pluginName);
            }
        }

        public void Notify(string title, string body)
        {
            if (Fenced(nameof(Notify))) return;
            owner.Notifier?.Invoke(pluginName, title, body);
        }

        public bool OpenUrl(string url)
        {
            if (Fenced(nameof(OpenUrl))) return false;
            return WebLinks.IsWebUrl(url) && owner.UrlOpener?.Invoke(pluginName, url) == true;
        }

        // ---- SDK 3 ----

        public void Invalidate(string pageId)
        {
            if (Fenced(nameof(Invalidate))) return;
            if (owner.FindPage(pluginId, pageId) is null) return;
            owner.PageInvalidated?.Invoke(pluginId, pageId);
        }

        public IPluginDownloads? Downloads
        {
            get
            {
                if (Fenced(nameof(Downloads))) return PausedDownloads.Instance;
                if (owner.DownloadCenter is not { } center) return null;
                return _downloads ??= new PluginDownloads(center, pluginId, pluginName);
            }
        }

        public IPluginNetwork? Network
        {
            get
            {
                if (Fenced(nameof(Network))) return PausedNetwork.Instance;
                return owner.Network;
            }
        }

        public void PublishSensors(IReadOnlyList<PluginSensorReading>? readings)
        {
            if (Fenced(nameof(PublishSensors)))
            {
                // A paused plugin's numbers are not handed out (it may not reach its source any more).
                owner.SensorBoard.Forget(pluginId);
                return;
            }
            owner.SensorBoard.Publish(pluginId, pluginName, readings);
        }

        public PluginSensorSnapshot? ReadSensors()
        {
            if (Fenced(nameof(ReadSensors))) return null;
            return owner.SensorBoard.Read(owner.IsActive);
        }

        public async Task<PluginCliResult> RunTrustedCliAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Fenced(nameof(RunTrustedCliAsync))) return new PluginCliResult(null, "", false, "paused");
            var confirmed = owner.Trust?.Entry(grant.Folder)?.ConfirmedExes ?? [];
            var refused = owner.Host.CheckCli(exePath, arguments ?? [], timeout, grant.Origin, owner.IsLocked?.Invoke() ?? true, confirmed, out var canonical);
            if (refused is not null)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: command line refused ({refused})");
                return new PluginCliResult(null, "", false, refused);
            }
            if (owner.CliRunner is not { } run) return new PluginCliResult(null, "", false, "unsupported");
            return await run(pluginName, canonical!, [.. arguments!], timeout, cancellationToken);
        }

        public Task<bool> RunSetupAsync(string path, string sha256, CancellationToken cancellationToken)
        {
            if (Fenced(nameof(RunSetupAsync))) return Task.FromResult(false);
            return owner.SetupRunner is { } run ? run(pluginId, pluginName, path, sha256, cancellationToken) : Task.FromResult(false);
        }

        public bool RestrictWhileLocked(string exePath)
        {
            if (Fenced(nameof(RestrictWhileLocked))) return false;
            return owner.Host.Restrict(pluginId, exePath);
        }

        public bool AdaptStartupArgs(string exePath, Func<string, string?> adapt)
        {
            if (Fenced(nameof(AdaptStartupArgs))) return false;
            return adapt is not null && owner.Host.AddStartupAdapter(pluginId, exePath, adapt);
        }
    }

    /// <summary>
    /// CouchHUD's own assemblies besides the SDK. No plugin gets them, CouchHUD's own included (they need only the SDK): with the host's
    /// types a plugin could reach past its capabilities (the trust store, the services). PluginLint refuses a reference to them before the
    /// install; this is the second wall at load time. Not a sandbox: code in the same process can still use reflection.
    /// </summary>
    internal static readonly IReadOnlySet<string> HostAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CouchHud.Core", "CouchHud.Shell", "CouchHud.Service", "CouchHud.Service.Contracts", "CouchHud.ServiceClient", "CouchHud.Privileged"
    };

    /// <param name="firstParty">CouchHUD's own plugins may share what the host ships (WebView2 and the like); every other plugin gets from
    /// the default context only the SDK and the .NET shared framework, and brings anything else itself.</param>
    /// <param name="files">The files that were hashed (<see cref="PluginFolderSnapshot"/>): nothing else is loaded, so a file that shows up
    /// in the folder after the check never runs. Null (tests of the context alone): no such limit.</param>
    // Collectible: the context can be let go when the plugin is stopped (Unload), which frees its memory and, once the runtime has let go of the
    // mapped files, the folder. It is a request, not a promise: a plugin that keeps a thread, a timer or a handler on a host object keeps it alive.
    internal sealed class PluginLoadContext(string mainAssembly, bool firstParty, PluginFolderSnapshot? files) : AssemblyLoadContext(isCollectible: true)
    {
        public PluginLoadContext(string mainAssembly, bool firstParty) : this(mainAssembly, firstParty, null)
        {
        }

        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share the SDK so IPlugin is the same type on both sides.
            if (string.Equals(name.Name, typeof(IPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
            // Returning null would hand out the host's copy from the default context: refused instead (also a copy next to the plugin).
            if (name.Name is { } n && HostAssemblies.Contains(n)) throw new FileLoadException(Strings.Get("plugin.hostAssembly", n), n);
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is not null)
            {
                // Only what was hashed: a library that was put into the folder after the check is not loaded.
                if (files is not null && !files.Contains(path)) throw new FileLoadException(Strings.Get("plugin.fileNotChecked", name.Name ?? "?"), name.Name);
                return LoadChecked(path);
            }
            // Null falls through to the default context: for anything but CouchHUD's own plugins only the shared framework, never one of
            // the host's own libraries (WebView2, QRCoder, ServiceController, ...).
            if (!firstParty && !SharedFramework.Contains(name.Name)) throw new FileLoadException(Strings.Get("plugin.hostLibrary", name.Name ?? "?"), name.Name);
            return null;
        }

        /// <summary>Loads an assembly of the plugin and, when the files are held, fences its native imports to the checked files (see <see cref="ResolveNative"/>).</summary>
        internal Assembly LoadChecked(string path)
        {
            var assembly = LoadFromAssemblyPath(path);
            if (files is not null)
            {
                try
                {
                    System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(assembly, ResolveNative);
                }
                catch (InvalidOperationException)
                {
                    // Already set (the same assembly twice): the first resolver is ours.
                }
            }
            return assembly;
        }

        private (NativeLookup Result, string? Path) Locate(string name) =>
            PluginNativeRules.Locate(name, Path.GetDirectoryName(Path.GetFullPath(mainAssembly))!, _resolver.ResolveUnmanagedDllToPath(name), files!.Contains, File.Exists);

        /// <summary>
        /// The import resolver of every plugin assembly: a library from the plugin folder only when it was hashed; a file that appeared in the
        /// folder afterwards is refused; anything else only as a system library by plain name from System32 (other developers' plugins), never
        /// the runtime's own search of the assembly folder, PATH or the current directory. Returning "nothing" would fall back to that search,
        /// so every refusal throws.
        /// </summary>
        private IntPtr ResolveNative(string name, Assembly assembly, System.Runtime.InteropServices.DllImportSearchPath? searchPath)
        {
            var (result, path) = Locate(name);
            if (result == NativeLookup.Refused) throw new DllNotFoundException(Strings.Get("plugin.fileNotChecked", PluginSettingRules.LogText(name)));
            if (result == NativeLookup.Snapshot)
            {
                EnsureFolderUnchanged(name);
                // Windows looks for the dependencies of a library loaded by a full path in the library's own folder first: restricted to that
                // folder (the files of which were just checked) and System32, never the application folder, PATH or the working folder.
                var handle = System.Runtime.InteropServices.NativeLibrary.Load(path!, assembly, System.Runtime.InteropServices.DllImportSearchPath.UseDllDirectoryForDependencies | System.Runtime.InteropServices.DllImportSearchPath.System32);
                LoadedNative = true;
                return handle;
            }
            if (firstParty)
            {
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, assembly, searchPath, out var own)) return own;
            }
            else if (PluginNativeRules.IsPlainName(name) && System.Runtime.InteropServices.NativeLibrary.TryLoad(name, assembly, System.Runtime.InteropServices.DllImportSearchPath.System32, out var system))
            {
                return system;
            }
            throw new DllNotFoundException(Strings.Get("plugin.fileNotChecked", PluginSettingRules.LogText(name)));
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            if (files is null)
            {
                var plain = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                return plain is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(plain);
            }
            var (result, path) = Locate(unmanagedDllName);
            if (result == NativeLookup.Refused) throw new DllNotFoundException(Strings.Get("plugin.fileNotChecked", PluginSettingRules.LogText(unmanagedDllName)));
            if (result != NativeLookup.Snapshot) return IntPtr.Zero;
            EnsureFolderUnchanged(unmanagedDllName);
            var loaded = LoadUnmanagedDllFromPath(path!);
            LoadedNative = loaded != IntPtr.Zero || LoadedNative;
            return loaded;
        }

        /// <summary>
        /// True once the plugin loaded a native library of its own. The runtime cannot take a native module back while anything may still use
        /// it, so such a plugin is never started a second time in the same process (see <see cref="PluginStop.NativeModule"/>).
        /// </summary>
        internal bool LoadedNative { get; private set; }

        /// <summary>For tests (a native file is not needed to check what follows a loaded one).</summary>
        internal void MarkNativeLoaded() => LoadedNative = true;

        /// <summary>
        /// Before a native library is loaded: no file may lie in the plugin folder tree that is not part of the snapshot. Windows resolves the
        /// library's own dependencies itself; this narrows what it could find there (it does not close it: see SECURITY.md). Fail closed.
        /// </summary>
        private void EnsureFolderUnchanged(string name)
        {
            foreach (var file in Directory.EnumerateFiles(files!.Directory, "*", SearchOption.AllDirectories))
            {
                if (PluginTrust.Ignored(Path.GetFileName(file)) || files.Contains(file)) continue;
                Console.Error.WriteLine($"[plugins] native library {PluginSettingRules.LogLine(name)} not loaded: {PluginSettingRules.LogLine(Path.GetFileName(file))} lies in the plugin folder and was not checked");
                throw new DllNotFoundException(Strings.Get("plugin.fileNotChecked", PluginSettingRules.LogLine(Path.GetFileName(file))));
            }
        }
    }

    /// <summary>
    /// The state of one plugin instance's context: once it is dead (the plugin was stopped, or failed to start) every call it makes into the host
    /// is ignored, quietly and without an exception (a thread of the plugin that runs on must not be crashed by the host). Per instance, not per
    /// id: a plugin that is started again later is a new instance, and a leftover thread of the old one stays cut off.
    /// </summary>
    internal sealed class PluginLife
    {
        private volatile bool _dead;
        private readonly List<Action> _onKill = [];

        public bool Dead => _dead;

        public void Kill()
        {
            List<Action> run;
            lock (_onKill)
            {
                if (_dead) return;
                _dead = true;
                run = [.. _onKill];
                _onKill.Clear();
            }
            foreach (var action in run)
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"[plugins] cutting off failed: {PluginSettingRules.LogLine(ex.Message)}");
                }
            }
        }

        /// <summary>Runs when the instance is cut off (at once when it already is).</summary>
        public void OnKill(Action action)
        {
            bool now;
            lock (_onKill)
            {
                now = _dead;
                if (!now) _onKill.Add(action);
            }
            if (now) action();
        }
    }

    /// <summary>What a start tells the caller besides success: the files must stay held (the plugin could not be stopped).</summary>
    internal sealed class StartAttempt
    {
        public bool KeepFiles { get; set; }
    }

    /// <summary>The plugin id belongs to a run that could not be stopped: it starts with the next start.</summary>
    internal sealed class RestartRequiredException : Exception
    {
    }

    /// <summary>What runs for one plugin folder: the load context, the plugin objects (one per id) and the files held for it.</summary>
    internal sealed class RunningPlugin(string folder, PluginLoadContext context, PluginFolderSnapshot? files)
    {
        public string Folder { get; } = folder;
        public PluginLoadContext Context { get; } = context;
        public PluginFolderSnapshot? Files { get; } = files;
        public List<string> Ids { get; } = [];
        public List<IPlugin> Instances { get; } = [];
        public List<PluginLife> Lives { get; } = [];

        public void Add(string id, IPlugin plugin, PluginLife life)
        {
            Ids.Add(id);
            Instances.Add(plugin);
            Lives.Add(life);
        }
    }
}

/// <summary>Raised by the host; a throwing plugin handler is logged and skipped.</summary>
public sealed class PluginEvents : IPluginEvents
{
    public event Action<GameEvent>? GameStarted;
    public event Action<GameEvent>? GameEnded;
    public event Action? PcModeEntered;
    public event Action? CouchModeEntered;
    public event Action<string>? LanguageChanged;
    public event Action<bool>? LockChanged;
    public event Action? HostStopping;

    /// <summary>How long the host waits for the plugins' <see cref="IPluginEvents.HostStopping"/> handlers (all together) before it quits regardless.</summary>
    public static readonly TimeSpan StoppingTimeout = TimeSpan.FromSeconds(2);

    private int _stopping;

    /// <summary>
    /// CouchHUD is about to quit: the handlers run side by side on the pool and the call returns when they are done or after
    /// <paramref name="timeout"/> (default <see cref="StoppingTimeout"/>), whichever is first; a handler that hangs or throws holds nothing back.
    /// Only the first call does anything. Returns how many handlers had not finished in time.
    /// </summary>
    public int RaiseHostStopping(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return 0;
        var tasks = Dispatch(HostStopping, h => h(), "stopping handler");
        if (tasks.Count == 0) return 0;
        try
        {
            Task.WaitAll([.. tasks], timeout ?? StoppingTimeout);
        }
        catch (AggregateException)
        {
            // Dispatch swallows what a handler throws; nothing else can fail here.
        }
        return tasks.Count(t => !t.IsCompleted);
    }

    public void RaiseGameStarted(GameEvent e) => Raise(GameStarted, h => h(e));
    public void RaiseGameEnded(GameEvent e) => Raise(GameEnded, h => h(e));
    public void RaisePcModeEntered() => Raise(PcModeEntered, h => h());
    public void RaiseCouchModeEntered() => Raise(CouchModeEntered, h => h());
    public void RaiseLanguageChanged(string language) => Raise(LanguageChanged, h => h(language));
    public void RaiseLockChanged(bool locked) => Raise(LockChanged, h => h(locked));

    private static void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate => Dispatch(handlers, invoke, "event handler");

    /// <summary>Each handler on the pool, off the caller's thread (a slow plugin must not stall the HUD), a failing one logged and skipped.</summary>
    internal static List<Task> Dispatch<T>(T? handlers, Action<T> invoke, string what) where T : Delegate
    {
        var tasks = new List<Task>();
        if (handlers is null) return tasks;
        foreach (var h in handlers.GetInvocationList().Cast<T>())
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    invoke(h);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[plugins] {what} failed: {ex.Message}");
                }
            }));
        }
        return tasks;
    }
}

/// <summary>The downloads of a paused plugin (Offline mode): nothing is shown, nothing throws, so a background thread of the plugin keeps running without a null reference.</summary>
internal sealed class PausedDownloads : IPluginDownloads
{
    public static readonly PausedDownloads Instance = new();

    public void Begin(string id, string label, bool isUpdate = false) { }

    public void Report(string id, double? progress = null, string? detail = null, long? bytesDone = null, long? bytesTotal = null) { }

    public void Complete(string id, bool ok, string? detail = null) { }
}

/// <summary>The network state of a paused plugin: no adapters, so nothing is up and there is no default route.</summary>
internal sealed class PausedNetwork : IPluginNetwork
{
    public static readonly PausedNetwork Instance = new();

    public IReadOnlyList<PluginNetworkAdapter> Adapters() => [];
}

/// <summary>A plugin's library provider while Offline mode may pause the plugin: paused, it is not asked and lists nothing.</summary>
internal sealed class PausableProvider(ILibraryProvider inner, Func<bool> paused, string pluginId = "") : ILibraryProvider
{
    public string Name => inner.Name;

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        paused() ? Task.FromResult<IReadOnlyList<LibraryItem>>([]) : Stamp(inner.GetItemsAsync(cancellationToken));

    /// <summary>Id prefixes of the core's entries (and of the limit keys): a plugin's entry never carries one.</summary>
    private static readonly string[] CorePrefixes = ["plugin:", "steam:", "dir:"];

    private async Task<IReadOnlyList<LibraryItem>> Stamp(Task<IReadOnlyList<LibraryItem>> items)
    {
        var all = await items.ConfigureAwait(false);
        var kept = all.Where(i => i.Id is not null && !CorePrefixes.Any(p => i.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (kept.Count < all.Count) Console.Error.WriteLine($"[plugins] {pluginId}: {all.Count - kept.Count} entr{(all.Count - kept.Count == 1 ? "y" : "ies")} with an id of the core's kind skipped");
        return kept.Select(i => i.LimitGroup is null ? i : i with { LimitGroup = LimitGroupOf(pluginId, i.LimitGroup) }).ToList();
    }

    /// <summary>The group with the plugin id in front, or null when it is empty, longer than 64 characters or has control characters (or there is no plugin id).</summary>
    public static string? LimitGroupOf(string pluginId, string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || pluginId.Length == 0 || group.Length > 64 || group.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')) return null;
        return pluginId.ToLowerInvariant() + ":" + group;
    }
}

/// <summary>The events as one plugin sees them: its handlers are called only while it is not paused (Offline mode).</summary>
internal sealed class PausableEvents(IPluginEvents inner, Func<bool> paused) : IPluginEvents
{
    // One wrapper per handler and how often it is subscribed: subscribing twice and unsubscribing twice leaves nothing behind.
    private readonly Dictionary<Delegate, (Delegate Wrapper, int Count)> _wrapped = [];
    private readonly List<Action> _undo = [];
    private bool _closed;

    internal int Tracked
    {
        get
        {
            lock (_wrapped) return _wrapped.Count;
        }
    }

    /// <summary>The plugin was cut off: every subscription it made is taken back from the host's events and no new one is taken (nothing of the plugin stays referenced from the host).</summary>
    internal void Close()
    {
        List<Action> undo;
        lock (_wrapped)
        {
            if (_closed) return;
            _closed = true;
            undo = [.. _undo];
            _undo.Clear();
            _wrapped.Clear();
        }
        foreach (var action in undo) action();
    }

    private bool Subscribe<T>(T handler, Func<T, T> make, Action<T> add, Action<T> remove) where T : Delegate
    {
        T wrapper;
        lock (_wrapped)
        {
            if (_closed) return false;
            var entry = _wrapped.TryGetValue(handler, out var known) ? known : (Wrapper: make(handler), Count: 0);
            _wrapped[handler] = (entry.Wrapper, entry.Count + 1);
            wrapper = (T)entry.Wrapper;
            _undo.Add(() => remove(wrapper));
            // Inside the lock: Close takes the same lock, so a subscription is either made and taken back by Close, or not made at all.
            add(wrapper);
        }
        return true;
    }

    private void Unsubscribe<T>(T handler, Action<T> remove) where T : Delegate
    {
        T wrapper;
        lock (_wrapped)
        {
            if (!_wrapped.TryGetValue(handler, out var entry)) return;
            if (entry.Count <= 1) _wrapped.Remove(handler);
            else _wrapped[handler] = (entry.Wrapper, entry.Count - 1);
            wrapper = (T)entry.Wrapper;
        }
        remove(wrapper);
    }

    public event Action<GameEvent>? GameStarted
    {
        add => Subscribe(value!, h => e => { if (!paused()) h(e); }, w => inner.GameStarted += w, w => inner.GameStarted -= w);
        remove => Unsubscribe(value!, w => inner.GameStarted -= w);
    }

    public event Action<GameEvent>? GameEnded
    {
        add => Subscribe(value!, h => e => { if (!paused()) h(e); }, w => inner.GameEnded += w, w => inner.GameEnded -= w);
        remove => Unsubscribe(value!, w => inner.GameEnded -= w);
    }

    public event Action? PcModeEntered
    {
        add => Subscribe(value!, h => () => { if (!paused()) h(); }, w => inner.PcModeEntered += w, w => inner.PcModeEntered -= w);
        remove => Unsubscribe(value!, w => inner.PcModeEntered -= w);
    }

    public event Action? CouchModeEntered
    {
        add => Subscribe(value!, h => () => { if (!paused()) h(); }, w => inner.CouchModeEntered += w, w => inner.CouchModeEntered -= w);
        remove => Unsubscribe(value!, w => inner.CouchModeEntered -= w);
    }

    public event Action<string>? LanguageChanged
    {
        add => Subscribe(value!, h => l => { if (!paused()) h(l); }, w => inner.LanguageChanged += w, w => inner.LanguageChanged -= w);
        remove => Unsubscribe(value!, w => inner.LanguageChanged -= w);
    }

    public event Action<bool>? LockChanged
    {
        add => Subscribe(value!, h => l => { if (!paused()) h(l); }, w => inner.LockChanged += w, w => inner.LockChanged -= w);
        remove => Unsubscribe(value!, w => inner.LockChanged -= w);
    }

    public event Action? HostStopping
    {
        add => Subscribe(value!, h => () => { if (!paused()) h(); }, w => inner.HostStopping += w, w => inner.HostStopping -= w);
        remove => Unsubscribe(value!, w => inner.HostStopping -= w);
    }
}

/// <summary>The settings a plugin sees: after its stop it reads nothing and writes nothing, and the store behind is not even looked up (so it is not created again).</summary>
internal sealed class GuardedSettings(Func<bool> dead, Func<IPluginSettings> store) : IPluginSettings
{
    public string? Get(string key) => dead() ? null : store().Get(key);

    public bool GetBool(string key, bool fallback = false) => dead() ? fallback : store().GetBool(key, fallback);

    public void Set(string key, string? value)
    {
        if (!dead()) store().Set(key, value);
    }

    public void SetSecret(string key, string? value)
    {
        if (!dead()) store().SetSecret(key, value);
    }

    /// <summary>The key of a changed value (see <see cref="IPluginSettings.SettingChanged"/>); a stopped plugin hears nothing and does not even look the store up.</summary>
    public event Action<string>? SettingChanged
    {
        add
        {
            if (!dead()) store().SettingChanged += value;
        }
        remove
        {
            if (!dead()) store().SettingChanged -= value;
        }
    }
}

/// <summary>The sign-in of one plugin; the checks of the addresses are the shell's (it knows the browser).</summary>
internal sealed class PluginSignIn(PluginManager owner, string pluginId, string pluginName) : IPluginSignIn
{
    public bool Open(string pageUrl, string redirectHost, Action<string, string> onRedirect) =>
        onRedirect is not null && !owner.IsPaused(pluginId) && owner.SignInOpener?.Invoke(pluginId, pluginName, pageUrl, redirectHost, onRedirect) == true;

    public void Cancel() => owner.SignInCanceller?.Invoke(pluginId);
}

/// <summary>A plugin's settings as a small JSON file in its data folder.</summary>
/// <param name="seed">Values to start with when the plugin has no settings file yet; written once, then <paramref name="seedApplied"/> is called.</param>
/// <param name="protect">Encrypts a secret for the file; null: secrets are not kept.</param>
/// <param name="unprotect">Decrypts a stored secret; null: a stored secret reads as empty.</param>
internal sealed class SettingsStore(string file, Func<IEnumerable<PluginSetting>> declared, IReadOnlyDictionary<string, string?>? seed = null, Action? seedApplied = null,
    Func<string, string>? protect = null, Func<string, string>? unprotect = null) : IPluginSettings
{
    /// <summary>A secret in the file starts with this (the shell's DPAPI text); everything else is plain.</summary>
    internal const string SecretPrefix = "dpapi:";

    private readonly object _lock = new();
    private Dictionary<string, string?>? _values;

    private Action<string>? _changed;

    /// <summary>A value changed (the key only). Handlers run on the pool, each failing alone; see <see cref="IPluginSettings.SettingChanged"/>.</summary>
    public event Action<string>? SettingChanged
    {
        add { lock (_lock) _changed += value; }
        remove { lock (_lock) _changed -= value; }
    }

    private void RaiseChanged(string key)
    {
        Action<string>? handlers;
        lock (_lock) handlers = _changed;
        PluginEvents.Dispatch(handlers, h => h(key), "setting handler");
    }

    public string? Get(string key)
    {
        lock (_lock)
        {
            Load();
            if (_values!.TryGetValue(key, out var v)) return v is not null && v.StartsWith(SecretPrefix, StringComparison.Ordinal) ? unprotect?.Invoke(v) ?? "" : v;
        }
        return declared().FirstOrDefault(s => s.Key == key)?.Default;
    }

    public bool GetBool(string key, bool fallback = false) => Get(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    /// <summary>A setting the plugin declared as <see cref="SettingKind.Secret"/> is kept encrypted, whichever way it is set (also by the settings page).</summary>
    public void Set(string key, string? value) =>
        Store(key, value, declared().Any(s => s.Key == key && s.Kind == SettingKind.Secret));

    public void SetSecret(string key, string? value) => Store(key, value, true);

    /// <summary>Takes every stored secret out of the values and the file (the plugin was removed); the other settings stay for a reinstall. How many went.</summary>
    internal int DropSecrets()
    {
        lock (_lock)
        {
            // Read the file as it is, without a seed: a removed plugin gets nothing written for it.
            Dictionary<string, string?> values;
            try
            {
                values = _values ?? (File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file)) ?? new() : new());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Unreadable: nothing can be told apart, so nothing of it stays.
                Console.Error.WriteLine($"[plugins] {file}: unreadable, removed with the plugin ({ex.GetType().Name})");
                _values = new();
                TryDelete();
                return 0;
            }
            var secrets = values.Where(v => v.Value is not null && v.Value.StartsWith(SecretPrefix, StringComparison.Ordinal)).Select(v => v.Key).ToList();
            if (secrets.Count == 0) return 0;
            foreach (var key in secrets) values.Remove(key);
            _values = values;
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(values));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Could not write it without the secrets: the file goes as a whole (fail closed, no key stays behind).
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
                TryDelete();
            }
            return secrets.Count;
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[plugins] {file}: not removed ({ex.Message})");
        }
    }

    /// <summary>Longest value a plugin may keep (characters), and the largest settings file (bytes). A plugin's settings are small; a runaway one must not fill the disk.</summary>
    internal const int MaxValueLength = 64 * 1024;

    internal const int MaxFileBytes = 1024 * 1024;

    private void Store(string key, string? value, bool secret)
    {
        if (key is null or { Length: 0 or > 200 } || (value is not null && value.Length > MaxValueLength))
        {
            Console.Error.WriteLine($"[plugins] {file}: a setting was refused (too long)");
            return;
        }
        var before = Get(key);
        var plain = value;
        // A secret is never written as plain text: without a protector it is not kept at all. A protector that fails is a refusal,
        // not an exception thrown into the plugin (and the old value stays).
        if (secret && !string.IsNullOrEmpty(value))
        {
            try
            {
                value = protect is null ? null : protect(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[plugins] {file}: a secret was not kept ({ex.GetType().Name})");
                return;
            }
            if (value is not null && value.Length > MaxValueLength) return;
        }
        // A plain value that looks like a stored secret would be read as one: it is not kept.
        else if (value is not null && value.StartsWith(SecretPrefix, StringComparison.Ordinal)) value = null;
        lock (_lock)
        {
            Load();
            var next = new Dictionary<string, string?>(_values!) { [key] = value };
            var json = JsonSerializer.Serialize(next);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            {
                Console.Error.WriteLine($"[plugins] {file}: the settings are full, a setting was refused");
                return;
            }
            _values = next;
            try
            {
                File.WriteAllText(file, json);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            }
        }
        if (!string.Equals(before, plain, StringComparison.Ordinal)) RaiseChanged(key);
    }

    private void Load()
    {
        if (_values is not null) return;
        if (seed is { Count: > 0 } && !File.Exists(file))
        {
            _values = new Dictionary<string, string?>(seed);
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(_values));
                seedApplied?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            }
            return;
        }
        try
        {
            _values = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _values = new();
        }
        // The settings file exists but the legacy key is still in the config (the first start wrote the file and could not clear the config, or the
        // plugin was installed before the key was seeded): a key the plugin does not have yet moves in, one it has is never overwritten, and
        // then the legacy copy may go.
        if (seed is { Count: > 0 } && File.Exists(file)) MergeSecretSeed();
    }

    private void MergeSecretSeed()
    {
        var secrets = seed!.Where(kv => kv.Value is not null && kv.Value.StartsWith(SecretPrefix, StringComparison.Ordinal)).ToList();
        if (secrets.Count == 0) return;
        var next = new Dictionary<string, string?>(_values!);
        var changed = false;
        foreach (var (key, value) in secrets)
        {
            if (next.TryGetValue(key, out var current) && !string.IsNullOrEmpty(current)) continue;
            next[key] = value;
            changed = true;
        }
        if (changed)
        {
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(next));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
                return;
            }
            _values = next;
        }
        seedApplied?.Invoke();
    }
}
