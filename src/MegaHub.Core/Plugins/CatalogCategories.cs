using MegaHub.Core.Localization;

namespace MegaHub.Core.Plugins;

/// <summary>
/// The categories of the catalog. The index names one per entry ("category": "smartHome"); what is shown is always the core's own
/// DE/EN name for it, never free text from the index. An id this version does not know (a newer index, a typo, a catalog of someone else)
/// lands in <see cref="Other"/>; an index without the field keeps working (plugins: Other, skins: Skins).
/// </summary>
public static class CatalogCategories
{
    public const string SmartHome = "smartHome";
    public const string Streaming = "streaming";
    public const string Hardware = "hardware";
    public const string Media = "media";
    public const string Ai = "ai";
    public const string Information = "information";
    public const string Skins = "skins";
    public const string Hud = "hud";
    public const string Other = "other";

    /// <summary>The order the catalog lists them in; "other" is always last.</summary>
    public static readonly IReadOnlyList<string> All = [SmartHome, Streaming, Hardware, Media, Ai, Information, Hud, Skins, Other];

    /// <summary>
    /// The known id for the text in the index, or the fallback: Skins for a skin, Other for a plugin. Case, dashes, underscores and spaces do not
    /// matter ("Smart-Home" is "smartHome"). Skins may carry only skins or hud (a HUD skin); any other value makes them skins.
    /// </summary>
    public static string Normalize(string? raw, CatalogKind kind)
    {
        var fallback = kind == CatalogKind.Skin ? Skins : Other;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 40) return fallback;
        var key = new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var id in All)
        {
            if (id.ToLowerInvariant() != key) continue;
            // A skin is a skin or a HUD skin; every other category is for plugins.
            return kind == CatalogKind.Skin && id is not (Skins or Hud) ? Skins : id;
        }
        return fallback;
    }

    /// <summary>The name to show, in the current language (the core's text; an id that is not known reads as "Other").</summary>
    public static string Label(string category) => LabelIn(Strings.Language, category);

    /// <summary>The name in a given language ("de" or "en").</summary>
    public static string LabelIn(string language, string category) => Strings.GetIn(language, "catalog.category." + (All.Contains(category) ? category : Other) + "Name");
}
