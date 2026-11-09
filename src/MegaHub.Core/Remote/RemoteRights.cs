using MegaHub.Core.Localization;

namespace MegaHub.Core.Remote;

/// <summary>
/// What a paired phone may do, set per phone by the parents in Settings › Fernbedienung. New pairings start with <see cref="Control"/>,
/// phones paired before 0.7 have no level stored and are <see cref="Control"/> too: exactly what they could do before (buttons, text, power).
/// </summary>
public enum RemoteLevel
{
    /// <summary>"Nur Steuerung": d-pad and buttons, text into the TV, power and the sleep timer.</summary>
    Control = 0,
    /// <summary>"Bibliothek": also browse the library, start, favourites, downloads.</summary>
    Library = 1,
    /// <summary>"Voll": also rename, hide and show, collections, artwork and the few settings of <see cref="RemoteSettings"/>.</summary>
    Full = 2
}

/// <summary>One thing the phone can ask for: the level it needs and the bridge call it ends in (null: the remote's own path, see Hud).</summary>
/// <param name="Name">The operation's name on the wire.</param>
/// <param name="Level">The least level of the phone.</param>
/// <param name="BridgeMethod">The bridge method the couch uses for the same thing; every gate of that method applies.</param>
public sealed record RemoteOp(string Name, RemoteLevel Level, string? BridgeMethod);

/// <summary>
/// The phone's operations and who may use them. Every operation with a bridge method runs through the bridge's own dispatch (couch lock,
/// waiting profile picker, switchable areas, then the host method with its parental checks and function levels), never past it. A phone
/// can never do more than the couch would allow at that moment, and its level can only take things away.
/// </summary>
public static class RemoteRights
{
    public static readonly IReadOnlyDictionary<string, RemoteOp> Ops = new[]
    {
        // Control: what every paired phone has had since 0.3 (the power section since 0.6.4) and the sleep timer, which ends in the same power call.
        new RemoteOp("state", RemoteLevel.Control, null),
        new RemoteOp("button", RemoteLevel.Control, null),
        // Mega Hub: the Android companion's named commands (RemoteCommands); they end in the same buttons.
        new RemoteOp("command", RemoteLevel.Control, null),
        new RemoteOp("text", RemoteLevel.Control, null),
        new RemoteOp("powerPrepare", RemoteLevel.Control, RemotePower.BridgeMethod),
        new RemoteOp("power", RemoteLevel.Control, RemotePower.BridgeMethod),
        new RemoteOp("sleepTimer", RemoteLevel.Control, RemotePower.BridgeMethod),
        // Library: looking and playing, the same calls as the couch's library, Home and downloads pages.
        new RemoteOp("library", RemoteLevel.Library, "getLibrary"),
        new RemoteOp("collections", RemoteLevel.Library, "getCollections"),
        new RemoteOp("downloads", RemoteLevel.Library, "getDownloadCenter"),
        new RemoteOp("launch", RemoteLevel.Library, "launch"),
        new RemoteOp("favorite", RemoteLevel.Library, "toggleFavorite"),
        new RemoteOp("parentCode", RemoteLevel.Library, "parentalAuth"),
        // Full: changing the library, the same calls as the couch's edit menu.
        new RemoteOp("rename", RemoteLevel.Full, "editItem"),
        new RemoteOp("hide", RemoteLevel.Full, "editItem"),
        new RemoteOp("collectionMember", RemoteLevel.Full, "setCollectionMember"),
        new RemoteOp("collectionCreate", RemoteLevel.Full, "createCollection"),
        new RemoteOp("collectionRename", RemoteLevel.Full, "renameCollection"),
        new RemoteOp("collectionDelete", RemoteLevel.Full, "deleteCollection"),
        new RemoteOp("reloadArtwork", RemoteLevel.Full, "reloadArtwork"),
        // Full: the few settings of the positive list (RemoteSettings): read through getSettings (cut to the list), written through the TV page's
        // own setSetting / setSkin. Nothing else of the settings is reachable: the keys and values are checked in RemoteSettings.Map first.
        new RemoteOp("settings", RemoteLevel.Full, "getSettings"),
        new RemoteOp("setting", RemoteLevel.Full, "setSetting"),
        new RemoteOp("skin", RemoteLevel.Full, "setSkin"),
        // Full: the second factor (0.7): the phone reads the open question (without the number) and answers with one of three; the HUD
        // decides again who may (SecondFactorRules.MayConfirm). No bridge method: the TV page is never the one answering.
        new RemoteOp("secondFactor", RemoteLevel.Full, null),
        new RemoteOp("secondFactorAnswer", RemoteLevel.Full, null)
        // Artwork search and pick (getArtworkChoices / setArtwork with the online search of w064-detect): reserved for "artChoices" and
        // "setArt" at Full once the search is in; the phone page has the place for it (RemoteRights.ArtworkHook).
    }.ToDictionary(o => o.Name, StringComparer.Ordinal);

