using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Skins;

public sealed record SkinInfo(string Id, string Name, string Description, IReadOnlyDictionary<string, string> Glyphs, string Css)
{
    /// <summary>UI event ("move", "select" …) → sound file URI, for skins that bring their own sounds.</summary>
    public IReadOnlyDictionary<string, string> Sounds { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// User skins: &lt;dataDir&gt;/skins/&lt;folder&gt;/skin.json + skin.css.
/// skin.json: { "name": "...", "description": "...", "glyphs": { "A": "✕" }, "sounds": { "select": "select.ogg" } }.
/// The stylesheet scopes itself via :root[data-skin="user:&lt;folder&gt;"].
/// Built-in skins live in the UI itself (ui/skins).
/// </summary>
public static class SkinCatalog
{
    private sealed record Manifest(string? Name, string? Description, Dictionary<string, string>? Glyphs, Dictionary<string, string>? Sounds);

    public static readonly string[] BuiltinIds = ["default", "ps4", "xbox", "cyberpunk", "nova"];

    /// <summary>A skin id the HUD may switch to: one of the built-in skins or an installed user skin. Anything else is refused, locked or not.</summary>
    public static bool IsKnown(string? id, IEnumerable<SkinInfo> userSkins) =>
        !string.IsNullOrEmpty(id) && (BuiltinIds.Contains(id) || userSkins.Any(s => s.Id == id));

    public static readonly string[] SoundEvents = ["move", "tab", "select", "back", "bump", "open", "close", "notify", "toggle", "boot", "music"];
    private static readonly string[] SoundTypes = [".wav", ".ogg", ".mp3"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<SkinInfo> ListUserSkins(string skinsDir)
    {
        if (!Directory.Exists(skinsDir)) return [];

        var skins = new List<SkinInfo>();
        foreach (var dir in Directory.EnumerateDirectories(skinsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(CouchHud.Core.Plugins.PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
            var css = Path.Combine(dir, "skin.css");
            var manifest = Path.Combine(dir, "skin.json");
            if (!File.Exists(css) || !File.Exists(manifest)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest), Options);
                skins.Add(new SkinInfo(
                    $"user:{folder}",
                    string.IsNullOrWhiteSpace(meta?.Name) ? folder : meta!.Name!,
                    meta?.Description ?? Strings.Get("skins.custom"),
                    meta?.Glyphs ?? new Dictionary<string, string>(),
                    new Uri(css).AbsoluteUri)
                {
                    Sounds = SoundsOf(dir, meta?.Sounds)
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.Error.WriteLine($"[skins] {folder}: {ex.Message}");
            }
        }
        return skins;
    }

    /// <summary>Only known events and audio files inside the skin's own folder.</summary>
    internal static IReadOnlyDictionary<string, string> SoundsOf(string dir, Dictionary<string, string>? sounds)
    {
        var result = new Dictionary<string, string>();
        if (sounds is null) return result;
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var (name, file) in sounds)
        {
            if (!SoundEvents.Contains(name) || string.IsNullOrWhiteSpace(file)) continue;
            var path = Path.GetFullPath(Path.Combine(dir, file));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !SoundTypes.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path)) continue;
            result[name] = new Uri(path).AbsoluteUri;
        }
        return result;
    }
}
