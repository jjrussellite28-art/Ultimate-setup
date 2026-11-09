using CouchHud.Sdk;

namespace MegaHub.Core.Guard;

/// <summary>
/// "Unlock first": the couch lock refused a call. An expected answer, not a fault: the bridge logs it as one line without a
/// stack trace and tells the page (error plus a locked flag), which asks for the code or shows nothing.
/// </summary>
public sealed class LockedException(string message) : InvalidOperationException(message);

/// <summary>
/// What a guest may do while the couch lock is closed. Pure decisions; the callers (bridge, tools, Hud)
/// translate "no" into "unlock first".
/// </summary>
public static class LockPolicy
{
    /// <summary>
    /// A locked couch starts games: every library item of type game, from a store, a game folder, a plugin or an own entry
    /// (own entries and plugins can only be added with the couch unlocked). Apps and websites need the unlock.
    /// </summary>
    public static bool MayLaunchLocked(ItemType type) => type == ItemType.Game;

    /// <summary>
    /// Bridge methods of the UI that work while the couch is locked: playing, reading, sound, unlocking.
    /// Everything else (adding apps, plugins, settings, file browsing, uninstallers …) could start a program of the guest's
    /// choice, so it needs the code first. Leaving the couch checks in Hud itself.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "getLibrary", "getFavorites", "getGuideApps", "launch", "toggleFavorite", "toggleFavoriteSite", "getStoreInfo", "getDetails", "systemInfo", "getSettings",
        "enterPcMode", "holdUnlockForSetup", "power", "sleepTimerWarning", "sleepTimerArm", "quit", "restartMegaHub", "unlock", "confirmWindowsUnlock", "getSecurity", "setPasscode", "setCouchLock",
        // The service: its state, and the repair (Windows' UAC prompt is the gate; the way out of a fail-closed lock).
        "getServiceStatus", "repairService",
        // The reset of the service's damaged code file: UAC again (the service takes an administrator's approval only), and with a damaged
        // file no code can unlock the couch.
        "resetCodeStore",
        "getSession", "resumeGame", "quitGame", "getAudio", "setVolume", "setMute", "setAudioOutput", "getAppVolumes", "setAppVolume", "setAppMute", "getPads", "getBattery", "rumble",
        "getNews", "getFeeds", "assistantInfo", "askAssistant", "confirmAssistant", "resetAssistant",
        "getBrowser", "browserLayout", "browserFocus", "browserCommand", "displayOff", "displayOn", "getLaunchers", "getProfile", "capture", "getDownloads", "getDisplay", "getScreens",
        // The downloads page and the list of not installed games may be looked at; installing ("installGame"), starting an update, cancelling
        // ("cancelDownload") and opening a client's download page ("openClientDownloads") need the unlock.
        "getDownloadCenter", "dismissDownload", "getUninstalled", "getPerformance", "getCaptures",
        // Save games are not here on purpose: looking at them (getSaveInfo, getSaveSettings) and every change (backupSaves, restoreSaves,
        // deleteSaveBackup, setSaveSetting) need the unlock; restoring and deleting also the parents' code (Hud.RequireSaveChange). The
        // automatic backup after a game is the host's own work and needs no call. The "Continue playing" row lives on getLibrary.
        "getPlugins", "getPluginPage", "getPluginTabs", "getPluginGate", "getQuickActions", "runQuickAction", "getNotifications", "markNotificationsRead", "clearNotifications",
        // runQuickAction: Hud decides per entry (PluginPolicy.MayRun): only an entry of Mega Hub's own plugins that is marked AllowedWhileLocked.
        // Plugin rows: the bridge lets them through, Hud decides by the row (PluginPolicy.MayPress): only rows of Mega Hub's own plugins that
        // are marked AllowedWhileLocked; a third-party row never. The suggestions answer nothing while locked (they wait).
        "activatePluginRow", "activatePluginAction", "changePluginRow", "getPluginSuggestions",
        // A plugin's setting: the bridge lets it through, Hud decides by the function level (PluginSettingRules): on the locked couch only
        // "restricted" settings with a value that fits; admin (also a setting without any function, and the "$settings" visit) needs the unlock, nobody is nobody.
        "setPluginSetting",
        // The same gate asked before the page opens the editor (Hud.CheckPluginSetting): changes nothing, so a refusal comes before the keyboard.
        "checkPluginSetting",
        // A settings entry that opens a plugin page: the same gate (Hud.OpenPluginSettingPage); the page itself is decided again by getPluginPage.
        "openPluginSettingPage",
        // The answer to the automatic Bluetooth switch-on's countdown: "now" or "leave it off" (the guest could switch the radio off with
        // the keyboard's airplane key anyway; switching it off in Mega Hub, "setBluetooth", needs the code).
        "answerBluetoothRecovery",
        "getBluetooth", "getRemote", "leavePairing", "keyboardState", "sleepTimerState", "codeEntryState", "pairingState", "getUpdates", "getCatalogUpdates", "getSelfUpdate", "refreshUpdates", "getWeather",
        "listSkins", "setSkin", "getGameFolders", "getCollections", "getParental", "parentalCheck", "parentalCloseGame", "parentalAuth", "setParental", "setParentalItemLock", "resetParentalAllChildrenLocks", "parentalExtend", "parentalReset", "parentalAcceptClock", "parentalAdoptZone"
    };

    /// <summary>
    /// Settings a guest may change while locked: sound and look only. Not expert mode (up to 0.6.3 it was in here): it shows the admin's
    /// tools (startup options, start targets, developer settings), so switching it is an admin setting behind the unlock like the others.
    /// </summary>
    public static readonly IReadOnlySet<string> HarmlessSettings = new HashSet<string>
    {
        "soundVolume", "soundSet", "audioMode", "musicVolume", "trailerPreviews", "favoritesMode", "favoritesRecentDays", "rumble", "padGlyphs", "acceptedTerms",
        "screensaverMinutes", "screensaverStyle", "displayOffMinutes", "pixelShift", "reduceMotion", "pointer", "mouseUnlocked", "nightMode", "nightModeStrength"
    };

    /// <summary>
    /// Settings that bring a protected profile back to the profile picker (screensaver, display off). With parental controls and several
    /// profiles they are the parents' (review NEU-1): a child could otherwise switch them off on the locked couch and keep an adult's
    /// profile without limits forever. The host's own idle threshold (<see cref="MegaHub.Core.Config.ProfileIdleClock"/>) stays anyway.
    /// </summary>
    public static readonly IReadOnlySet<string> ProfileIdleSettings = new HashSet<string> { "screensaverMinutes", "displayOffMinutes" };

    /// <summary>A setting a guest may change on the locked couch; <paramref name="profilesGuarded"/>: parental controls on and several profiles.</summary>
    public static bool IsHarmlessSetting(string key, bool profilesGuarded) =>
        HarmlessSettings.Contains(key) && !(profilesGuarded && ProfileIdleSettings.Contains(key));
}
