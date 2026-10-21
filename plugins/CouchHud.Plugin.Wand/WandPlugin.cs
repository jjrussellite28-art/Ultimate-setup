using System.Diagnostics;
using CouchHud.Sdk;

namespace CouchHud.Plugin.Wand;

/// <summary>
/// Wand (formerly WeMod) brings cheats and mods for thousands of single-player games.
/// It finds the running game by itself, so this plugin only has to start it at the
/// right moment: from the guide menu, through the assistant, or with every game.
/// Also the reference example for CouchHud plugins: settings, a page, a quick action,
/// a tool, events and two languages, all in one small file.
/// </summary>
public sealed class WandPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private GameEvent? _running;

    public string Id => "wand";

    /// <summary>The text in the language CouchHud shows right now (the SDK reports "de" or "en").</summary>
    private string T(string de, string en) => _context.Language == "en" ? en : de;

    public void Initialize(IPluginContext context)
    {
        _context = context;

        Register();
        // Settings, page and guide-menu entry carry their texts from the moment they are registered:
        // register them again when the language changes (the same ids replace the earlier registration).
        // Rows and details are built on demand and ask for the language each time.
        context.Events.LanguageChanged += _ => Register();

        context.RegisterTool(new PluginTool
        {
            Name = "open_cheats",
            // English works best for the models, whatever language CouchHud shows.
            Description = "Open Wand (formerly WeMod), a tool with cheats and mods for single-player games. It attaches to the game that is running.",
            Writes = true,
            Run = (_, _) => Task.FromResult(Open() ?? "Wand opened")
        });

        context.Events.GameStarted += game =>
        {
            _running = game;
            // Never on its own while the couch is locked: Wand is a program of its own, and a guest must not get one by starting a game.
            if (!context.Locked && context.Settings.GetBool("autostart") && FindExe() is not null) Open();
        };
        context.Events.GameEnded += _ => _running = null;
    }

    private void Register()
    {
        _context.RegisterSettings(
            new PluginSetting
            {
                Key = "autostart",
                Label = T("Mit jedem Spiel starten", "Start with every game"),
                Description = T("Öffnet Wand im Hintergrund, sobald ein Spiel läuft", "Opens Wand in the background as soon as a game runs"),
                Kind = SettingKind.Toggle,
                Default = "false"
            },
            new PluginSetting
            {
                Key = "path",
                Label = T("Pfad zu Wand.exe", "Path to Wand.exe"),
                Description = T("Leer lassen für die automatische Suche", "Leave empty to search automatically"),
                Kind = SettingKind.Text
            });

        _context.RegisterQuickAction(new QuickAction
        {
            Id = "open",
            Icon = "🪄",
            Label = T("Cheats & Mods (Wand)", "Cheats & mods (Wand)"),
            Detail = () => FindExe() is null
                ? T("Wand ist nicht installiert", "Wand is not installed")
                : _running is { } g ? T($"Für {g.Name}", $"For {g.Name}") : T("Startet Wand", "Starts Wand"),
            Run = _ => Task.FromResult<string?>(Open())
        });

        _context.RegisterPage(new PluginPage
        {
            Id = "status",
            Title = "Wand",
            Icon = "🪄",
            Description = T("Cheats und Mods für das laufende Spiel", "Cheats and mods for the running game"),
            Rows = _ => Task.FromResult(Rows()),
            Activate = (row, _) => Task.FromResult(row switch
            {
                "open" => Open(),
                "get" => OpenUrl("https://www.wand.com"),
                _ => null
            })
        });
    }

    private IReadOnlyList<PluginRow> Rows()
    {
        var exe = FindExe();
        if (exe is null)
        {
            return
            [
                new PluginRow("state", T("Wand ist nicht installiert", "Wand is not installed"))
                {
                    Icon = "⚠",
                    Detail = T("Im PC-Modus von wand.com laden und installieren, dann hier wieder reinschauen", "Download and install it from wand.com in PC mode, then come back here")
                },
                new PluginRow("get", T("wand.com öffnen", "Open wand.com")) { Icon = "🌐" }
            ];
        }
        var auto = _context.Settings.GetBool("autostart");
        return
        [
            new PluginRow("state", "Wand") { Icon = "✓", Detail = T($"Gefunden: {exe}", $"Found: {exe}"), Value = T("Installiert", "Installed"), On = true },
            new PluginRow("open", T("Wand öffnen", "Open Wand"))
            {
                Icon = "🪄",
                Detail = _running is { } g ? T($"Cheats für {g.Name}", $"Cheats for {g.Name}") : T("Startet Wand oder holt es nach vorne", "Starts Wand or brings it to the front")
            },
            new PluginRow("auto", T("Mit jedem Spiel starten", "Start with every game"))
            {
                Icon = "⚙",
                Value = auto ? T("An", "On") : T("Aus", "Off"),
                On = auto,
                Detail = T("Umschalten in den Plugin-Einstellungen", "Switch it in the plugin settings")
            }
        ];
    }

    private string? Open()
    {
        var exe = FindExe();
        if (exe is null) return T("Wand ist nicht installiert", "Wand is not installed");
        if (_context.Locked) return T("Gesperrt: erst entsperren", "Locked: unlock first");
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) })?.Dispose();
            return _running is { } g ? T($"Wand wird für {g.Name} geöffnet", $"Opening Wand for {g.Name}") : T("Wand wird geöffnet", "Opening Wand");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log($"start failed: {ex.Message}");
            return T($"Wand ließ sich nicht starten: {ex.Message}", $"Wand could not be started: {ex.Message}");
        }
    }

    /// <summary>In CouchHud's own browser, never in the system's.</summary>
    private string? OpenUrl(string url) =>
        _context.OpenUrl(url) ? null : T("Die Seite ließ sich nicht öffnen (Couch entsperren?)", "The page could not be opened (unlock the couch?)");

    /// <summary>The setting, then the usual install places (Squirrel puts a launcher stub in the root folder).</summary>
    private string? FindExe()
    {
        var configured = _context.Settings.Get("path");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Wand", "Wand.exe"),
            Path.Combine(local, "WeMod", "WeMod.exe"),
            Path.Combine(local, "Programs", "Wand", "Wand.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
