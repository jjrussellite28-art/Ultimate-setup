using System.Text.Json;
using CouchHud.Core.Library;
using CouchHud.Core.Security;
using CouchHud.Sdk;

namespace CouchHud.Core.Remote;

/// <summary>A phone operation turned into the couch's own bridge call: the method and its arguments exactly as the TV page sends them.</summary>
/// <param name="Method">The bridge method (<see cref="RemoteOp.BridgeMethod"/>).</param>
/// <param name="Args">The argument array.</param>
/// <param name="Favorite">For "favorite": the state the phone wants (the bridge only toggles; the HUD toggles only when it differs).</param>
public sealed record RemoteCall(string Method, JsonElement Args, bool? Favorite = null);

/// <summary>
/// The library operations of the phone: their arguments checked and shaped into the couch's bridge call (<see cref="Map"/>), and the slim
/// view of an entry the phone gets back (<see cref="Entry"/>). Nothing here decides a right: the level is <see cref="RemoteRights"/>, every
/// gate after that is the bridge's dispatch and the host method, as on the couch.
/// </summary>
public static class RemoteCalls
{
    public const int MaxIdLength = 256;
    public const int MaxNameLength = 100;
    public const int MaxCodeLength = 16;

    /// <summary>The sleep timer's choices, the same as the TV's picker (ui/core/sleep-timer-rules.js TIMER_MINUTES, pinned by a check).</summary>
    public static readonly IReadOnlyList<int> SleepTimerMinutes = [30, 60, 90, 120];

