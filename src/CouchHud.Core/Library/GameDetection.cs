using System.Text.RegularExpressions;
using CouchHud.Core.Guard;

namespace CouchHud.Core.Library;

/// <summary>What the folder scan knows about one exe below a game folder: its path relative to that folder, its size and its version resource.</summary>
public sealed record ExeFacts(string RelativePath, long Size, string? ProductName = null, string? FileDescription = null)
{
    public string FileName => Path.GetFileName(RelativePath);

    public string Stem => Path.GetFileNameWithoutExtension(RelativePath);

    /// <summary>The folder of the exe relative to the game folder ("" for one directly inside), with backslashes.</summary>
    public string Dir => (Path.GetDirectoryName(RelativePath.Replace('/', '\\')) ?? "").Trim('\\');

    public int Depth => Dir.Length == 0 ? 0 : Dir.Count(c => c == '\\') + 1;
}

/// <summary>The exe that starts the game and the name the entry gets. <paramref name="NameSource"/> is the exe whose metadata named it (the real game binary behind a stub).</summary>
public sealed record ExePick(ExeFacts Exe, string Name, ExeFacts NameSource);

/// <summary>
/// Which exe in a game folder is the game, and what the game is called. Pure rules (no file access), so they are tested with the
/// folders found on real machines: repacks put checksum tools (RapidCRC, QuickSFV) and their own updaters next to the game, Unreal
/// games have a small start stub in the folder and the real binary under &lt;Project&gt;\Binaries\Win64, store clients (a Steam folder
/// below D:\Games) are no games, and folder names carry noise ("- Crack by …", scene tags, version numbers, console title ids).
/// Picks: never a helper (uninstaller, crash handler, redistributable, anti-cheat service, checksum tool, downloader); then the exe
/// named like the folder (by file name or product name), no launcher when there is something else, the shallower, the bigger (the
/// real binary behind a stub counts with its size). Names: the product name of the version resource when it says something (not
/// "BootstrapPackagedGame", "Game" or the exe's own abbreviation), the folder name when it is more specific, else the cleaned folder name.
/// </summary>
public static partial class GameDetection
{
    /// <summary>Exe names that are never the game. Matched anywhere in the file name (without .exe).</summary>
    [GeneratedRegex(@"unins|uninstall|setup|install|redist|vcredist|vc_redist|directx|dxsetup|dxwebsetup|dotnet|ndp\d|crash(handler|report|pad|sender|dump|log|_handler|uploader)|^crashpad|bugsplat|report|updater|update|patcher|helper|prereq|physx|oalinst|easyanticheat|anticheat|battleye|beservice|eac_|cefsharp|cefprocess|webhelper|notification_helper|quicksfv|rapidcrc|hashcheck|^md5|sfv$|checksum|config|settings|benchmark|server$|dedicated|editor|sdk|tool|downloader|broker|attestation|cleaner|minidump|errorreporter|^dxwebsetup|^ue[45]prereq",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkExe();

    /// <summary>Version resources that say "not the game" (checksum tools, runtimes, crash handlers, setups of a repack).</summary>
    [GeneratedRegex(@"rapidcrc|quicksfv|hashcheck|redistributable|directx.*setup|web setup|crash ?handler|crash ?report|crash logs|setup/uninstall|uninstall|installer|patch downloader|launch helper|error ?reporter|minidump|anti-?cheat|battleye|easyanticheat|prerequisites|\bsetup\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkMetadata();

    /// <summary>Folders whose exes are never the game.</summary>
    [GeneratedRegex(@"(^|\\)(_?commonredist|redist|redistributables?|directx|support|installers?|__installer|prereqs?|EasyAntiCheat|BattlEye|Engine\\Binaries\\ThirdParty)(\\|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkFolder();

    /// <summary>A start that hands over to something else: only taken when nothing else is there.</summary>
    [GeneratedRegex(@"launcher", RegexOptions.IgnoreCase)]
    private static partial Regex Launcherish();

    /// <summary>Product names that name the engine, the vendor or nothing at all.</summary>
    [GeneratedRegex(@"^(unity|unreal|microsoft|windows|game|launcher|application|app|bootstrappackagedgame|unrealgame|ue4game|ue5game|crashreportclient|steam|epic games)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericProduct();

    /// <summary>The Unreal suffix of a game binary: "Foo-Win64-Shipping".</summary>
    [GeneratedRegex(@"-(win64|wingdk|win32)(-shipping|-test|-debug)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealSuffix();

    /// <summary>Whether an exe is a helper and never the game: by its name, its folder or its version resource.</summary>
    public static bool IsJunk(ExeFacts exe) =>
        JunkExe().IsMatch(exe.Stem)
        || JunkFolder().IsMatch(exe.Dir)
        || exe.ProductName is { } p && JunkMetadata().IsMatch(p)
        || exe.FileDescription is { } d && JunkMetadata().IsMatch(d);

    /// <summary>The name check alone (for callers without metadata).</summary>
    public static bool IsJunkName(string fileName) => JunkExe().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>The main exes of the store clients; helpers such as steamerrorreporter or steamwebhelper do not make a folder a client's (a Source game keeps a steamerrorreporter in its bin folder).</summary>
    private static readonly HashSet<string> ClientExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "upc.exe", "Battle.net.exe"
    };

    /// <summary>Client exes with names no game has: taken at any depth (Epic keeps its launcher under Launcher\Portal\Binaries\Win64).</summary>
    private static readonly HashSet<string> DistinctiveClientExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EpicGamesLauncher.exe", "GalaxyClient.exe", "UbisoftConnect.exe", "EADesktop.exe"
    };

    /// <summary>
    /// A store client's own folder (Steam, Epic, EA, Ubisoft, GOG Galaxy, Battle.net …): its games belong to the store's scanner. Recognised by the
    /// client's main exe (not its helpers) directly in the folder, or Steam's library folder (steamapps).
    /// </summary>
    public static bool IsStoreClientFolder(IEnumerable<ExeFacts> exes, bool hasSteamApps) =>
        hasSteamApps || exes.Any(e => DistinctiveClientExes.Contains(e.FileName) || e.Depth == 0 && ClientExes.Contains(e.FileName));

    /// <summary>
    /// The game exe of one folder and its name; null when there is none (only helpers, or a store client's folder). <paramref name="folderName"/>
    /// is the folder's own name (the game folder below the watched one).
    /// </summary>
    public static ExePick? Pick(string folderName, IReadOnlyList<ExeFacts> exes, bool hasSteamApps = false)
    {
        if (IsStoreClientFolder(exes, hasSteamApps)) return null;
        var candidates = exes.Where(e => !IsJunk(e) && !IsInterpreter(e)).ToList();
        // A game whose own name has a helper's word in it ("Toolbox.exe" in "Toolbox"): taken when it is named like the folder and nothing else
        // (its folder, its version resource) says helper.
        if (candidates.Count == 0)
        {
            candidates = exes.Where(e => !IsInterpreter(e) && !JunkFolder().IsMatch(e.Dir) && Similar(e, folderName)
                && !(e.ProductName is { } p && JunkMetadata().IsMatch(p)) && !(e.FileDescription is { } d && JunkMetadata().IsMatch(d))
                && !Regex.IsMatch(e.Stem, @"unins|setup|redist|crash|rapidcrc|quicksfv|anticheat|beservice", RegexOptions.IgnoreCase)).ToList();
        }
        if (candidates.Count == 0) return null;

        // Unreal: the stub in the folder starts <Stub>\Binaries\Win64\…; the pair counts as one, started by the stub (like the stores do),
        // ranked and named by the real binary.
        var bodies = new Dictionary<ExeFacts, ExeFacts>();
        foreach (var stub in candidates.Where(c => c.Depth == 0))
        {
            var body = candidates
                .Where(c => c != stub && IsUnrealBody(c, stub))
                .OrderByDescending(c => c.Size)
                .FirstOrDefault();
            if (body is not null) bodies[stub] = body;
        }
        var paired = bodies.Values.ToHashSet();
        var ranked = candidates.Where(c => !paired.Contains(c))
            .Select(c => (Exe: c, Body: bodies.GetValueOrDefault(c) ?? c))
            .OrderByDescending(x => Similar(x.Exe, folderName) || Similar(x.Body, folderName))
            .ThenBy(x => Launcherish().IsMatch(x.Exe.Stem) || x.Exe.FileDescription is { } d && Launcherish().IsMatch(d))
            .ThenBy(x => x.Exe.Depth)
            .ThenByDescending(x => x.Body.Size)
            .ToList();
        if (ranked.Count == 0) return null;
        var (exe, source) = ranked[0];
        return new ExePick(exe, NameFor(folderName, exe, source), source);
    }

    /// <summary>"Foo.exe" in the folder and "Foo\Binaries\Win64\Foo[-Win64-Shipping].exe" (or any exe there when the stub says it is Unreal's).</summary>
    private static bool IsUnrealBody(ExeFacts candidate, ExeFacts stub)
    {
        var dir = candidate.Dir;
        var parts = dir.Split('\\');
        if (parts.Length != 3 || !parts[1].Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            || !parts[2].StartsWith("Win", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[0].Equals(stub.Stem, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(stub.ProductName?.Trim(), "BootstrapPackagedGame", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterpreter(ExeFacts exe) => !GameEntryRules.MayBeGame(exe.FileName, "");

    /// <summary>Named like the folder: the file name (without Unreal's suffix) or a telling product name inside the folder name, or the other way round.</summary>
    private static bool Similar(ExeFacts exe, string folderName)
    {
        var folder = Compact(folderName);
        if (folder.Length == 0) return false;
        var stem = Compact(UnrealSuffix().Replace(exe.Stem, ""));
        if (stem.Length >= 3 && (folder.Contains(stem) || stem.Contains(folder))) return true;
        var product = TellingProduct(exe);
        if (product is null) return false;
        var p = Compact(product);
        return p.Length >= 3 && (folder.Contains(p) || p.Contains(folder));
    }

    private static string Compact(string s) => new(NameMatch.Canonical(s).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// The product name of the version resource when it names the game: not empty, not the engine or vendor ("Unity", "BootstrapPackagedGame",
    /// "Game"), not a helper's, and not just the exe's own short name ("COTEG" for COTEG.exe says nothing a folder name doesn't say better).
    /// </summary>
    internal static string? TellingProduct(ExeFacts exe)
    {
        var product = CleanProduct(exe.ProductName);
        if (product is null || product.Length < 2 || !product.Any(char.IsLetter)) return null;
        if (GenericProduct().IsMatch(product) || JunkMetadata().IsMatch(product)) return null;
        var stem = UnrealSuffix().Replace(exe.Stem, "");
        if (!product.Contains(' ') && string.Equals(Compact(product), Compact(stem), StringComparison.Ordinal)) return null;
        return product;
    }

    /// <summary>The name of the entry: see the class.</summary>
    internal static string NameFor(string folderName, ExeFacts exe, ExeFacts source)
    {
        var folder = CleanFolderName(folderName);
        var product = TellingProduct(source) ?? TellingProduct(exe);
        if (product is not null)
        {
            // "Call of Duty" in a folder "Call of Duty Modern Warfare 4": the folder says which one.
            var p = NameMatch.Canonical(product);
            var f = NameMatch.Canonical(folder);
            if (p.Length > 0 && f.Length > p.Length && f.StartsWith(p + " ", StringComparison.Ordinal)) return folder;
            return product;
        }
        if (folder.Length > 0) return folder;
        return UnrealSuffix().Replace(exe.Stem, "");
    }

    /// <summary>Trademark signs out, spaces tidied.</summary>
    internal static string? CleanProduct(string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return null;
        var s = product.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("\uFFFD", " ");
        s = Spaces().Replace(s, " ").Trim();
        s = Regex.Replace(s, @"\s+([:,])", "$1");
        return s.Length == 0 ? null : s;
    }

    // ----- folder names -----

    /// <summary>Release groups and repackers whose tag ends up in folder names.</summary>
    private static readonly string[] Groups =
    [
        "CODEX", "PLAZA", "SKIDROW", "RELOADED", "PROPHET", "CPY", "HOODLUM", "DARKSiDERS", "TiNYiSO", "SiMPLEX", "RUNE", "TENOKE", "FLT", "DOGE",
        "EMPRESS", "GOLDBERG", "ANOMALY", "Razor1911", "DINOByTES", "I_KnoW", "Chronos", "ElAmigos", "El Amigos", "DODI", "FitGirl", "KaOs",
        "xatab", "R.G. Mechanics", "SteamRip", "Steam-Rip", "P2P", "GOG", "PORTABLE", "REPACK", "MULTi", "RIP", "DRMFREE"
    ];

    [GeneratedRegex(@"\s*[\[\{\(]([^\]\}\)]*)[\]\}\)]\s*")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+[-–—]\s+.*\b(crack(ed)?|repack|rip|by|update|build|patch|incl|dlcs?|v\d)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoisyTail();

    [GeneratedRegex(@"(?<![a-z0-9])(?:v\s?\d+(?=[._]\d)(?:[._]\d+)*|(?-i:v)\s?\d+(?:[._]\d+)*)[a-z]?(?![a-z0-9])|(?<![a-z0-9])\d+(?:\.\d+){2,}(?![a-z0-9])|\bbuild[\s._]?\d+\b|\bb\d{4,}\b|\bupdate[\s._]?\d+(?:[._]\d+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex Version();

    [GeneratedRegex(@"\b(CUSA|PPSA|NPUB|NPEB|NPUA|NPEA|NPJB|BLUS|BLES|BCUS|BCES|BLJM|BCAS|BCJS|SLUS|SLES|SCUS|SCES|SLPS|ULUS|ULES|UCUS|UCES|PCSE|PCSB)[-_ ]?\d{5}\b|\b0100[0-9A-F]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex TitleId();

    [GeneratedRegex(@"\b(MULTi\d*|x64|x86|Win64|Win32|DX1[12]|Cracked|Crack|Repack|Portable|DRM[- ]?Free|Early Access|All DLCs?|Steam ?Rip)\b|(\bIncl\b|\bIncluding\b|\+\s*\d*\s*DLCs?\b|\+\s*Bonus\b).*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseWords();

    [GeneratedRegex(@"\b(\d{1,2})\s+(?:19|20)\d\d$")]
    private static partial Regex NumberThenYear();

    [GeneratedRegex(@"^(?:19|20)\d\d$")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// A folder name as a game name: brackets with tags or versions out (a year in brackets stays, it tells remakes apart), a tail like
    /// "- Crack by …" out, release group tags, version numbers ("v1.2.3", "Build 123", "Update 5"), console title ids (CUSA12345, BLUS30001,
    /// Switch ids), "MULTi12", "x64", "+ 3 DLCs" … out; scene style ("Game.Name-GROUP", "Game_Name") becomes words; a year after a sequel number
    /// ("Modern Warfare 4 2026") goes. What is left of nothing gives back the folder name with only the separators tidied.
    /// </summary>
    public static string CleanFolderName(string folderName)
    {
        var s = folderName.Trim();
        s = Bracketed().Replace(s, m => YearOnly().IsMatch(m.Groups[1].Value.Trim()) ? $" ({m.Groups[1].Value.Trim()}) " : " ");
        // Scene style without spaces: Game.Name.v1.2-GROUP → words first, but versions keep their dots until they are cut.
        var scene = !s.Contains(' ');
        s = s.Replace('_', ' ');
        if (scene)
        {
            s = Version().Replace(s, " ");
            s = Regex.Replace(s, @"[.\-]+", " ");
        }
        s = NoisyTail().Replace(s, "");
        s = TitleId().Replace(s, " ");
        s = Version().Replace(s, " ");
        s = NoiseWords().Replace(s, " ");
        // A group tag at the end ("-CODEX", " ElAmigos", "[FitGirl Repack]" is gone already).
        for (var changed = true; changed;)
        {
            changed = false;
            var trimmed = s.TrimEnd(' ', '-', '–', '.', '+', ',');
            foreach (var g in Groups)
            {
                if (trimmed.Length > g.Length && trimmed.EndsWith(g, StringComparison.OrdinalIgnoreCase)
                    && !char.IsLetterOrDigit(trimmed[trimmed.Length - g.Length - 1]))
                {
                    s = trimmed[..^g.Length];
                    changed = true;
                    break;
                }
            }
        }
        s = Spaces().Replace(s, " ").Trim().Trim('-', '–', '—', ':', '.', ',', '+', ' ');
        s = NumberThenYear().Replace(s, "$1");
        s = Spaces().Replace(s, " ").Trim();
        if (s.Length > 0 && s.Any(char.IsLetterOrDigit)) return s;
        var plain = Spaces().Replace(Regex.Replace(folderName, @"[_.\-]+", " "), " ").Trim();
        return plain.Length > 0 ? plain : folderName;
    }
}
