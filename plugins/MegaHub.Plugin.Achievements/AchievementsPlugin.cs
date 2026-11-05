using System.Globalization;
using CouchHud.Sdk;

namespace MegaHub.Plugin.Achievements;

/// <summary>
/// One Achievements page for Mega Hub, by russellite06 (written with Claude and ChatGPT). Version 0.1 reads local achievement files
/// (the Goldberg emulator's format) and shows progress per game with a tier; the other sources are listed as planned.
/// </summary>
public sealed class AchievementsPlugin : IPlugin
{
    private IPluginContext _context = null!;

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
            Id = "achievements",
            Title = T("Erfolge", "Achievements"),
            Description = T("Fortschritt je Spiel aus lokalen Erfolgsdateien", "Progress per game from local achievement files"),
            Sections =
            [
                new PluginSection("summary", T("Übersicht", "Overview")),
                new PluginSection("games", T("Spiele", "Games")),
                new PluginSection("sources", T("Quellen", "Sources"))
            ],
            Rows = ct => Task.Run<IReadOnlyList<PluginRow>>(() => BuildRows(ct), ct)
        });

        _context.RegisterTab(new PluginTab("achievements", "achievements", "Erfolge", "Achievements") { Area = "games" });
    }

    private IReadOnlyList<PluginRow> BuildRows(CancellationToken ct)
    {
        var rows = new List<PluginRow>();
        var localOn = _context.Settings.GetBool("local", true);
        var games = new List<AchievementGame>();
        if (localOn)
        {
            var roots = LocalAchievements.DefaultRoots().Concat(LocalAchievements.SplitFolders(_context.Settings.Get("folders")));
            try
            {
                games = LocalAchievements.Scan(roots, LocalAchievements.ParseNames(_context.Settings.Get("names")), ct);
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

        var earned = games.Sum(g => g.Earned);
        var total = games.Sum(g => g.Total);
        rows.Add(new PluginRow("sum-games", T("Spiele mit Erfolgen", "Games with achievements")) { Section = "summary", Value = games.Count.ToString(CultureInfo.CurrentCulture), RequiresUnlock = false });
        rows.Add(new PluginRow("sum-earned", T("Freigeschaltet", "Unlocked")) { Section = "summary", Value = $"{earned} / {total}", RequiresUnlock = false });
        rows.Add(new PluginRow("sum-done", T("Alle Erfolge geholt", "Fully completed"))
        {
            Section = "summary",
            Value = games.Count(g => g.Percent == 100).ToString(CultureInfo.CurrentCulture),
            RequiresUnlock = false
        });

        if (games.Count == 0)
        {
            rows.Add(new PluginRow("none", localOn ? T("Noch keine Erfolgsdateien gefunden", "No achievement files found yet"))
            {
                Section = "games",
                Detail = localOn
                    ? T("Weitere Ordner trägst du in den Plugin-Einstellungen ein", "Add more folders in the plugin settings")
                    : T("Das Lesen lokaler Dateien ist ausgeschaltet", "Reading local files is switched off"),
                RequiresUnlock = false
            });
        }

        foreach (var g in games)
        {
            var detail = $"{g.Percent} % · {Tier(g.Percent)}";
            if (g.LastUnlock is { } unlockedAt)
                detail += " · " + T("zuletzt ", "last ") + (g.LastName ?? "") + " (" + unlockedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) + ")";
            rows.Add(new PluginRow("game:" + g.Id, g.Name)
            {
                Section = "games",
                Detail = detail,
                Value = $"{g.Earned} / {g.Total}",
                On = g.Percent == 100,
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