    /// <summary>The bridge call for an operation with its arguments (an object, as the phone sends them); null for anything malformed.</summary>
    public static RemoteCall? Map(string op, JsonElement args)
    {
        if (!RemoteRights.Ops.TryGetValue(op, out var known) || known.BridgeMethod is not { } method) return null;
        string? Id(string name = "id") => Text(args, name, MaxIdLength, allowEmpty: false);
        switch (op)
        {
            case "library" or "collections" or "downloads":
                return new RemoteCall(method, Array());
            case "launch" or "reloadArtwork":
                return Id() is { } id ? new RemoteCall(method, Array(id)) : null;
            case "favorite":
                return Id() is { } fav && Bool(args, "favorite") is { } want ? new RemoteCall(method, Array(fav), want) : null;
            case "rename":
            {
                if (Id() is not { } item || Name(args) is not { } name) return null;
                return new RemoteCall(method, Array(item, new Dictionary<string, object> { ["name"] = name }));
            }
            case "hide":
            {
                if (Id() is not { } item || Bool(args, "hidden") is not { } hidden) return null;
                return new RemoteCall(method, Array(item, new Dictionary<string, object> { ["hidden"] = hidden }));
            }
            case "collectionMember":
                return Id("collection") is { } col && Id() is { } member && Bool(args, "member") is { } add ? new RemoteCall(method, Array(col, member, add)) : null;
            case "collectionCreate":
                return Name(args) is { } created ? new RemoteCall(method, Array(created)) : null;
            case "collectionRename":
                return Id("collection") is { } renamed && Name(args) is { } newName ? new RemoteCall(method, Array(renamed, newName)) : null;
            case "collectionDelete":
                return Id("collection") is { } deleted ? new RemoteCall(method, Array(deleted)) : null;
            case "setting" or "skin":
                // Only the positive list of RemoteSettings: its keys, types and ranges; the call is rebuilt from the checked values.
                return RemoteSettings.Map(op, args);
            case "settings":
                return new RemoteCall(method, Array());
            case "parentCode":
            {
                // The buttons of the code (the couch's names), the action and the entry the PC asked for.
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Array) return null;
                var buttons = new List<string>();
                foreach (var b in code.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.String || !Passcode.Buttons.Contains(b.GetString()!) || buttons.Count >= MaxCodeLength) return null;
                    buttons.Add(b.GetString()!);
                }
                if (buttons.Count == 0 || Text(args, "action", 64, allowEmpty: false) is not { } action) return null;
                string? item = null;
                if (args.TryGetProperty("item", out var it))
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        if ((item = Text(args, "item", MaxIdLength, allowEmpty: false)) is null) return null;
                    }
                    else if (it.ValueKind != JsonValueKind.Null) return null;
                }
                return new RemoteCall(method, Array(buttons, action, item));
            }
            default:
                return null;
        }
    }

    /// <summary>The action and entry a parentCode call is for (already checked by <see cref="Map"/>).</summary>
    public static (string Action, string? Item) ParentScope(RemoteCall call) =>
        (call.Args[1].GetString()!, call.Args[2].ValueKind == JsonValueKind.String ? call.Args[2].GetString() : null);

    /// <summary>
    /// What the phone sees of an entry: id, name, kind, source, favourite, hidden, last played, play time and the cached cover's hash (the
    /// phone loads it through /art with its session's proof); no start target, folder or anything else of the PC.
    /// </summary>
    public static Dictionary<string, object?> Entry(LibraryItem item) => new()
    {
        ["id"] = item.Id,
        ["name"] = item.Name,
        ["type"] = item.Type.ToString().ToLowerInvariant(),
        ["source"] = item.Source,
        ["favorite"] = item.Favorite,
        ["hidden"] = item.Hidden,
        ["lastPlayed"] = item.LastPlayed,
        ["minutes"] = (long)Math.Round(item.PlaytimeMinutes),
        ["cover"] = CoverHash(item.Art.Cover) ?? CoverHash(item.Art.Square)
    };

    /// <summary>
    /// The library for a phone: hidden entries only for a phone that may show them again (<see cref="RemoteLevel.Full"/>); the list itself is
    /// the couch's (areas, profile and parental filters applied by the host).
    /// </summary>
    public static List<Dictionary<string, object?>> Library(IEnumerable<LibraryItem> items, RemoteLevel level) =>
        items.Where(i => level >= RemoteLevel.Full || !i.Hidden).Select(Entry).ToList();

    /// <summary>The ids of the entries a phone sees in its library (the same rule as <see cref="Library"/>).</summary>
    public static HashSet<string> VisibleIds(IEnumerable<LibraryItem> items, RemoteLevel level) =>
        items.Where(i => level >= RemoteLevel.Full || !i.Hidden).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The longest text the phone gets in a projected field (names; more is cut).</summary>
    public const int MaxProjectedText = 200;

    /// <summary>The most collection members / download lines a phone gets in one answer.</summary>
    public const int MaxProjectedItems = 2000;

    /// <summary>
    /// The answers of "downloads" and "collections" in a fixed shape, field by field (the same idea as <see cref="Entry"/>): the
    /// host's own records go to the TV page whole, the phone gets only the fields its page shows, so a field added to a record later
    /// (a path, an error detail, a plugin's text) never reaches a phone by itself. Anything of an unexpected shape becomes null. There is no
    /// "session" operation for phones: the running game's name reaches a phone only through the state and the power section, which apply
    /// the library's visibility rules (the HUD's GameForPhone); the TV's session record would hand it out unfiltered.
    /// For "collections", <paramref name="visible"/> is the ids of the entries this phone sees in its library: members are cut to them, and a
    /// collection that has members but none visible is left out whole (its name alone would tell what is hidden); empty ones stay.
    /// </summary>
    public static object? Project(string op, object? dto, IReadOnlySet<string>? visible = null)
    {
        if (dto is null) return null;
        var node = JsonSerializer.SerializeToElement(dto, RemoteChannel.Json);
        switch (op)
        {
            case "downloads":
            {
                if (node.ValueKind != JsonValueKind.Object) return null;
                var entries = new List<Dictionary<string, object?>>();
                if (node.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var e in list.EnumerateArray().Take(MaxProjectedItems).Where(e => e.ValueKind == JsonValueKind.Object))
                        entries.Add(new()
                        {
                            ["name"] = Str(e, "name"), ["kind"] = Str(e, "kind"), ["state"] = Str(e, "state"), ["progress"] = Num(e, "progress"),
                            ["bytesDone"] = Num(e, "bytesDone"), ["bytesTotal"] = Num(e, "bytesTotal"), ["isUpdate"] = Flag(e, "isUpdate")
                        });
                var summary = node.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object
                    ? new Dictionary<string, object?> { ["active"] = Num(s, "active"), ["finished"] = Num(s, "finished"), ["progress"] = Num(s, "progress") }
                    : null;
                return new Dictionary<string, object?> { ["entries"] = entries, ["summary"] = summary };
            }
            case "collections":
            {
                if (node.ValueKind != JsonValueKind.Array) return null;
                var result = new List<Dictionary<string, object?>>();
                foreach (var c in node.EnumerateArray().Take(MaxProjectedItems).Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    var members = new List<string>();
                    var had = false;
                    if (c.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var m in items.EnumerateArray())
                        {
                            if (m.ValueKind != JsonValueKind.String || m.GetString() is not { Length: > 0 and <= MaxIdLength } id) continue;
                            had = true;
                            if ((visible is null || visible.Contains(id)) && members.Count < MaxProjectedItems) members.Add(id);
                        }
                    if (visible is not null && had && members.Count == 0) continue;
                    result.Add(new() { ["id"] = Str(c, "id"), ["name"] = Str(c, "name"), ["items"] = members });
                }
                return result;
            }
            default:
                return null;
        }
    }

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = new string(v.GetString()!.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > MaxProjectedText ? s[..MaxProjectedText] : s;
    }

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The hash of the first image of a slot that goes through the artwork cache (an allowed source), else null.</summary>
    public static string? CoverHash(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (ArtworkSources.CacheUrl(url) is not { } cached) continue;
            var hash = cached[(cached.LastIndexOf('/') + 1)..];
            if (IsHash(hash)) return hash;
        }
        return null;
    }

    /// <summary>Exactly 64 lowercase hex characters (the cache's names).</summary>
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Text(JsonElement args, string name, int max, bool allowEmpty)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!;
        if (s.Length > max || (!allowEmpty && s.Length == 0) || s.Any(char.IsControl)) return null;
        return s;
    }

    /// <summary>A name for an entry or collection: trimmed, not empty, no control characters, at most <see cref="MaxNameLength"/>.</summary>
    private static string? Name(JsonElement args)
    {
        var s = Text(args, "name", MaxNameLength * 2, allowEmpty: false)?.Trim();
        return string.IsNullOrEmpty(s) || s.Length > MaxNameLength ? null : s;
    }

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static JsonElement Array(params object?[] values) => JsonSerializer.SerializeToElement(values);
}