    /// <summary>Where the artwork picker will go (see the comment in <see cref="Ops"/>): the bridge methods it is to end in.</summary>
    public static readonly IReadOnlyList<string> ArtworkHook = ["getArtworkChoices", "setArtwork"];

    /// <summary>The level as stored ("control", "library", "full"); anything else, also nothing (phones paired before 0.7), is Control.</summary>
    public static RemoteLevel Parse(string? stored) => stored switch
    {
        "library" => RemoteLevel.Library,
        "full" => RemoteLevel.Full,
        _ => RemoteLevel.Control
    };

    public static string Name(RemoteLevel level) => level switch
    {
        RemoteLevel.Library => "library",
        RemoteLevel.Full => "full",
        _ => "control"
    };

    /// <summary>Exactly one of the stored names (for the parents' setting): anything else is refused.</summary>
    public static bool IsName(string? name) => name is "control" or "library" or "full";

    /// <summary>
    /// The level a phone has right now. A phone paired while a child's profile was on the couch (parental controls on) gets at most
    /// <see cref="RemoteLevel.Library"/>, and only while that profile is still the active one; with any other profile on the couch it is a
    /// plain remote control (<see cref="RemoteLevel.Control"/>), so it never borrows a parent's or a sibling's rights.
    /// In PC mode the couch lock and the parental controls are not enforced (the desktop is in use), so the library operations of a phone
    /// would run without them: the phone is a plain remote control there (<see cref="RemoteLevel.Control"/>), whatever its stored level.
    /// <paramref name="pcMode"/> has no default on purpose: a caller must say, never forget.
    /// </summary>
    public static RemoteLevel Effective(RemoteDevice device, string activeProfile, bool pcMode)
    {
        if (pcMode) return RemoteLevel.Control;
        var level = Parse(device.Level);
        if (!device.Child) return level;
        if (!string.Equals(device.Profile, activeProfile, StringComparison.Ordinal)) return RemoteLevel.Control;
        return level > RemoteLevel.Library ? RemoteLevel.Library : level;
    }

    /// <summary>The code of a refusal by level: "pcMode" when the phone would be allowed outside PC mode, else "level".</summary>
    public static string DenyCode(RemoteDevice device, string activeProfile, bool pcMode, string? op) =>
        pcMode && Allows(Effective(device, activeProfile, pcMode: false), op) ? "pcMode" : "level";

    /// <summary>The refusal's text for <see cref="DenyCode"/>.</summary>
    public static string DenyText(string code) => Strings.Get(code == "pcMode" ? "remote.level.pcMode" : "remote.level.denied");

    /// <summary>May a phone with this level ask for this operation (unknown operations never)?</summary>
    public static bool Allows(RemoteLevel level, string? op) => op is not null && Ops.TryGetValue(op, out var o) && level >= o.Level;

    /// <summary>
    /// May the parents' code be typed on this phone: only over the encrypted channel (every sealed operation is), never on a phone paired in
    /// a child's profile (the parents use the TV there, as the child does), and only when the code is the Mega Hub code (with Windows'
    /// own password as the code the confirmation is Windows' lock screen, at the TV).
    /// </summary>
    public static bool MayEnterParentCode(RemoteDevice device, bool sealedChannel, string couchLock) =>
        sealedChannel && !device.Child && couchLock == "code";

    /// <summary>
    /// May this phone end the running sleep timer? Anybody's phone may, except a child's phone: it ends only a timer a child's phone set
    /// (a parent's timer, one set at the TV, is not its to end).
    /// </summary>
    public static bool MayEndSleepTimer(bool deviceIsChild, bool timerSetByChildPhone) => !deviceIsChild || timerSetByChildPhone;

    /// <summary>The least level that gets an event of this kind; null: not for phones.</summary>
    public static RemoteLevel? EventLevel(string type) => type switch
    {
        "keyboard" or "sleepTimer" or "session" or "level" => RemoteLevel.Control,
        "library" or "downloads" or "collections" => RemoteLevel.Library,
        "settings" or "secondFactor" => RemoteLevel.Full,
        _ => null
    };
}
