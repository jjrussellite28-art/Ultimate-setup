using CouchHud.Core.Config;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Library;

/// <summary>
/// Combines all providers (built-in scanners, config entries, plugins) into one
/// library and keeps user state like favorites and launch times on top of it.
/// </summary>
public sealed class LibraryService(ConfigStore config, ArtworkResolver artwork, IEnumerable<ILibraryProvider> providers)
{
    /// <summary>A provider that takes longer (offline media server, hung plugin) is skipped for this scan.</summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(20);

    private readonly List<ILibraryProvider> _providers = [.. providers];
    private readonly object _lock = new();
    private IReadOnlyList<LibraryItem> _items = [];
    private int _artworkRunning;

    /// <summary>Raised from a background thread when artwork arrived after a scan.</summary>
    public event Action<IReadOnlyList<LibraryItem>>? Changed;

    public IReadOnlyList<LibraryItem> Items
    {
        get
        {
            lock (_lock) return _items;
        }
    }

    public void AddProvider(ILibraryProvider provider) => _providers.Add(provider);

    /// <summary>
    /// A watched folder may contain games a store already knows (e.g. D:\Games\SteamLibrary).
    /// The store entry wins: it has the better launch, artwork and playtime.
    /// </summary>
    internal static List<LibraryItem> WithoutFolderDuplicates(List<LibraryItem> items)
    {
        var storeDirs = items.Where(i => i.Source != "folder" && !string.IsNullOrEmpty(i.InstallDir))
            .Select(i => i.InstallDir!.TrimEnd('\\', '/') + "\\")
            .ToList();
        if (storeDirs.Count == 0) return items;
        return items.Where(i => i.Source != "folder" || i.InstallDir is null
            || !storeDirs.Any(d => (i.InstallDir.TrimEnd('\\', '/') + "\\").StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public async Task<IReadOnlyList<LibraryItem>> ScanAsync(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(_providers.Select(p => SafeScan(p, ProviderTimeout, ct)));
        var cfg = config.Current;
        var custom = CustomEntries.From(cfg.Apps);

        var raw = WithoutFolderDuplicates(results.SelectMany(r => r).Concat(custom)
            .GroupBy(i => i.Id).Select(g => g.First()) // first provider wins on id clashes
            .ToList());
        lock (_lock)
        {
            _baseNames.Clear();
            foreach (var item in raw) _baseNames[item.Id] = item.Name;
        }
        var merged = raw.Select(ApplyUserState).ToList();

        // The art without the user's picks: "automatic" is always this, whatever was picked before.
        var cached = artwork.ApplyCached(merged);
        var overrides = config.Current.Overrides;
        var withArt = cached.Select(i => i with { Art = ArtworkOverrides.Apply(i.Art, overrides.GetValueOrDefault(i.Id)) }).ToList();
        lock (_lock)
        {
            _baseArt.Clear();
            foreach (var item in cached) _baseArt[item.Id] = item.Art;
            _items = withArt;
        }
        _ = FetchMissingArtworkAsync();
        return withArt;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetAsync(bool rescan, CancellationToken ct = default) =>
        rescan || Items.Count == 0 ? await ScanAsync(ct) : Items;

    public LibraryItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public LibraryItem MarkLaunched(string id)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        config.Update(c => c.LastLaunched[id] = now);
        return Replace(id, i => i with { LastPlayed = now });
    }

    public bool ToggleFavorite(string id)
    {
        var favorite = false;
        config.Update(c =>
        {
            favorite = !c.Favorites.Remove(id);
            if (favorite) c.Favorites.Add(id);
        });
        Replace(id, i => i with { Favorite = favorite });
        return favorite;
    }

    private LibraryItem Replace(string id, Func<LibraryItem, LibraryItem> change)
    {
        lock (_lock)
        {
            var list = _items.ToList();
            var index = list.FindIndex(i => i.Id == id);
            if (index < 0) throw new KeyNotFoundException($"unknown item {id}");
            list[index] = change(list[index]);
            _items = list;
            return list[index];
        }
    }

    private LibraryItem ApplyUserState(LibraryItem item)
    {
        var cfg = config.Current;
        cfg.LastLaunched.TryGetValue(item.Id, out var launched);
        cfg.PlaytimeMinutes.TryGetValue(item.Id, out var minutes);
        cfg.Overrides.TryGetValue(item.Id, out var custom);
        return item with
        {
            Favorite = cfg.Favorites.Contains(item.Id),
            // Our own launch timestamps beat whatever the store recorded.
            LastPlayed = Math.Max(item.LastPlayed, launched),
            PlaytimeMinutes = minutes,
            Name = string.IsNullOrWhiteSpace(custom?.Name) ? item.Name : custom!.Name!,
            Type = custom?.Type switch { "game" => ItemType.Game, "app" => ItemType.App, _ => item.Type },
            Hidden = custom?.Hidden ?? false
        };
    }

    /// <summary>Rename, re-categorize or hide any entry; stored as an override so rescans keep it.</summary>
    public LibraryItem EditItem(string id, string? name, string? type, bool? hidden)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        if (type is not null && type is not ("game" or "app")) throw new ArgumentException("type must be game or app", nameof(type));
        config.Update(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            if (name is not null) o.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (type is not null) o.Type = type;
            if (hidden is not null) o.Hidden = hidden.Value;
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
        });
        return Replace(id, i => ApplyUserState(i with { Name = BaseName(i) }));
    }

    /// <summary>Picks artwork for one slot ("cover", "hero", "logo"): an https URL or a local image. Null goes back to automatic.</summary>
    public LibraryItem SetArtwork(string id, string slot, string? urlOrPath)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        var url = string.IsNullOrWhiteSpace(urlOrPath) ? null : ArtworkOverrides.Normalize(urlOrPath);
        ItemOverride? after = null;
        config.Update(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            ArtworkOverrides.Set(o, slot, url);
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
            after = o;
        });
        // Recomputed from the automatic art, so nothing is lost when a pick is reset.
        var result = Replace(id, i => i with { Art = ArtworkOverrides.Apply(BaseArt(i), after) });
        // A reset slot may still be empty (the lookup skipped it while the pick was there).
        if (url is null) _ = FetchMissingArtworkAsync();
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _ = CachePickedAsync(url);
        return result;
    }

    /// <summary>A picked online image goes to disk right away (the UI shows it from there), then the UI hears about it.</summary>
    private async Task CachePickedAsync(string url)
    {
        try
        {
            if (await artwork.CacheNowAsync(url)) Changed?.Invoke(Items);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[library] picked image: {ex.Message}");
        }
    }

    /// <summary>Empties the image cache (wrong or damaged pictures); what the library shows is fetched again in the background.</summary>
    public void ClearArtworkCache()
    {
        artwork.Images?.Clear();
        _ = FetchMissingArtworkAsync();
    }

    /// <summary>Candidates for the artwork picker.</summary>
    public Task<ArtworkChoices> GetArtworkChoicesAsync(string id, CancellationToken ct = default) =>
        artwork.GetChoicesAsync(Find(id) ?? throw new KeyNotFoundException($"unknown item {id}"), ct);

    public IReadOnlyList<ItemCollection> GetCollections()
    {
        lock (_lock) return config.Current.Collections.Select(Clone).ToList();
    }

    /// <summary>Applies one change to the collections and returns the new list.</summary>
    public IReadOnlyList<ItemCollection> UpdateCollections(Action<List<ItemCollection>> change)
    {
        config.Update(c => change(c.Collections));
        return GetCollections();
    }

    private static ItemCollection Clone(ItemCollection c) => new() { Id = c.Id, Name = c.Name, Items = [.. c.Items], OnHome = c.OnHome };

    /// <summary>The name before user overrides, so clearing an override restores it.</summary>
    private Artwork BaseArt(LibraryItem item) => _baseArt.TryGetValue(item.Id, out var a) ? a : item.Art;

    private readonly Dictionary<string, Artwork> _baseArt = new();

    private string BaseName(LibraryItem item) => _baseNames.TryGetValue(item.Id, out var n) ? n : item.Name;

    private readonly Dictionary<string, string> _baseNames = new();

    public void AddCustomEntries(IEnumerable<CustomEntry> entries)
    {
        var valid = entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrWhiteSpace(e.Path) || !string.IsNullOrWhiteSpace(e.Url))).ToList();
        if (valid.Count == 0) throw new ArgumentException(Strings.Get("library.nothingToAdd"));
        config.Update(c => c.Apps.AddRange(valid));
    }

    /// <summary>
    /// Startup parameters and working folder of an entry the user added (an exe, not a link or a store game). The checks are
    /// <see cref="LaunchOptions"/>; a folder equal to the program's own is stored as "default".
    /// </summary>
    public LibraryItem EditLaunch(string id, string? arguments, string? workingDirectory)
    {
        var args = LaunchOptions.ParseArguments(arguments);
        LibraryItem? rebuilt = null;
        config.Update(c =>
        {
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = CustomEntries.From(c.Apps).Select(i => i.Id).ToList().IndexOf(id);
            if (index < 0 || string.IsNullOrEmpty(valid[index].Path) || !string.IsNullOrEmpty(valid[index].Url)) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
            var entry = valid[index];
            entry.Cwd = LaunchOptions.ValidateWorkingDirectory(workingDirectory, entry.Path!);
            entry.Args = [.. args];
            rebuilt = CustomEntries.From([entry])[0];
        });
        return Replace(id, i => i with { Launch = rebuilt!.Launch, InstallDir = rebuilt.InstallDir });
    }

    /// <summary>A web entry opens in Edge (copy-protected streaming) or in the couch browser. Takes effect with the next scan.</summary>
    public void SetCustomEdge(string id, bool edge)
    {
        var found = false;
        config.Update(c =>
        {
            var ids = CustomEntries.From(c.Apps).Select(i => i.Id).ToList();
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = ids.IndexOf(id);
            if (index < 0 || !WebLinks.IsWebUrl(valid[index].Url)) return;
            valid[index].OpenInEdge = edge;
            found = true;
        });
        if (!found) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
    }

    /// <summary>Removes an entry the user added; store games are hidden instead.</summary>
    public void RemoveCustomEntry(string id)
    {
        var removed = false;
        config.Update(c =>
        {
            var ids = CustomEntries.From(c.Apps).Select(i => i.Id).ToList();
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = ids.IndexOf(id);
            if (index < 0) return;
            c.Apps.Remove(valid[index]);
            c.Overrides.Remove(id);
            c.Favorites.Remove(id);
            Collections.Forget(c.Collections, id);
            removed = true;
        });
        if (!removed) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
        lock (_lock) _items = _items.Where(i => i.Id != id).ToList();
    }

    /// <summary>Adds a finished play session.</summary>
    public LibraryItem AddPlaytime(string id, double minutes)
    {
        if (minutes <= 0) return Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        double total = 0;
        config.Update(c =>
        {
            c.PlaytimeMinutes.TryGetValue(id, out var before);
            total = before + minutes;
            c.PlaytimeMinutes[id] = total;
        });
        return Replace(id, i => i with { PlaytimeMinutes = total });
    }

    private async Task FetchMissingArtworkAsync()
    {
        if (Interlocked.Exchange(ref _artworkRunning, 1) == 1) return;
        try
        {
            // Looked up on the automatic art, so a picked image never hides a slot from the lookup.
            IReadOnlyList<LibraryItem> baseItems;
            lock (_lock) baseItems = _items.Select(i => i with { Art = BaseArt(i) }).ToList();
            var updated = await artwork.ResolveAsync(baseItems);
            if (updated is not null)
            {
                IReadOnlyList<LibraryItem> merged;
                lock (_lock)
                {
                    // Keep state that changed while we were waiting (favorites, launches).
                    var art = updated.ToDictionary(i => i.Id, i => i.Art);
                    // The user's picks are applied last, from the current config: they always win.
                    var overrides = config.Current.Overrides;
                    foreach (var (id, a) in art) _baseArt[id] = a;
                    merged = _items.Select(i => art.TryGetValue(i.Id, out var a)
                        ? i with { Art = ArtworkOverrides.Apply(a, overrides.GetValueOrDefault(i.Id)) }
                        : i).ToList();
                    _items = merged;
                }
                Changed?.Invoke(merged);
            }

            // The images themselves: once to disk, slowly, in the background. The UI shows each as it arrives.
            var slots = Items.SelectMany(i => new[] { i.Art.Cover, i.Art.Hero, i.Art.Logo, i.Art.Icon }).ToList();
            var arrived = await artwork.DownloadMissingAsync(slots, () => Changed?.Invoke(Items));
            artwork.PruneImages(Items);
            if (arrived > 0) Changed?.Invoke(Items);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[library] artwork lookup failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _artworkRunning, 0);
        }
    }

    private static async Task<IReadOnlyList<LibraryItem>> SafeScan(ILibraryProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Task.Run: a provider that blocks before returning its task must not freeze the UI thread.
            return await Task.Run(() => provider.GetItemsAsync(cts.Token), cts.Token).WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One broken, slow or offline store or plugin must not take down the whole library.
            var reason = ex is TimeoutException or OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0}s" : ex.ToString();
            Console.Error.WriteLine($"[library] {provider.Name} skipped: {reason}");
            return [];
        }
    }
}
