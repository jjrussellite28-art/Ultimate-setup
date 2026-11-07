using System.Globalization;
using System.Text;
using CouchHud.Sdk;

namespace MegaHub.Plugin.Achievements;

/// <summary>
/// One Achievements page for Mega Hub, by russellite06 (written with Claude and ChatGPT). Version 0.2 reads local achievement files
/// (the Goldberg emulator's format), lists the games with progress and a tier, lets you sort and filter the list, and opens a page per game
/// with every achievement. Version 0.3 adds Steam (through Steam's web service, with the owner's own free web key). The other sources are listed as planned.
/// </summary>
public sealed class AchievementsPlugin : IPlugin
{
    private const int MaxGamePages = 120;
    private const string ListPage = "achievements";
    private const string SteamPage = "steam-setup";

    private IPluginContext _context = null!;
    private readonly object _gate = new();
    private string _sort = "recent";
    private string _filter = "all";

    // Steam: refreshed in the background (the page shows what is there and updates itself when a refresh ends).
    private static readonly TimeSpan SteamRefreshAfter = TimeSpan.FromMinutes(30);
    private SteamStatus _steamStatus = SteamStatus.Off;
    private List<AchievementGame> _steamGames = [];
    private string _steamPersona = "";
    private string _steamProgress = "";
    private DateTimeOffset _steamFetched = DateTimeOffset.MinValue;
    private bool _steamRunning;
    private CancellationTokenSource? _steamCts;

    public string Id => "achievements";

