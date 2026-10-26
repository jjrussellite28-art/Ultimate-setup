namespace CouchHud.Sdk;

/// <summary>
/// Supplies entries for the library: a store (GOG, Xbox), a media center
/// (Kodi, Jellyfin) or anything else that can be launched.
/// </summary>
public interface ILibraryProvider
{
    /// <summary>Display name, used in logs.</summary>
    string Name { get; }

    Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Entry point of a plugin. CouchHud loads every public, non-abstract type
/// implementing this interface from the DLLs in plugins/&lt;name&gt;/.
/// </summary>
public interface IPlugin
{
    /// <summary>Unique id, lower case, e.g. "kodi".</summary>
    string Id { get; }

    void Initialize(IPluginContext context);
}

public interface IPluginContext
{
    /// <summary>Private, writable folder for the plugin's settings and cache.</summary>
    string DataDirectory { get; }

    void RegisterLibraryProvider(ILibraryProvider provider);

    void Log(string message);

    // ---- SDK v1 ----

    /// <summary>
    /// The language CouchHud shows right now: "de" or "en". Texts registered once (settings, pages, quick actions)
    /// are written in this language; <see cref="IPluginEvents.LanguageChanged"/> says when to register them again.
    /// Hosts older than this member answer "de" (the default implementation), so plugins keep working.
    /// </summary>
    string Language => "de";

    // ---- SDK v2 ----

    /// <summary>
    /// True while the couch is locked (a guest or a child at the controller). A plugin must not start programs,
    /// open windows or switch things on its own then (an autostart on <see cref="IPluginEvents.GameStarted"/>, for example);
    /// <see cref="IPluginEvents.LockChanged"/> says when it changes. Hosts older than SDK 2 answer false (the default implementation).
    /// </summary>
    bool Locked => false;

    /// <summary>Values of the plugin's settings (see <see cref="RegisterSettings"/>).</summary>
    IPluginSettings Settings { get; }

    /// <summary>Game started/ended, PC mode, couch mode.</summary>
    IPluginEvents Events { get; }

    /// <summary>Settings shown on the plugin's page in CouchHud's settings.</summary>
    void RegisterSettings(params PluginSetting[] settings);

    /// <summary>A list page, opened from the plugin's entry in the settings.</summary>
    void RegisterPage(PluginPage page);

    /// <summary>An entry in the guide menu.</summary>
    void RegisterQuickAction(QuickAction action);

    /// <summary>An operation the AI assistant and MCP clients can use.</summary>
    void RegisterTool(PluginTool tool);

    /// <summary>Shows a notification on the TV and in the notification center.</summary>
    void Notify(string title, string body);

    /// <summary>
    /// Opens an http(s) address in CouchHud's own couch browser (never in the system's browser; other schemes are refused).
    /// Needs the unlocked couch. Returns false when refused. Older hosts answer false (the default implementation).
    /// </summary>
    bool OpenUrl(string url) => false;
}

/// <summary>
/// The SDK version this build of CouchHud implements. Plugins can declare the version
/// they need in plugin.json ("sdk": 1); newer ones are not loaded.
/// Version 2 adds <see cref="IPluginContext.Locked"/> and <see cref="IPluginEvents.LockChanged"/>; plugins for version 1 keep working unchanged.
/// </summary>
public static class SdkVersion
{
    public const int Current = 2;
}
