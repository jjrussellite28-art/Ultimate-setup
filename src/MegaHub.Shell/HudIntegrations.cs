using System.Net.Http;
using System.Text.Json;
using MegaHub.Core.Assistant;
using MegaHub.Core.Config;
using MegaHub.Core.Localization;
using MegaHub.Core.Plugins;
using CouchHud.Sdk;
using MegaHub.Shell.Services;

namespace MegaHub.Shell;

// The weather, the news and the assistant's providers since 0.7: plugins fill the slots (SDK 5), the HUD keeps the places where they are
// shown. Without the plugin the feature is simply not there. Everything a plugin hands over is bounded and cleaned here before the page
// sees it, and every call into a plugin runs with a timeout.
public sealed partial class Hud
{
    private const int MaxNewsItems = 100;
    // The feeds are fetched in parallel, each with up to 20 seconds of its own.
    private static readonly TimeSpan NewsTimeout = TimeSpan.FromSeconds(30);
    private const int MaxForecastDays = 7;
    private const int MaxPlaces = 8;

    // -----------------------------------------------------------------------
    // Calls into a slot that are not async (the place, the feed list, setting a place, adding and removing a feed)

    private static readonly TimeSpan SlotTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SlotPenalty = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, long> SlotStalled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a synchronous call into a plugin on a worker with a timeout, so a plugin that hangs cannot freeze the shell's thread. A reading call
    /// (<paramref name="soft"/>) fails soft: after a timeout it answers <paramref name="fallback"/> at once for the next 30 seconds instead of waiting
    /// again. A changing call throws "no answer from the plugin". An exception of the plugin comes out as it was thrown.
    /// </summary>
    internal static T SlotCall<T>(string plugin, Func<T> call, T fallback, bool soft, TimeSpan? timeout = null)
    {
        if (soft)
        {
            lock (SlotStalled)
            {
                if (SlotStalled.TryGetValue(plugin, out var until) && Environment.TickCount64 < until) return fallback;
            }
        }
        var task = Task.Run(call);
        try
        {
            if (task.Wait(timeout ?? SlotTimeout)) return task.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
        }
        // Not answered: whatever it throws later is observed and dropped.
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        lock (SlotStalled) SlotStalled[plugin] = Environment.TickCount64 + (long)SlotPenalty.TotalMilliseconds;
        Console.Error.WriteLine($"[plugins] {plugin}: no answer in time");
        if (soft) return fallback;
        throw new InvalidOperationException(Strings.Get("plugin.noAnswer", plugin));
    }

    // -----------------------------------------------------------------------
    // Weather

    /// <summary>A weather plugin is loaded (and not paused).</summary>
    private bool WeatherAvailable => plugins.WeatherSource is not null;

