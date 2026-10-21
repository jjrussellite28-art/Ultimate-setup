using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Assistant;
using CouchHud.Sdk;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>What plugin.json says about a plugin; everything optional.</summary>
/// <summary>
/// plugin.json. <c>description</c> is a text, or an object per language: <c>{ "de": "…", "en": "…" }</c>.
/// </summary>
public sealed record PluginManifest(string? Name, string? Version, string? Author, LocalizedText? Description, string? Homepage, int Sdk = 1);

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

    /// <summary>True if the folder was installed before and its files are not the installed ones any more.</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Loads plugins from plugins/&lt;name&gt;/ folders. Each folder is one plugin with its own
/// load context, so plugins can ship their own dependency versions; only the SDK
/// assembly is shared with the host. The entry assembly is &lt;name&gt;.dll; other
/// DLLs in the folder are dependencies and are resolved on demand.
/// A broken plugin is reported, never fatal, and every call into it has a timeout.
/// </summary>
public sealed class PluginManager(IReadOnlyList<(string Dir, bool UserInstalled)> pluginDirs, string dataDir, Func<string, bool> isEnabled)
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly List<PluginInfo> _infos = [];
    private readonly List<ILibraryProvider> _providers = [];
    private readonly List<(string Plugin, PluginPage Page)> _pages = [];
    private readonly List<(string Plugin, QuickAction Action)> _actions = [];
    private readonly List<(string Plugin, PluginSetting Setting)> _settings = [];
    private readonly List<(string Plugin, PluginTool Tool, ToolSpec Spec)> _tools = [];
    private readonly Dictionary<string, SettingsStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public PluginEvents Events { get; } = new();

    /// <summary>Hashes of installed plugins; a user plugin that is not (or no longer) in it waits for approval. Null: no check (tests, tools).</summary>
    public PluginTrust? Trust { get; init; }

    /// <summary>Asked by plugins (<see cref="IPluginContext.Locked"/>): is the couch locked right now?</summary>
    public Func<bool>? IsLocked { get; set; }

    /// <summary>Set by the host: (plugin name, title, body).</summary>
    public Action<string, string, string>? Notifier { get; set; }

    /// <summary>Set by the host: opens an http(s) address in the couch browser (plugin name, address); false when refused.</summary>
    public Func<string, string, bool>? UrlOpener { get; set; }

    // Plugins may load on a background thread while the UI is already up: lists are only touched under this lock.
    private readonly object _gate = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when all plugins are loaded (or failed to).</summary>
    public Task Loaded => _loaded.Task;

    public IReadOnlyList<PluginInfo> Plugins => Snapshot(_infos)
        .Select(i => i.DescriptionText is { } d ? i with { Description = d.For(Strings.Language) } : i).ToList();
    public IReadOnlyList<ILibraryProvider> LibraryProviders => Snapshot(_providers);
    public IReadOnlyList<(string Plugin, PluginPage Page)> Pages => Snapshot(_pages);
    public IReadOnlyList<(string Plugin, QuickAction Action)> QuickActions => Snapshot(_actions);
    public IReadOnlyList<(string Plugin, PluginSetting Setting)> Settings => Snapshot(_settings);

    /// <summary>Plugin tools as the assistant sees them, names prefixed with the plugin id.</summary>
    public IReadOnlyList<ToolSpec> ToolSpecs => Snapshot(_tools).Select(t => t.Spec).ToList();

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_gate) return [.. list];
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_gate) list.Add(item);
    }

    /// <summary>Puts the item where the one it replaces was (same place in the lists), or adds it.</summary>
    private void Replace<T>(List<T> list, T item, Predicate<T> same)
    {
        lock (_gate)
        {
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
            LoadAllCore();
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    private void LoadAllCore()
    {
        var overridden = OverriddenBundledFolders(pluginDirs, Trust is null ? null : (folder, dir) => Trust.Check(folder, dir) == PluginTrustState.Trusted);
        foreach (var (pluginsDir, userInstalled) in pluginDirs)
        {
            if (!Directory.Exists(pluginsDir)) continue;
            foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
            {
                var folder = Path.GetFileName(dir);
                // Leftovers of an interrupted install (.couchhud-new-…) are not plugins.
                if (folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
                if (!userInstalled && overridden.Contains(folder)) continue;
                var manifest = ReadManifest(dir);
                var name = manifest?.Name ?? folder;
                if (userInstalled && Trust is not null && Trust.Check(folder, dir) is var trust && trust != PluginTrustState.Trusted)
                {
                    // Dropped into the folder by something else, or changed since: not run until the user says so.
                    Console.Error.WriteLine($"[plugins] {folder}: {trust}, waiting for approval");
                    if (trust == PluginTrustState.Fault)
                    {
                        // The list itself cannot be trusted: nothing to approve against until it is repaired or removed.
                        Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get("plugin.trustFault"), userInstalled));
                        continue;
                    }
                    var code = PluginTrust.ShortCode(SafeHash(dir));
                    Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get(trust == PluginTrustState.Changed ? "plugin.changed" : "plugin.unknown"), userInstalled)
                        with { NeedsApproval = true, ApprovalCode = code, Changed = trust == PluginTrustState.Changed });
                    continue;
                }
                if (!isEnabled(folder))
                {
                    Add(_infos, Info(folder, folder, name, manifest, enabled: false, loaded: false, null, userInstalled));
                    continue;
                }
                try
                {
                    if (manifest is not null && manifest.Sdk > SdkVersion.Current)
                    {
                        throw new InvalidOperationException(Strings.Get("plugin.needsNewer", manifest.Sdk));
                    }
                    var entry = FindEntryAssembly(dir) ?? throw new InvalidOperationException(Strings.Get("plugin.dllMissing", folder));
                    foreach (var id in LoadAssembly(entry, name))
                    {
                        Add(_infos, Info(folder, id, name, manifest, enabled: true, loaded: true, null, userInstalled));
                    }
                }
                catch (Exception ex)
                {
                    // A broken plugin must never keep the shell from starting.
                    Console.Error.WriteLine($"[plugins] {dir}: {ex.Message}");
                    Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, ex.Message, userInstalled));
                }
            }
        }
    }

    /// <summary>
    /// Bundled plugin folders that a user copy replaces (catalog update). The copy only counts if it has a
    /// DLL and is not older than the bundled one; otherwise the bundled plugin loads and the reason is logged.
    /// </summary>
    internal static HashSet<string> OverriddenBundledFolders(IReadOnlyList<(string Dir, bool UserInstalled)> pluginDirs, Func<string, string, bool>? isTrusted = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundled = pluginDirs.Where(d => !d.UserInstalled && Directory.Exists(d.Dir)).SelectMany(d => Directory.EnumerateDirectories(d.Dir)).ToList();
        foreach (var user in pluginDirs.Where(d => d.UserInstalled && Directory.Exists(d.Dir)).SelectMany(d => Directory.EnumerateDirectories(d.Dir)))
        {
            var folder = Path.GetFileName(user);
            var original = bundled.FirstOrDefault(b => Path.GetFileName(b).Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (original is null) continue;
            string? why = null;
            // A copy that waits for approval does not load, so it must not push the bundled plugin out either.
            if (isTrusted is not null && !isTrusted(folder, user)) why = Strings.Get("plugin.unknown");
            else if (!Directory.EnumerateFiles(user, "*.dll").Any()) why = Strings.Get("plugin.noDll");
            else
            {
                var have = SemVer.TryParse(ReadManifest(user)?.Version, out var u) ? u : (SemVer?)null;
                var shipped = SemVer.TryParse(ReadManifest(original)?.Version, out var b) ? b : (SemVer?)null;
                if (shipped is not null && (have is null || have.Value < shipped.Value)) why = Strings.Get("plugin.olderThanShipped", ReadManifest(user)?.Version ?? Strings.Get("plugin.noVersion"), shipped);
            }
            if (why is null) result.Add(folder);
            else Console.Error.WriteLine($"[plugins] user copy of {folder} ignored, bundled one loads: {why}");
        }
        return result;
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

    /// <summary>The user said yes to this folder: its files are remembered as they are now, and it loads at the next start.</summary>
    public void Approve(string folder)
    {
        if (Trust is null) return;
        if (folder.Length == 0 || folder.Trim('.').Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        var dir = pluginDirs.Where(d => d.UserInstalled).Select(d => Path.Combine(d.Dir, folder)).FirstOrDefault(Directory.Exists)
                  ?? throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        Trust.Trust(folder, dir);
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

    private List<string> LoadAssembly(string dll, string name)
    {
        var context = new PluginLoadContext(dll);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var types = assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

        var ids = new List<string>();
        foreach (var type in types)
        {
            var plugin = (IPlugin)Activator.CreateInstance(type)!;
            plugin.Initialize(new Context(this, plugin.Id, name));
            ids.Add(plugin.Id);
            Console.WriteLine($"[plugins] loaded {plugin.Id} from {Path.GetFileName(dll)}");
        }
        if (ids.Count == 0) throw new InvalidOperationException(Strings.Get("plugin.noIPlugin"));
        return ids;
    }

    public IPluginSettings SettingsOf(string pluginId)
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(pluginId, out var store))
            {
                store = new SettingsStore(Path.Combine(PluginDataDir(pluginId), "settings.json"), () => Snapshot(_settings).Where(s => s.Plugin == pluginId).Select(s => s.Setting));
                _stores[pluginId] = store;
            }
            return store;
        }
    }

    private string PluginDataDir(string pluginId)
    {
        var dir = Path.Combine(dataDir, "plugin-data", pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -----------------------------------------------------------------------
    // Calls into plugins: always with a timeout, errors become messages.

    public static async Task<T> SafeAsync<T>(string what, Func<CancellationToken, Task<T>> call)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        try
        {
            return await Task.Run(() => call(cts.Token), cts.Token).WaitAsync(CallTimeout);
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
        var hit = Snapshot(_pages).FirstOrDefault(p => p.Plugin == pluginId && p.Page.Id == pageId);
        return hit.Page is null ? null : (hit.Page, hit.Plugin);
    }

    public QuickAction? FindAction(string pluginId, string actionId) =>
        Snapshot(_actions).FirstOrDefault(a => a.Plugin == pluginId && a.Action.Id == actionId).Action;

    public bool HasTool(string name) => Snapshot(_tools).Any(t => t.Spec.Name == name);

    public ToolSpec? FindTool(string name) => Snapshot(_tools).FirstOrDefault(t => t.Spec.Name == name).Spec;

    public Task<string> CallToolAsync(string name, JsonElement args)
    {
        var tool = Snapshot(_tools).FirstOrDefault(t => t.Spec.Name == name);
        if (tool.Tool is null) throw new ArgumentException(Strings.Get("plugin.unknownTool", name));
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
        if (action.Detail is not { } detail) return null;
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

    private sealed class Context(PluginManager owner, string pluginId, string pluginName) : IPluginContext
    {
        public string DataDirectory => owner.PluginDataDir(pluginId);

        public IPluginSettings Settings => owner.SettingsOf(pluginId);

        public IPluginEvents Events => owner.Events;

        public void RegisterLibraryProvider(ILibraryProvider provider) => owner.Add(owner._providers, provider);

        public void Log(string message) => Console.WriteLine($"[{pluginId}] {message}");

        public string Language => Strings.Language;

        public bool Locked => owner.IsLocked?.Invoke() ?? false;

        // Registering an id again replaces the earlier one (a plugin re-registers its texts when the language changes).
        public void RegisterSettings(params PluginSetting[] settings)
        {
            foreach (var s in settings) owner.Replace(owner._settings, (Plugin: pluginId, Setting: s), x => x.Plugin == pluginId && x.Setting.Key == s.Key);
        }

        public void RegisterPage(PluginPage page) => owner.Replace(owner._pages, (Plugin: pluginId, Page: page), x => x.Plugin == pluginId && x.Page.Id == page.Id);

        public void RegisterQuickAction(QuickAction action) => owner.Replace(owner._actions, (Plugin: pluginId, Action: action), x => x.Plugin == pluginId && x.Action.Id == action.Id);

        public void RegisterTool(PluginTool tool)
        {
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
            // Sensitive: over MCP even a read-only plugin tool is confirmed on the TV.
            owner.Add(owner._tools, (pluginId, tool, new ToolSpec(name, $"[{pluginName}] {tool.Description}", schema, tool.Writes, Sensitive: true)));
        }

        public void Notify(string title, string body) => owner.Notifier?.Invoke(pluginName, title, body);

        public bool OpenUrl(string url) => WebLinks.IsWebUrl(url) && owner.UrlOpener?.Invoke(pluginName, url) == true;
    }

    private sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share the SDK so IPlugin is the same type on both sides.
            if (name.Name == typeof(IPlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
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

    public void RaiseGameStarted(GameEvent e) => Raise(GameStarted, h => h(e));
    public void RaiseGameEnded(GameEvent e) => Raise(GameEnded, h => h(e));
    public void RaisePcModeEntered() => Raise(PcModeEntered, h => h());
    public void RaiseCouchModeEntered() => Raise(CouchModeEntered, h => h());
    public void RaiseLanguageChanged(string language) => Raise(LanguageChanged, h => h(language));
    public void RaiseLockChanged(bool locked) => Raise(LockChanged, h => h(locked));

    private static void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<T>())
        {
            // Off the caller's thread: a slow plugin must not stall the HUD.
            _ = Task.Run(() =>
            {
                try
                {
                    invoke(h);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[plugins] event handler failed: {ex.Message}");
                }
            });
        }
    }
}

/// <summary>A plugin's settings as a small JSON file in its data folder.</summary>
internal sealed class SettingsStore(string file, Func<IEnumerable<PluginSetting>> declared) : IPluginSettings
{
    private readonly object _lock = new();
    private Dictionary<string, string?>? _values;

    public string? Get(string key)
    {
        lock (_lock)
        {
            Load();
            if (_values!.TryGetValue(key, out var v)) return v;
        }
        return declared().FirstOrDefault(s => s.Key == key)?.Default;
    }

    public bool GetBool(string key, bool fallback = false) => Get(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            Load();
            _values![key] = value;
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(_values));
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            }
        }
    }

    private void Load()
    {
        if (_values is not null) return;
        try
        {
            _values = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _values = new();
        }
    }
}
