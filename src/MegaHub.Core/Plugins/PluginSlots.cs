using MegaHub.Core.Localization;
using MegaHub.Core.Net;
using CouchHud.Sdk;

namespace MegaHub.Core.Plugins;

// SDK 5 on the host's side: the declared network services and their clients, and the slots for the weather, the news and the assistant's
// providers. What a slot holds is only used while its plugin is loaded and not paused; Mega Hub's own plugin wins over any other.
public sealed partial class PluginManager
{
    private readonly List<(string Plugin, IPluginWeather Source)> _weather = [];
    private readonly List<(string Plugin, IPluginNews Source)> _news = [];
    private readonly List<(string Plugin, IPluginChatProvider Provider)> _chat = [];
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by the shell: Offline and the gate below every plugin client. Null: plugins get no client (<see cref="IPluginContext.Http"/> answers null).</summary>
    public NetPolicy? Net { get; set; }

    /// <summary>Set by the shell: is this declared service (plugin id, service id) switched on in Settings › Privacy? Null: every service is on.</summary>
    public Func<string, string, bool>? ServiceAllowed { get; set; }

    /// <summary>The timeout of a plugin client (the assistant's providers wait longest for an answer).</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The most one answer through a plugin client may hold (buffered or streamed); larger ones fail. The news plugin's own cap is 4 MB.</summary>
    public const long MaxResponseBytes = 8L * 1024 * 1024;

    /// <summary>The declared services of the loaded plugins (for Settings › Privacy), own plugins first.</summary>
    public IReadOnlyList<(string Plugin, string PluginName, PluginService Service)> Services
    {
        get
        {
            List<(string, PluginGrant)> grants;
            lock (_gate) grants = [.. _grants.Select(g => (g.Key, g.Value))];
            var names = Snapshot(_infos).Where(i => i.Loaded).ToDictionary(i => i.Id, i => i.Name, StringComparer.OrdinalIgnoreCase);
            return grants
                .Where(g => names.ContainsKey(g.Item1) && HasNetworkCapability(g.Item2))
                .OrderBy(g => PluginPolicy.IsOwn(g.Item2.Origin) ? 0 : 1).ThenBy(g => g.Item1, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => (g.Item2.Services ?? []).Select(s => (g.Item1, names[g.Item1], s)))
                .ToList();
        }
    }

    /// <summary>May the service go out now: not Offline, its switch on, the plugin loaded and not paused.</summary>
    internal bool ServiceOpen(string pluginId, string serviceId) =>
        Net is { Offline: false } && !IsPaused(pluginId) && GrantOf(pluginId) is not null && (ServiceAllowed?.Invoke(pluginId, serviceId) ?? true);

    private HttpClient? ClientFor(string pluginId, PluginGrant grant, string serviceId)
    {
        if (Net is not { } policy || grant.Services?.FirstOrDefault(s => s.Id == serviceId) is not { } service) return null;
        // A service that names only the home network ("lan") has no host this client may reach: Http stays closed for the home network.
        if (!service.Hosts.Any(h => h != PluginNetServices.LanHost)) return null;
        lock (_gate)
        {
            var key = PluginService.Key(pluginId, serviceId);
            if (_clients.TryGetValue(key, out var existing)) return existing;
            var inner = new PluginResponseLimitHandler(MaxResponseBytes, new PluginHttpHandler(pluginId, service, () => ServiceOpen(pluginId, serviceId), SafeNet.CreateHandler()));
            var client = NetGate.CreateClient(NetService.Plugin, policy, HttpTimeout, inner, userAgent: $"MegaHub/{CatalogRules.AppVersion}", followRedirects: false);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            _clients[key] = client;
            return client;
        }
    }

    /// <summary>One source of a slot: Mega Hub's own plugin first, then in the order they registered; none while paused.</summary>
    private (string Plugin, T Item)? Pick<T>(List<(string Plugin, T Item)> list) =>
        Live(list, x => x.Plugin).OrderBy(x => PluginPolicy.IsOwn(OriginOf(x.Plugin)) ? 0 : 1).Select(x => ((string, T)?)x).FirstOrDefault();

    /// <summary>The weather source in use (plugin id, source), or null.</summary>
    public (string Plugin, IPluginWeather Source)? WeatherSource => Pick(_weather);

    /// <summary>The news source in use (plugin id, source), or null.</summary>
    public (string Plugin, IPluginNews Source)? NewsSource => Pick(_news);

    /// <summary>The assistant's providers of the loaded plugins (plugin id, provider), one per provider id.</summary>
    public IReadOnlyList<(string Plugin, IPluginChatProvider Provider)> ChatProviders =>
        Live(_chat, x => x.Plugin).GroupBy(x => x.Provider.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();

    /// <summary>Lower-case letters, digits, "-" and "_", at most 32: a provider id the settings can store.</summary>
    internal static bool ValidProviderId(string? id) => PluginFunctionRules.ValidId(id);

    private sealed partial class Context
    {
        public HttpClient? Http(string serviceId)
        {
            if (Fenced(nameof(Http))) return null;
            return owner.ClientFor(pluginId, grant, serviceId ?? "");
        }

        public void RegisterWeather(IPluginWeather weather)
        {
            if (Fenced(nameof(RegisterWeather)) || weather is null) return;
            owner.Replace(owner._weather, (Plugin: pluginId, Source: weather), x => x.Plugin == pluginId);
        }

        public void RegisterNews(IPluginNews news)
        {
            if (Fenced(nameof(RegisterNews)) || news is null) return;
            owner.Replace(owner._news, (Plugin: pluginId, Source: news), x => x.Plugin == pluginId);
        }

        public void RegisterChatProvider(IPluginChatProvider provider)
        {
            if (Fenced(nameof(RegisterChatProvider)) || provider is null) return;
            string id;
            try
            {
                id = provider.Id;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }
            if (!ValidProviderId(id))
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: provider id refused");
                return;
            }
            lock (owner._gate)
            {
                if (owner._chat.Any(x => x.Provider.Id == id && x.Plugin != pluginId))
                {
                    Console.Error.WriteLine($"[plugins] {pluginId}: provider {id} already exists, not registered");
                    return;
                }
                owner._chat.RemoveAll(x => x.Plugin == pluginId && x.Provider.Id == id);
                owner._chat.Add((pluginId, provider));
            }
        }
    }

    /// <summary>
    /// A plugin was removed: its stored secrets (API keys, tokens) go from its settings file and from memory, so no key stays behind; the other
    /// settings stay for a reinstall. Ids that are not plain ids are skipped. Returns how many secrets went.
    /// </summary>
    public int ForgetSecrets(IEnumerable<string> pluginIds)
    {
        var dropped = 0;
        foreach (var id in pluginIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Trim('.').Length == 0) continue;
            // An uninstalled plugin hears no more events (it may stay loaded until the restart).
            ForgetContext(id);
            SettingsStore? store;
            lock (_stores) store = _stores.GetValueOrDefault(id);
            var dir = Path.Combine(dataDir, "plugin-data", id);
            store ??= Directory.Exists(dir) ? new SettingsStore(Path.Combine(dir, "settings.json"), () => []) : null;
            dropped += store?.DropSecrets() ?? 0;
        }
        return dropped;
    }

    /// <summary>The text of the language now for a declared service.</summary>
    public static string ServiceName(PluginService s) => Strings.Language == "en" ? s.NameEn : s.NameDe;
}