    private PluginPlace? WeatherPlace()
    {
        if (plugins.WeatherSource is not { } w) return null;
        try
        {
            return CleanPlace(SlotCall(w.Plugin, () => w.Source.Place, null, soft: true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[weather] {w.Plugin}: {ex.GetType().Name}");
            return null;
        }
    }

    public async Task<PluginWeatherReport?> GetWeatherAsync()
    {
        if (plugins.WeatherSource is not { } w || WeatherPlace() is null) return null;
        try
        {
            var report = await PluginManager.SafeAsync(w.Plugin, ct => w.Source.GetAsync(ct));
            return report is null ? null : CleanReport(report);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[weather] {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<PluginPlace>> SearchPlacesAsync(string query)
    {
        if (plugins.WeatherSource is not { } w || string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        if (q.Length > 100) q = q[..100];
        var found = await PluginManager.SafeAsync(w.Plugin, ct => w.Source.SearchAsync(q, ct));
        return (found ?? []).Select(CleanPlace).Where(p => p is not null).Select(p => p!).Take(MaxPlaces).ToList();
    }

    private void SetWeatherPlace(JsonElement value)
    {
        if (plugins.WeatherSource is not { } w) throw new InvalidOperationException(Strings.Get("hud.featureNeedsPlugin"));
        PluginPlace? place = null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            place = CleanPlace(JsonSerializer.Deserialize<PluginPlace>(value.GetRawText(), UiHost.UiBridge.Json)) ?? throw new ArgumentException(Strings.Get("hud.badPlace"));
        }
        try
        {
            SlotCall(w.Plugin, () => { w.Source.SetPlace(place); return 0; }, 0, soft: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"{w.Plugin}: {ex.Message}");
        }
    }

    internal static PluginPlace? CleanPlace(PluginPlace? p)
    {
        if (p is null || !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || Math.Abs(p.Latitude) > 90 || Math.Abs(p.Longitude) > 180) return null;
        return new PluginPlace(PlainText(p.Name, 100) is { Length: > 0 } name ? name : "?", PlainText(p.Region, 200), p.Latitude, p.Longitude);
    }

    internal static PluginWeatherReport? CleanReport(PluginWeatherReport r)
    {
        if (!double.IsFinite(r.Temperature)) return null;
        var days = (r.Days ?? []).Where(d => d is not null && double.IsFinite(d.Min) && double.IsFinite(d.Max)).Take(MaxForecastDays)
            .Select(d => new PluginWeatherDay(PlainText(d.Date, 10), d.Min, d.Max, Math.Clamp(d.Code, 0, 99))).ToList();
        return new PluginWeatherReport(PlainText(r.Place, 100), r.Temperature, Math.Clamp(r.Code, 0, 99), r.IsDay, days);
    }

    // -----------------------------------------------------------------------
    // News

    /// <summary>A news plugin is loaded (and not paused) and the News area is on.</summary>
    private bool NewsAvailable => Areas.On(FeatureAreas.News) && plugins.NewsSource is not null;

    private (string Plugin, IPluginNews Source) NewsSourceOrThrow()
    {
        RequireArea(FeatureAreas.News);
        return plugins.NewsSource ?? throw new InvalidOperationException(Strings.Get("hud.featureNeedsPlugin"));
    }

    public async Task<IReadOnlyList<PluginNewsItem>> GetNewsAsync()
    {
        RequireArea(FeatureAreas.News);
        if (plugins.NewsSource is not { } n) return [];
        var items = await PluginManager.SafeAsync(n.Plugin, ct => n.Source.GetItemsAsync(ct), NewsTimeout);
        // The page loads the pictures straight from the web (the CSP allows https images), past the plugin's client: so while Offline is on, or when
        // every news service is switched off in Settings > Privacy, the pictures are dropped here and only the text is shown.
        var pictures = !net.Offline && !(plugins.GrantOf(n.Plugin)?.Services is { Count: > 0 } services && services.All(sv => !ServiceSwitchedOn(config.Current, n.Plugin, sv.Id)));
        return (items ?? []).Select(CleanItem).Where(i => i is not null).Select(i => pictures ? i! : i! with { Image = null }).Take(MaxNewsItems).ToList();
    }

    public IReadOnlyList<PluginNewsFeed> GetFeeds()
    {
        RequireArea(FeatureAreas.News);
        if (plugins.NewsSource is not { } n) return [];
        try
        {
            return (SlotCall(n.Plugin, () => n.Source.Feeds, null, soft: true) ?? []).Where(f => f is not null && HttpUrl(f.Url) is not null).Take(100)
                .Select(f => new PluginNewsFeed(PlainText(f.Name, 60), HttpUrl(f.Url)!)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[news] {n.Plugin}: {ex.GetType().Name}");
            return [];
        }
    }

    public void AddFeed(string name, string url)
    {
        var n = NewsSourceOrThrow();
        if (HttpUrl(url) is not { } clean) throw new ArgumentException(Strings.Get("hud.needFeedAddress"));
        try
        {
            var feedName = PlainText(name, 60);
            SlotCall(n.Plugin, () => { n.Source.AddFeed(feedName, clean); return 0; }, 0, soft: false);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(PlainText(ex.Message, 200));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"{n.Plugin}: {ex.GetType().Name}");
        }
    }

    public void RemoveFeed(string url)
    {
        var n = NewsSourceOrThrow();
        try
        {
            var feedUrl = url ?? "";
            SlotCall(n.Plugin, () => { n.Source.RemoveFeed(feedUrl); return 0; }, 0, soft: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"{n.Plugin}: {ex.GetType().Name}");
        }
    }

    /// <summary>An article as the page may show it: plain text, bounded; links http(s) only, pictures https only; null without a title or link.</summary>
    internal static PluginNewsItem? CleanItem(PluginNewsItem? i)
    {
        if (i is null || HttpUrl(i.Link) is not { } link || PlainText(i.Title, 300) is not { Length: > 0 } title) return null;
        var image = HttpUrl(i.Image) is { } img && img.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? img : null;
        var paragraphs = (i.Paragraphs ?? []).Select(p => PlainText(p, 4000)).Where(p => p.Length > 0).Take(60).ToList();
        return new PluginNewsItem(PlainText(i.Id, 128) is { Length: > 0 } id ? id : link, PlainText(i.Feed, 60), title, PlainText(i.Summary, 1000), link, image,
            Math.Max(0, i.Published), paragraphs);
    }

    private static string? HttpUrl(string? url)
    {
        var u = url?.Trim();
        return u is { Length: > 0 and <= 2000 } && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? u : null;
    }

    private static string PlainText(string? s, int max)
    {
        var clean = new string((s ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    // -----------------------------------------------------------------------
    // The plugins' network services (Settings › Privacy)

    private object PluginServicesDto()
    {
        var off = config.Current.PluginNetOff ?? [];
        return plugins.Services.Select(s => new
        {
            plugin = s.Plugin,
            pluginName = s.PluginName,
            id = s.Service.Id,
            name = PluginManager.ServiceName(s.Service),
            anyHost = s.Service.AnyHost,
            lan = s.Service.Lan,
            hosts = s.Service.Hosts.Where(h => h != PluginNetServices.AnyHost && h != PluginNetServices.LanHost).ToList(),
            on = !off.Contains(PluginService.Key(s.Plugin, s.Service.Id), StringComparer.OrdinalIgnoreCase)
        }).ToList();
    }

    /// <summary>Is a declared service switched on (Offline aside)? Asked by the plugins' clients before every request.</summary>
    internal static bool ServiceSwitchedOn(AppConfig c, string plugin, string service) =>
        !(c.PluginNetOff ?? []).Contains(PluginService.Key(plugin, service), StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings › Privacy: one plugin service on or off. Same gate as the core's own switches (parents' code under parental controls).</summary>
    public object SetPluginService(string plugin, string service, bool on)
    {
        RefuseWhileConfigFaulted();
        var found = plugins.Services.FirstOrDefault(s => s.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase) && s.Service.Id == service);
        if (found.Service is null) throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        var key = PluginService.Key(found.Plugin, found.Service.Id);
        // The parents' code is asked for exactly this switch (the page asks with the same item).
        RequirePrivacyGrant("pluginNet:" + key);
        config.UpdateOrThrow(c =>
        {
            c.PluginNetOff ??= [];
            c.PluginNetOff.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!on) c.PluginNetOff.Add(key);
        });
        ApplyNetPolicy();
        return PluginServicesDto();
    }

    // -----------------------------------------------------------------------
    // Assistant providers

    private (string Plugin, IPluginChatProvider Provider)? CurrentProvider =>
        plugins.ChatProviders.FirstOrDefault(p => p.Provider.Id == config.Current.AiProvider) is { Provider: not null } found ? found : null;

    private static T Ask<T>(string plugin, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[assistant] {plugin}: {ex.GetType().Name}");
            return fallback;
        }
    }

    private static IReadOnlyList<string> ModelsOf(string plugin, IPluginChatProvider p) =>
        Ask(plugin, () => (p.Models ?? []).Select(m => PlainText(m, 80)).Where(m => m.Length > 0).Distinct().Take(12).ToList(), new List<string>());

    private bool AssistantConfigured => CurrentProvider is { } p && Ask(p.Plugin, () => p.Provider.HasKey, false);

    /// <summary>The providers to choose from in the settings (never a key, only whether one is stored).</summary>
    private object AiProvidersDto() => plugins.ChatProviders.Select(p => new
    {
        id = p.Provider.Id,
        name = PlainText(Ask(p.Plugin, () => p.Provider.Name, p.Provider.Id), 60),
        models = ModelsOf(p.Plugin, p.Provider),
        keySet = Ask(p.Plugin, () => p.Provider.HasKey, false)
    }).ToList();

    private string ModelNow(string plugin, IPluginChatProvider provider)
    {
        var model = config.Current.AiModel;
        return string.IsNullOrWhiteSpace(model) ? ModelsOf(plugin, provider).FirstOrDefault() ?? "" : model;
    }

    public object AssistantInfo()
    {
        var p = CurrentProvider;
        return new
        {
            configured = AssistantConfigured,
            provider = config.Current.AiProvider,
            model = p is { } cur ? ModelNow(cur.Plugin, cur.Provider) : config.Current.AiModel
        };
    }

    private void SetAiProvider(string provider)
    {
        if (provider.Length > 0 && !plugins.ChatProviders.Any(p => p.Provider.Id == provider)) throw new ArgumentException(Strings.Get("hud.unknownProvider", PlainText(provider, 40)));
        config.UpdateOrThrow(c =>
        {
            if (c.AiProvider != provider) c.AiModel = "";
            c.AiProvider = provider;
        });
    }

    /// <summary>A new key for the chosen provider: handed to its plugin (which keeps it in its own secret store), never kept by the core.</summary>
    private void SetAiKey(string key)
    {
        if (CurrentProvider is not { } p) throw new InvalidOperationException(Strings.Get("assistant.needsProvider"));
        var trimmed = key.Trim();
        if (trimmed.Length > 1000) throw new ArgumentException(Strings.Get("assistant.keyInvalid"));
        try
        {
            p.Provider.SetKey(trimmed.Length == 0 ? null : trimmed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"{p.Plugin}: {ex.GetType().Name}");
        }
        // A new key starts a fresh conversation.
        _assistant = null;
    }

    public async Task<string> AskAssistantAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(Strings.Get("assistant.emptyQuestion"));
        if (net.Offline) throw new InvalidOperationException(Strings.Get("net.assistantOffline"));
        if (CurrentProvider is not { } p || !AssistantConfigured) throw new InvalidOperationException(Strings.Get("assistant.needsProvider"));
        var grant = plugins.GrantOf(p.Plugin);
        if (grant?.Services is { Count: > 0 } services && services.All(s => !ServiceSwitchedOn(config.Current, p.Plugin, s.Id)))
            throw new InvalidOperationException(Strings.Get("net.assistantOff"));

        // A new provider or model starts a fresh conversation (a new key does too, see SetAiKey).
        var model = ModelNow(p.Plugin, p.Provider);
        var identity = $"{p.Plugin}|{p.Provider.Id}|{model}";
        if (_assistant is null || _assistantFor != identity)
        {
            _assistant = new AssistantSession(new PluginChatAdapter(p.Provider, model, PluginManager.HttpTimeout), new HudTools(this), () => AllTools);
            _assistantFor = identity;
        }

        _assistantCts?.Cancel();
        _assistantCts = new CancellationTokenSource();
        try
        {
            return await _assistant.AskAsync(text, ConfirmAsync,
                call => Emit("assistant", new { type = "tool", name = call.Name }),
                _assistantCts.Token);
        }
        catch (AssistantException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException(Strings.Get("assistant.timeout"));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(Strings.Get("assistant.noConnection"));
        }
    }

    // -----------------------------------------------------------------------
    // Features of the core that became plugins: was this one in use here (so its plugin is worth pointing out once)?

    private bool UsedFeature(string feature)
    {
        var c = config.Current;
        return feature switch
        {
            "homeAssistant" => PluginSettingSeeds.For(c, PluginSettingSeeds.HomeAssistantId) is not null,
            "weather" => c.WeatherLocation is not null,
            // Everybody had the News tab with the default feeds: worth pointing out while the area is on.
            "news" => c.NewsFeeds is not null && Areas.On(FeatureAreas.News),
            "assistantAnthropic" => c.AiProvider == PluginSettingSeeds.AnthropicId || !string.IsNullOrWhiteSpace(c.AnthropicApiKey),
            "assistantOpenAi" => c.AiProvider == PluginSettingSeeds.OpenAiId || !string.IsNullOrWhiteSpace(c.OpenAiApiKey),
            // An LG TV paired while webOS was part of the core (before 0.7): its pairing waits for the plugin.
            "lgTv" => c.Tv is { Provider: MegaHub.Core.Integrations.Tv.TvProviderRegistry.LgWebOs },
            _ => false
        };
    }

    private static string FeatureSuggestionText(string? feature, string name) => feature switch
    {
        "weather" => Strings.Get("plugin.suggestWeather", name),
        "news" => Strings.Get("plugin.suggestNews", name),
        "assistantAnthropic" or "assistantOpenAi" => Strings.Get("plugin.suggestAssistant", name),
        "lgTv" => Strings.Get("plugin.suggestLgTv", name),
        _ => Strings.Get("plugin.suggestHomeAssistant", name)
    };
}