    private string T(string de, string en) => _context.Language == "en" ? en : de;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        Register();
        context.Events.LanguageChanged += _ => Register();
        // A new key, switch or Steam ID: ask Steam again right away.
        context.Settings.SettingChanged += key =>
        {
            if (key is "steam" or "steamkey" or "steamid") ResetSteam();
        };
    }

    private void ResetSteam()
    {
        lock (_gate)
        {
            _steamFetched = DateTimeOffset.MinValue;
            _steamCts?.Cancel();
        }
        _context.Invalidate(ListPage);
    }

    private void Register()
    {
        _context.RegisterSettings(
            new PluginSetting
            {
                Key = "local",
                Label = T("Lokale Erfolgsdateien lesen", "Read local achievement files"),
                Description = T("Liest die Datei achievements.json, die Emulatoren wie Goldberg je Spiel anlegen. Es wird nur gelesen.", "Reads the achievements.json file that emulators such as Goldberg keep for each game. It only reads."),
                Kind = SettingKind.Toggle,
                Default = "true"
            },
            new PluginSetting
            {
                Key = "steamsetup",
                Label = T("Steam einrichten (Schritt für Schritt)", "Set up Steam (step by step)"),
                Description = T("Öffnet eine Anleitung mit Link zum Schlüssel", "Opens a guide with a link to get your key"),
                Kind = SettingKind.Page,
                Page = SteamPage
            },
            new PluginSetting
            {
                Key = "steam",
                Label = T("Steam-Erfolge anzeigen", "Show my Steam achievements"),
                Description = T("Schalter für Steam. Erst den Schlüssel einrichten (Zeile darüber).", "Switch for Steam. Set up the key first (the row above)."),
                Kind = SettingKind.Toggle,
                Default = "true"
            },
            new PluginSetting
            {
                Key = "steamkey",
                Label = T("Steam-Schlüssel einfügen", "Paste your Steam key"),
                Description = T("Ein kostenloses Passwort von Steam, damit Mega Hub deine Erfolge lesen darf. Woher? Siehe die Anleitung oben. Wird verschlüsselt gespeichert und nie angezeigt.", "A free password from Steam that lets Mega Hub read your achievements. Where to get it? See the guide above. It is stored encrypted and never shown."),
                Kind = SettingKind.Secret
            },
            new PluginSetting
            {
                Key = "steamid",
                Label = T("Steam-ID (nur falls nötig)", "Steam ID (only if needed)"),
                Description = T("Normalerweise leer lassen: Mega Hub findet dein Konto selbst, wenn Steam auf diesem PC angemeldet ist.", "Normally leave empty: Mega Hub finds your account itself when Steam is signed in on this PC."),
                Kind = SettingKind.Text
            },
            new PluginSetting
            {
                Key = "folders",
                Label = T("Weitere Ordner", "Extra folders"),
                Description = T("Mit Semikolon getrennt. Die üblichen Goldberg-Ordner in %APPDATA% werden immer gesucht.", "Separated by semicolons. The usual Goldberg folders in %APPDATA% are always searched."),
                Kind = SettingKind.Text
            },
            new PluginSetting
            {
                Key = "names",
                Label = T("Spielnamen", "Game names"),
                Description = T("Format: 480=Spacewar;12345=Mein Spiel. Die Ordner heißen nur wie die App-Nummer.", "Format: 480=Spacewar;12345=My game. The folders are only named after the app number."),
                Kind = SettingKind.Text
            });

        _context.RegisterPage(new PluginPage
        {
            Id = ListPage,
            Title = T("Erfolge", "Achievements"),
            Description = T("Fortschritt je Spiel aus Steam und lokalen Erfolgsdateien", "Progress per game from Steam and local achievement files"),
            Sections =
            [
                new PluginSection("view", T("Ansicht", "View")),
                new PluginSection("summary", T("Übersicht", "Overview")),
                new PluginSection("games", T("Spiele", "Games")),
                new PluginSection("months", T("Freigeschaltet nach Monat", "Unlocked by month")),
                new PluginSection("steam", "Steam"),
                new PluginSection("sources", T("Weitere Quellen", "Other sources"))
            ],
            Rows = ct => Task.Run<IReadOnlyList<PluginRow>>(() => BuildRows(ct), ct),
            Activate = (rowId, _) => Task.FromResult(ActivateRow(rowId)),
            Change = (rowId, value, _) =>
            {
                lock (_gate)
                {
                    if (rowId == "sort" && SortOptions.Any(o => o.Id == value)) _sort = value;
                    else if (rowId == "filter" && FilterOptions.Any(o => o.Id == value)) _filter = value;
                }
                _context.Invalidate(ListPage);
                return Task.FromResult<string?>(null);
            }
        });

        _context.RegisterPage(new PluginPage
        {
            Id = SteamPage,
            Title = T("Steam einrichten", "Set up Steam"),
            Description = T("Vier Schritte, einmal. Danach zeigt Mega Hub deine Steam-Erfolge von selbst.", "Four steps, once. After that Mega Hub shows your Steam achievements by itself."),
            Sections =
            [
                new PluginSection("state", T("Stand", "Status")),
                new PluginSection("steps", T("Die Schritte", "The steps"))
            ],
            Rows = _ => Task.FromResult(SteamSetupRows()),
            Activate = (rowId, _) => Task.FromResult(ActivateRow(rowId))
        });

        _context.RegisterTab(new PluginTab("achievements", ListPage, "Erfolge", "Achievements") { Area = "games" });
    }

    private PluginChoiceOption[] SortOptions =>
    [
        new("recent", T("Zuletzt freigeschaltet", "Recently unlocked")),
        new("progress", T("Fortschritt", "Progress")),
        new("name", T("Name", "Name"))
    ];

    private PluginChoiceOption[] FilterOptions =>
    [
        new("all", T("Alle Spiele", "All games")),
        new("open", T("Angefangen", "In progress")),
        new("done", T("Abgeschlossen", "Completed")),
        new("new", T("Nicht angefangen", "Not started"))
    ];

    private IReadOnlyList<PluginRow> BuildRows(CancellationToken ct)
    {
        string sort, filter;
        lock (_gate)
        {
            sort = _sort;
            filter = _filter;
        }

        var rows = new List<PluginRow>();
        var localOn = _context.Settings.GetBool("local", true);
        var all = new List<AchievementGame>();
        if (localOn)
        {
            var roots = LocalAchievements.DefaultRoots().Concat(LocalAchievements.SplitFolders(_context.Settings.Get("folders")));
            try
            {
                all = LocalAchievements.Scan(roots, LocalAchievements.ParseNames(_context.Settings.Get("names")), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _context.Log("scan failed: " + ex.Message);
            }
        }

        // Steam: what the last refresh found (a refresh starts in the background when it is due). Steam wins over a local file of the same game.
        var steamOn = _context.Settings.GetBool("steam", true);
        StartSteamIfDue(steamOn);
        List<AchievementGame> steamGames;
        lock (_gate) steamGames = steamOn ? _steamGames : [];
        if (steamGames.Count > 0)
        {
            var steamIds = steamGames.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            all = [.. steamGames, .. all.Where(g => !steamIds.Contains(g.Id))];
        }

        // The viewer list: the filter picks the games, the sort puts them in order.
        IEnumerable<AchievementGame> shown = filter switch
        {
            "open" => all.Where(g => g.Earned > 0 && g.Percent < 100),
            "done" => all.Where(g => g.Percent == 100),
            "new" => all.Where(g => g.Earned == 0),
            _ => all
        };
        shown = sort switch
        {
            "progress" => shown.OrderByDescending(g => g.Percent).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "name" => shown.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            _ => shown.OrderByDescending(g => g.LastUnlock ?? DateTimeOffset.MinValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        };
        var list = shown.Take(MaxGamePages).ToList();

        rows.Add(new PluginRow("sort", T("Sortieren nach", "Sort by"))
        {
            Section = "view",
            Kind = new PluginRowKind.Choice(SortOptions),
            Value = SortOptions.First(o => o.Id == sort).Label,
            RequiresUnlock = false
        });
        rows.Add(new PluginRow("filter", T("Anzeigen", "Show"))
        {
            Section = "view",
            Kind = new PluginRowKind.Choice(FilterOptions),
            Value = FilterOptions.First(o => o.Id == filter).Label,
            RequiresUnlock = false
        });

        var earned = all.Sum(g => g.Earned);
        var total = all.Sum(g => g.Total);
        rows.Add(new PluginRow("sum-games", T("Spiele mit Erfolgen", "Games with achievements")) { Section = "summary", Value = all.Count.ToString(CultureInfo.CurrentCulture), RequiresUnlock = false });
        rows.Add(new PluginRow("sum-earned", T("Freigeschaltet", "Unlocked")) { Section = "summary", Value = $"{earned} / {total}", RequiresUnlock = false });
        rows.Add(new PluginRow("sum-done", T("Alle Erfolge geholt", "Fully completed"))
        {
            Section = "summary",
            Value = all.Count(g => g.Percent == 100).ToString(CultureInfo.CurrentCulture),
            RequiresUnlock = false
        });

        // A page per game, registered now so the row can open it.
        var taken = new HashSet<string>();
        var entries = new List<(string PageId, AchievementGame Game)>();
        foreach (var g in list)
        {
            var pageId = PageId(g, taken);
            taken.Add(pageId);
            entries.Add((pageId, g));
            RegisterGamePage(pageId, g);
        }

        if (list.Count == 0)
        {
            rows.Add(new PluginRow("none", all.Count == 0 && (localOn || steamOn) ? T("Noch keine Erfolge gefunden", "No achievements found yet") : T("Keine Spiele in dieser Ansicht", "No games in this view"))
            {
                Section = "games",
                Detail = !localOn && !steamOn
                    ? T("Steam und lokale Dateien sind ausgeschaltet", "Steam and local files are switched off")
                    : T("Steam: siehe den Abschnitt Steam unten. Weitere Ordner trägst du in den Plugin-Einstellungen ein", "Steam: see the Steam section below. Add more folders in the plugin settings"),
                RequiresUnlock = false
            });
        }

        foreach (var (pageId, g) in entries)
        {
            var detail = $"{g.Percent} % · {Tier(g.Percent)}";
            if (g.LastUnlock is { } unlockedAt)
                detail += " · " + T("zuletzt ", "last ") + (g.LastName ?? "") + " (" + unlockedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) + ")";
            rows.Add(new PluginRow("game:" + pageId, g.Name)
            {
                Section = "games",
                Kind = new PluginRowKind.Link(pageId),
                Detail = detail,
                Value = $"{g.Earned} / {g.Total}",
                On = g.Percent == 100,
                RequiresUnlock = false
            });
        }

        // Unlocks per month, the last six months.
        var now = DateTimeOffset.Now;
        for (var back = 0; back < 6; back++)
        {
            var month = new DateTime(now.Year, now.Month, 1).AddMonths(-back);
            var count = all.SelectMany(g => g.Items).Count(i => i.When is { } w && w.ToLocalTime().Year == month.Year && w.ToLocalTime().Month == month.Month);
            rows.Add(new PluginRow("month:" + month.ToString("yyyy-MM", CultureInfo.InvariantCulture), month.ToString("MMMM yyyy", CultureInfo.CurrentCulture))
            {
                Section = "months",
                Value = count.ToString(CultureInfo.CurrentCulture),
                RequiresUnlock = false
            });
        }

        rows.Add(new PluginRow("src-local", T("Lokale Dateien (Goldberg-Format)", "Local files (Goldberg format)"))
        {
            Section = "sources",
            Value = localOn ? T("An", "On") : T("Aus", "Off"),
            On = localOn,
            Detail = T("Schalter in den Plugin-Einstellungen", "Switch in the plugin settings"),
            RequiresUnlock = false
        });
        rows.Add(SteamRow(steamOn));
        foreach (var (id, de, en) in Planned)
            rows.Add(new PluginRow("src-" + id, T(de, en)) { Section = "sources", Value = T("Geplant", "Planned"), RequiresUnlock = false });
        return rows;
    }

    /// <summary>The page of one game: every achievement, unlocked first.</summary>
    private void RegisterGamePage(string pageId, AchievementGame game)
    {
        var title = game.Name.Length > 40 ? game.Name[..40] : game.Name;
        _context.RegisterPage(new PluginPage
        {
            Id = pageId,
            Title = title,
            Description = $"{game.Earned} / {game.Total} · {game.Percent} % · {Tier(game.Percent)}",
            Sections =
            [
                new PluginSection("done", T("Freigeschaltet", "Unlocked")),
                new PluginSection("todo", T("Noch offen", "Still locked"))
            ],
            Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>(GameRows(game))
        });
    }

    private IReadOnlyList<PluginRow> GameRows(AchievementGame game)
    {
        var rows = new List<PluginRow>();
        var done = game.Items.Where(i => i.Earned).OrderByDescending(i => i.When ?? DateTimeOffset.MinValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var todo = game.Items.Where(i => !i.Earned).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var i in done)
        {
            if (rows.Count >= 190) break;
            rows.Add(new PluginRow("a" + (n++).ToString(CultureInfo.InvariantCulture), Pretty(i.Name))
            {
                Section = "done",
                On = true,
                Value = T("Freigeschaltet", "Unlocked"),
                Detail = i.When is { } w ? w.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : null,
                RequiresUnlock = false
            });
        }
        foreach (var i in todo)
        {
            if (rows.Count >= 190) break;
            rows.Add(new PluginRow("a" + (n++).ToString(CultureInfo.InvariantCulture), Pretty(i.Name))
            {
                Section = "todo",
                Value = T("Gesperrt", "Locked"),
                RequiresUnlock = false
            });
        }
        return rows;
    }

    /// <summary>A page id the host accepts: lower case letters, digits and "-", unique in this list.</summary>
    private static string PageId(AchievementGame game, HashSet<string> taken)
    {
        var sb = new StringBuilder("g-");
        foreach (var ch in game.Id.ToLowerInvariant())
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb[^1] != '-') sb.Append('-');
        var baseId = sb.Length > 24 ? sb.ToString(0, 24) : sb.ToString();
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++) id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    /// <summary>"ACH_WIN_ONE_GAME" reads better as "Win one game".</summary>
    private static string Pretty(string name)
    {
        // Steam gives real titles ("Win one game", "First Blood"): only the file style (ACH_WIN_ONE_GAME) is reworked.
        if (!name.Contains('_') && name.Any(char.IsLower)) return name.Trim();
        var s = name.Replace('_', ' ').Trim();
        if (s.StartsWith("ACH ", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.Length == 0) return name;
        s = s.ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static readonly (string Id, string De, string En)[] Planned =
    [
        ("retro", "RetroAchievements", "RetroAchievements"),
        ("xbox", "Xbox", "Xbox"),
        ("psn", "PlayStation", "PlayStation"),
        ("emu", "Emulatoren (Xenia, RPCS3, shadPS4)", "Emulators (Xenia, RPCS3, shadPS4)"),
        ("stores", "Epic, GOG, EA, Battle.net", "Epic, GOG, EA, Battle.net"),
        ("manual", "Manuell", "Manual")
    ];

    private IReadOnlyList<PluginRow> SteamSetupRows()
    {
        var row = SteamRow(_context.Settings.GetBool("steam", true));
        var id = SteamAccountToUse();
        var rows = new List<PluginRow>
        {
            new("state", T("Steam", "Steam")) { Section = "state", Value = row.Value, Detail = row.Detail, On = row.On, RequiresUnlock = false },
            new("account", T("Dein Steam-Konto", "Your Steam account"))
            {
                Section = "state",
                Value = id is null ? T("nicht gefunden", "not found") : id.Persona,
                Detail = id is null
                    ? T("Melde dich in der Steam-App an. Mega Hub liest das Konto von dort, du musst keine Nummer tippen.", "Sign in in the Steam app. Mega Hub reads the account from there, you do not have to type a number.")
                    : T("Gefunden in der Steam-App (Steam-ID " + id.SteamId + ")", "Found in the Steam app (Steam ID " + id.SteamId + ")"),
                RequiresUnlock = false
            },
            new("s1", T("1. Steams Schlüssel-Seite öffnen", "1. Open Steam's key page"))
            {
                Section = "steps",
                Kind = PluginRowKind.Action,
                Detail = T("Drück hier. Melde dich dort bei Steam an. Bei „Domain Name“ tippst du: localhost", "Press here. Sign in to Steam there. For \"Domain Name\" type: localhost"),
                RequiresUnlock = false
            },
            new("s2", T("2. Den Schlüssel kopieren", "2. Copy the key"))
            {
                Section = "steps",
                Detail = T("Steam zeigt unter „Key“ einen langen Code aus Buchstaben und Zahlen. Markieren und kopieren (Strg+C), oder mit „Text senden“ vom Handy.", "Steam shows a long code of letters and numbers under \"Key\". Select it and copy (Ctrl+C), or use \"Send text\" from the phone."),
                RequiresUnlock = false
            },
            new("s3", T("3. Den Schlüssel in Mega Hub einfügen", "3. Paste the key into Mega Hub"))
            {
                Section = "steps",
                Detail = T("Einstellungen › Plugins & Skins › Erfolge › „Steam-Schlüssel einfügen“", "Settings › Plugins & skins › Achievements › \"Paste your Steam key\""),
                RequiresUnlock = false
            },
            new("s4", T("4. In Steam das Profil öffentlich stellen", "4. Make your Steam profile public"))
            {
                Section = "steps",
                Detail = T("Steam › Profil bearbeiten › Datenschutz › „Spieldetails“ auf Öffentlich. Sonst sieht Mega Hub keine Erfolge.", "Steam › Edit Profile › Privacy Settings › \"Game details\" = Public. Otherwise Mega Hub sees no achievements."),
                RequiresUnlock = false
            },
            new("refresh", T("Jetzt neu laden", "Refresh now"))
            {
                Section = "steps",
                Kind = PluginRowKind.Action,
                Detail = T("Fragt Steam sofort noch einmal", "Asks Steam again right away"),
                RequiresUnlock = false
            }
        };
        return rows;
    }

    private bool HasSteamKey() => !string.IsNullOrWhiteSpace(_context.Settings.Get("steamkey"));

    /// <summary>The Steam ID to ask about: the one typed in the settings, else the account Steam is signed in with on this PC.</summary>
    private SteamAccount? SteamAccountToUse()
    {
        var typed = _context.Settings.Get("steamid")?.Trim();
        if (SteamAchievements.IsSteamId(typed)) return new SteamAccount(typed!, typed!);
        return SteamAchievements.FindAccount();
    }

    private PluginRow SteamRow(bool on)
    {
        SteamStatus status;
        string persona, progress;
        lock (_gate)
        {
            status = on ? _steamStatus : SteamStatus.Off;
            persona = _steamPersona;
            progress = _steamProgress;
        }
        var (value, detail) = status switch
        {
            SteamStatus.Off => (T("Aus", "Off"), T("Schalter in den Plugin-Einstellungen", "Switch in the plugin settings")),
            SteamStatus.NoKey => (T("Schlüssel fehlt", "Key needed"), T("Drück hier: Schritt für Schritt zum kostenlosen Schlüssel", "Press here: step by step to your free key")),
            SteamStatus.NoAccount => (T("Kein Konto gefunden", "No account found"), T("Melde dich in Steam an oder trag die Steam-ID in den Plugin-Einstellungen ein", "Sign in to Steam, or type the Steam ID in the plugin settings")),
            SteamStatus.Loading => (T("Lädt …", "Loading ..."), progress),
            SteamStatus.Ready => (T("Verbunden", "Connected"), persona.Length > 0 ? persona : null),
            SteamStatus.Private => (T("Profil ist privat", "Profile is private"), T("In Steam: Profil › Datenschutz › Spieldetails auf Öffentlich stellen", "In Steam: Profile › Privacy settings › set Game details to Public")),
            SteamStatus.BadKey => (T("Schlüssel abgelehnt", "Key not accepted"), T("Prüfe den Schlüssel in den Plugin-Einstellungen", "Check the key in the plugin settings")),
            SteamStatus.Blocked => (T("Blockiert", "Blocked"), T("Einstellungen › Datenschutz: der Steam-Dienst muss an sein und Offline aus", "Settings › Privacy: the Steam service must be on and Offline off")),
            _ => (T("Fehler", "Error"), T("Steam hat nicht geantwortet. Ein Druck versucht es noch einmal", "Steam did not answer. Press to try again"))
        };
        return new PluginRow("src-steam", "Steam")
        {
            Section = "steam",
            Kind = new PluginRowKind.Link(SteamPage),
            Value = value,
            Detail = detail,
            On = status == SteamStatus.Ready,
            RequiresUnlock = false
        };
    }

    /// <summary>Starts a background refresh of Steam when there is a key and the last one is old (or failed).</summary>
    private void StartSteamIfDue(bool on)
    {
        if (!on)
        {
            lock (_gate) _steamStatus = SteamStatus.Off;
            return;
        }
        if (!HasSteamKey())
        {
            lock (_gate) _steamStatus = SteamStatus.NoKey;
            return;
        }
        var account = SteamAccountToUse();
        if (account is null)
        {
            lock (_gate) _steamStatus = SteamStatus.NoAccount;
            return;
        }
        var http = _context.ServiceAllowed("steam") ? _context.Http("steam") : null;
        if (http is null)
        {
            lock (_gate) _steamStatus = SteamStatus.Blocked;
            return;
        }
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_steamRunning) return;
            // A good answer is kept for half an hour, a failed one for two minutes (so a failure never turns into a loop of requests).
            var age = DateTimeOffset.UtcNow - _steamFetched;
            if (_steamStatus is SteamStatus.Ready or SteamStatus.Private or SteamStatus.BadKey && age < SteamRefreshAfter) return;
            if (_steamStatus == SteamStatus.Error && age < TimeSpan.FromMinutes(2)) return;
            _steamRunning = true;
            _steamStatus = SteamStatus.Loading;
            _steamPersona = account.Persona;
            _steamProgress = "";
            _steamCts = cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        }
        var key = _context.Settings.Get("steamkey")!.Trim();
        _ = Task.Run(async () =>
        {
            SteamAchievements.Outcome outcome;
            try
            {
                outcome = await SteamAchievements.FetchAsync(http, key, account.SteamId, (done, total) =>
                {
                    lock (_gate) _steamProgress = $"{done} / {total}";
                    _context.Invalidate(ListPage);
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A new key or switch ends this run; the next page build starts a fresh one.
                lock (_gate)
                {
                    _steamRunning = false;
                    _steamStatus = SteamStatus.Error;
                    _steamFetched = DateTimeOffset.MinValue;
                }
                _context.Invalidate(ListPage);
                return;
            }
            catch (Exception ex)
            {
                _context.Log("steam refresh failed: " + ex.GetType().Name);
                outcome = new SteamAchievements.Outcome(SteamStatus.Error, []);
            }
            lock (_gate)
            {
                _steamRunning = false;
                _steamStatus = outcome.Status;
                _steamFetched = DateTimeOffset.UtcNow;
                if (outcome.Status == SteamStatus.Ready) _steamGames = outcome.Games;
                else if (outcome.Status is SteamStatus.BadKey or SteamStatus.Private) _steamGames = [];
            }
            _context.Invalidate(ListPage);
        });
    }

    /// <summary>A press on a row of the list page: the Steam rows do something, the rest are plain.</summary>
    private string? ActivateRow(string rowId)
    {
        if (rowId is "src-steamkey" or "s1")
            return _context.OpenUrl("https://steamcommunity.com/dev/apikey")
                ? T("Geöffnet. Melde dich bei Steam an, tippe bei Domain Name: localhost, und kopiere den Schlüssel.", "Opened. Sign in to Steam, type localhost under Domain Name, and copy the key.")
                : T("Konnte die Seite nicht öffnen: steamcommunity.com/dev/apikey", "Could not open the page: steamcommunity.com/dev/apikey");
        if (rowId is "src-steam" or "refresh")
        {
            if (!HasSteamKey()) return T("Erst den Schlüssel holen (Schritt 1) und einfügen (Schritt 3).", "Get the key first (step 1) and paste it (step 3).");
            ResetSteam();
            return T("Steam wird neu abgefragt …", "Asking Steam again ...");
        }
        return null;
    }

    private string Tier(int percent) => percent switch
    {
        >= 100 => T("Platin", "Platinum"),
        >= 75 => T("Gold", "Gold"),
        >= 50 => T("Silber", "Silver"),
        _ => T("Bronze", "Bronze")
    };
}
