using System.Text.Json;
using CouchHud.Core.Library;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Remote;

/// <summary>The shape of a value a phone may set.</summary>
public enum RemoteSettingKind
{
    Bool,
    /// <summary>One of a fixed list of words.</summary>
    Choice,
    /// <summary>One of a fixed list of numbers.</summary>
    IntChoice,
    /// <summary>A whole number in a range.</summary>
    IntRange
}

/// <summary>One setting a phone may change: its key (the same key the TV page sends to the bridge's setSetting) and what values it takes.</summary>
public sealed record RemoteSetting(string Key, RemoteSettingKind Kind, IReadOnlyList<string>? Words = null, IReadOnlyList<int>? Numbers = null, int Min = 0, int Max = 0);

/// <summary>
/// The settings a phone with the level Full may change, as ONE fixed list (the positive list) with the type and the range of each value, and
/// what the phone gets back of the settings (a fixed projection). Everything not in the list is refused here, before the bridge sees it: the
/// list is the whole reach of the phone into the settings. The list only holds settings that take nothing away from the protection of the
/// PC (look, language, sound, the Home tab's mode, the screensaver); never the couch lock or any code, the parental
/// controls, profiles, the shell, the sign-in, services, the privacy switches, plugins and their rights, catalog sources, the phones' own
/// rights or anything that can start a program. Values go through the bridge's own setSetting / setSkin (the TV page's setters), so side
/// effects, the couch lock, the switchable areas and the parents' code are exactly the TV's.
/// </summary>
public static class RemoteSettings
{
    /// <summary>The action of the parents' code for the settings that bring a protected profile back to the picker (Hud.SetSettingAsync: RequireParentGrant("idleSetting", key)).</summary>
    public const string IdleAction = "idleSetting";

    /// <summary>The screensaver's choices, the same as the TV's picker (ui/views/settings.js, pinned by a check).</summary>
    public static readonly IReadOnlyList<int> ScreensaverMinutes = [0, 2, 5, 10, 15, 30];

    public static readonly IReadOnlyList<RemoteSetting> All =
    [
        new("language", RemoteSettingKind.Choice, Words: ["auto", "de", "en"]),
        new("favoritesMode", RemoteSettingKind.Choice, Words: [FavoritesShelf.ModeFavorites, FavoritesShelf.ModeRecent, FavoritesShelf.ModeCombined]),
        new("favoritesRecentDays", RemoteSettingKind.IntChoice, Numbers: FavoritesShelf.RecentDaysChoices),
        new("favoritesSites", RemoteSettingKind.Bool),
        new("screensaverMinutes", RemoteSettingKind.IntChoice, Numbers: ScreensaverMinutes),
        new("screensaverStyle", RemoteSettingKind.Choice, Words: ["art", "clock", "dim", "black"]),
        new("audioMode", RemoteSettingKind.Choice, Words: ["off", "ui", "music"]),
        new("soundVolume", RemoteSettingKind.IntRange, Min: 0, Max: 100),
        new("musicVolume", RemoteSettingKind.IntRange, Min: 0, Max: 100)
    ];

