using System.Globalization;
using System.Text;
using CouchHud.Sdk;

namespace MegaHub.Plugin.Achievements;

/// <summary>
/// One Achievements page for Mega Hub, by russellite06 (written with Claude and ChatGPT). Version 0.2 reads local achievement files
/// (the Goldberg emulator's format), lists the games with progress and a tier, lets you sort and filter the list, and opens a page per game
/// with every achievement. The other sources are listed as planned.
/// </summary>
public sealed class AchievementsPlugin : IPlugin
{
    private const int MaxGamePages = 120;
    private const string ListPage = "achievements";

    private IPluginContext _context = null!;
    private readonly object _gate = new();
    private string _sort = "recent";
    private string _filter = "all";

    public string Id => "achievements";

    private string T(string de, string en) => _context.Language == "en" ? en : de;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        Register();
        context.Events.LanguageChanged += _ => Register();
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
            Description = T("Fortschritt je Spiel aus lokalen Erfolgsdateien", "Progress per game from local achievement files"),
            Sections =
            [
                new PluginSection("view", T("Ansicht", "View")),
                new PluginSection("summary", T("Übersicht", "Overview")),
                new PluginSection("games", T("Spiele", "Games")),
                new PluginSection("months", T("Freigeschaltet nach Monat", "Unlocked by month")),
                new PluginSection("sources", T("Quellen", "Sources"))
            ],
            Rows = ct => Task.Run<IReadOnlyList<PluginRow>>(() => BuildRows(ct), ct),
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
            rows.Add(new PluginRow("none", all.Count == 0 && localOn ? T("Noch keine Erfolgsdateien gefunden", "No achievement files found yet") : T("Keine Spiele in dieser Ansicht", "No games in this view"))
            {
                Section = "games",
                Detail = !localOn
                    ? T("Das Lesen lokaler Dateien ist ausgeschaltet", "Reading local files is switched off")
                    : T("Weitere Ordner trägst du in den Plugin-Einstellungen ein", "Add more folders in the plugin settings"),
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
        var s = name.Replace('_', ' ').Trim();
        if (s.StartsWith("ACH ", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.Length == 0) return name;
        s = s.ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static readonly (string Id, string De, string En)[] Planned =
    [
        ("steam", "Steam", "Steam"),
        ("retro", "RetroAchievements", "RetroAchievements"),
        ("xbox", "Xbox", "Xbox"),
        ("psn", "PlayStation", "PlayStation"),
        ("emu", "Emulatoren (Xenia, RPCS3, shadPS4)", "Emulators (Xenia, RPCS3, shadPS4)"),
        ("stores", "Epic, GOG, EA, Battle.net", "Epic, GOG, EA, Battle.net"),
        ("manual", "Manuell", "Manual")
    ];

    private string Tier(int percent) => percent switch
    {
        >= 100 => T("Platin", "Platinum"),
        >= 75 => T("Gold", "Gold"),
        >= 50 => T("Silber", "Silver"),
        _ => T("Bronze", "Bronze")
    };
}