    private static readonly Dictionary<string, RemoteSetting> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>The setting of this key, or null for anything that is not in the list (also the skin, which has its own operation).</summary>
    public static RemoteSetting? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var s) ? s : null;

    /// <summary>
    /// The parents' code a change would need on the couch (the grant the host checks: action and entry), or null. With parental controls on
    /// and several profiles the screensaver brings a protected profile back to the picker, so it is the parents' (LockPolicy.ProfileIdleSettings).
    /// </summary>
    public static (string Action, string Item)? ParentScope(string key, bool profilesGuarded) =>
        profilesGuarded && Guard.LockPolicy.ProfileIdleSettings.Contains(key) ? (IdleAction, key) : null;

    /// <summary>Is this value one the setting takes (right type, in the list or the range)? Nothing is coerced: "5" is not 5, 1.5 is not 1.</summary>
    public static bool Accepts(RemoteSetting setting, JsonElement value)
    {
        switch (setting.Kind)
        {
            case RemoteSettingKind.Bool:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case RemoteSettingKind.Choice:
                return value.ValueKind == JsonValueKind.String && setting.Words!.Contains(value.GetString()!, StringComparer.Ordinal);
            case RemoteSettingKind.IntChoice:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && setting.Numbers!.Contains(n);
            default:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var r) && r >= setting.Min && r <= setting.Max;
        }
    }

    /// <summary>
    /// A skin's id as the TV page knows it: the Couch skin ("default") or an installed one ("user:&lt;folder&gt;"). Only the shape is checked
    /// here (the id is never a path: the host compares it with the skins it lists, SkinCatalog.IsKnown); no separators, no "..", no control characters.
    /// </summary>
    public static bool IsSkinId(string? id)
    {
        if (id is null or { Length: 0 } or { Length: > 100 }) return false;
        if (id == "default") return true;
        if (!id.StartsWith("user:", StringComparison.Ordinal) || id.Length == 5) return false;
        var folder = id[5..];
        return !folder.Contains("..", StringComparison.Ordinal) && folder.All(c => !char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            && folder.Trim() == folder && !folder.EndsWith('.');
    }

    /// <summary>
    /// The bridge call for "setting" ({key, value}) or "skin" ({id}): the key must be in the list and the value of its type and range; null for
    /// anything else (also extra fields do not matter, only these are read). The value is rebuilt from the checked one, not passed on as sent.
    /// </summary>
    public static RemoteCall? Map(string op, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        switch (op)
        {
            case "setting":
            {
                if (!args.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String || Find(k.GetString()) is not { } setting) return null;
                if (!args.TryGetProperty("value", out var v) || !Accepts(setting, v)) return null;
                object value = setting.Kind switch
                {
                    RemoteSettingKind.Bool => v.GetBoolean(),
                    RemoteSettingKind.Choice => v.GetString()!,
                    _ => v.GetInt32()
                };
                return new RemoteCall("setSetting", JsonSerializer.SerializeToElement(new object[] { setting.Key, value }));
            }
            case "skin":
            {
                if (!args.TryGetProperty("id", out var i) || i.ValueKind != JsonValueKind.String || i.GetString() is not { } id || !IsSkinId(id)) return null;
                return new RemoteCall("setSkin", JsonSerializer.SerializeToElement(new object[] { id }));
            }
            default:
                return null;
        }
    }

    /// <summary>The most skins a phone gets listed.</summary>
    public const int MaxSkins = 200;

    /// <summary>The longest skin name a phone gets.</summary>
    public const int MaxSkinName = 80;

    /// <summary>The skins the phone may pick: the Couch skin first, then the installed ones; only id and name, no style sheet, sound or path.</summary>
    public static List<Dictionary<string, object?>> SkinList(IEnumerable<SkinInfo> installed, string language)
    {
        var list = new List<Dictionary<string, object?>> { new() { ["id"] = "default", ["name"] = SkinCatalog.BuiltinName("default", language) } };
        foreach (var s in installed)
        {
            if (list.Count >= MaxSkins) break;
            if (!IsSkinId(s.Id) || s.Id == "default" || list.Any(x => (string)x["id"]! == s.Id)) continue;
            var name = new string(s.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length > MaxSkinName) name = name[..MaxSkinName];
            list.Add(new() { ["id"] = s.Id, ["name"] = name.Length > 0 ? name : s.Id[5..] });
        }
        return list;
    }

    /// <summary>
    /// What the phone gets of the settings: the values of the listed settings only (each checked like a value it could set; a value of
    /// another shape is left out), the skin now, the skins it may pick and the list itself (so the page shows exactly what the host accepts).
    /// Nothing else of the host's settings reaches the phone, whatever <paramref name="settings"/> holds.
    /// </summary>
    public static Dictionary<string, object?> Project(JsonElement settings, IEnumerable<SkinInfo> installed, string language)
    {
        var values = new Dictionary<string, object?>();
        if (settings.ValueKind == JsonValueKind.Object)
            foreach (var s in All)
                if (settings.TryGetProperty(s.Key, out var v) && Accepts(s, v))
                    values[s.Key] = s.Kind switch { RemoteSettingKind.Bool => v.GetBoolean(), RemoteSettingKind.Choice => v.GetString(), _ => v.GetInt32() };
        string? skin = null;
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("skin", out var sk) && sk.ValueKind == JsonValueKind.String && IsSkinId(sk.GetString())) skin = sk.GetString();
        return new Dictionary<string, object?>
        {
            ["values"] = values,
            ["skin"] = skin,
            ["skins"] = SkinList(installed, language),
            ["schema"] = All.Select(s => new Dictionary<string, object?>
            {
                ["key"] = s.Key,
                ["kind"] = s.Kind switch { RemoteSettingKind.Bool => "bool", RemoteSettingKind.Choice => "choice", RemoteSettingKind.IntChoice => "choice", _ => "range" },
                ["options"] = s.Kind switch { RemoteSettingKind.Choice => s.Words!.Cast<object>().ToList(), RemoteSettingKind.IntChoice => s.Numbers!.Cast<object>().ToList(), _ => null },
                ["min"] = s.Kind == RemoteSettingKind.IntRange ? s.Min : null,
                ["max"] = s.Kind == RemoteSettingKind.IntRange ? s.Max : null
            }).ToList()
        };
    }
}
