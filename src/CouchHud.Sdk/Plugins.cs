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

    // ---- SDK v3 ----
    // Every member below needs its capability (see PluginCapabilities); without it the host throws PluginCapabilityException.
    // Older hosts answer with the default implementations: nothing happens, false or null.

    /// <summary>
    /// A main tab that shows one of this plugin's pages (<see cref="PluginTab"/>). Capability "tabs". The host allows at most two tabs per
    /// plugin and six plugin tabs in all, refuses ids, titles and pictures that do not pass its checks (and titles that look like a
    /// built-in tab), and registering an id again replaces the earlier one. Older hosts do nothing (the default implementation).
    /// </summary>
    void RegisterTab(PluginTab tab) { }

    /// <summary>A page's rows changed (pushed state): the host asks <see cref="PluginPage.Rows"/> again and updates an open page. Capability "pages".</summary>
    void Invalidate(string pageId) { }

    /// <summary>Lines on the downloads page. Capability "downloads"; null on older hosts.</summary>
    IPluginDownloads? Downloads => null;

    /// <summary>Read-only network state. Capability "network.state"; null on older hosts.</summary>
    IPluginNetwork? Network => null;

    /// <summary>
    /// Runs the command line of a program below Program Files that the user confirmed for this plugin: no shell, no window, a hard
    /// timeout (at most one minute), stdout not read, stderr capped. Capability "process". Refused (see <see cref="PluginCliResult.Refused"/>)
    /// for an exe that is not confirmed, not below Program Files, or on a locked couch for a plugin that is not CouchHUD's own.
    /// </summary>
    Task<PluginCliResult> RunTrustedCliAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(new PluginCliResult(null, "", false, "unsupported"));

    /// <summary>Takes an entry out of the guide menu again (the id of <see cref="RegisterQuickAction"/>). Capability "quickActions".</summary>
    void RemoveQuickAction(string id) { }

    /// <summary>
    /// First-party only, capability "browser.signin": a web sign-in in the couch browser whose redirect the host catches without loading
    /// it (see <see cref="IPluginSignIn"/>). Null on hosts without it.
    /// </summary>
    IPluginSignIn? SignIn => null;

    /// <summary>
    /// First-party only, capability "setup.run": hands a downloaded setup to Windows with the admin prompt (runas), so Windows always asks. The host
    /// needs the unlocked couch, a press of the plugin's own row or entry that asks for the parents' code (one use, short-lived), a file in this plugin's own
    /// data folder and hashes it again against <paramref name="sha256"/>.
    /// </summary>
    Task<bool> RunSetupAsync(string path, string sha256, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>First-party only, capability "lock.restrict": windows of this program (canonical, below Program Files) are minimized on the locked couch.</summary>
    bool RestrictWhileLocked(string exePath) => false;

    /// <summary>
    /// First-party only, capability "startup.args": when CouchHUD runs the autostart entries as the shell, the arguments of the entry
    /// that starts this program (canonical, below Program Files) go through <paramref name="adapt"/> (null keeps them).
    /// </summary>
    bool AdaptStartupArgs(string exePath, Func<string, string?> adapt) => false;

    // ---- functions (additive; no capability and no contract change: an older host ignores them and the old flags apply) ----

    /// <summary>
    /// Declares the plugin's functions (<see cref="PluginFunction"/>), at most twelve; declaring an id again replaces it. Pages, rows, guide
    /// entries, tabs and tools name one with their <c>Function</c> property. Older hosts do nothing (the default implementation).
    /// </summary>
    void RegisterFunctions(params PluginFunction[] functions) { }

    // ---- SDK v5 ----

    /// <summary>
    /// A client for one network service the plugin declared in plugin.json ("services", see <see cref="PluginNetServices"/>). Capability
    /// "network:internet". The host lets a request through only to a declared host (each redirect checked again, credentials never sent on
    /// to another host), only to public addresses, and only while Offline is off, the service's switch in Settings › Privacy is on and the
    /// plugin is not paused; otherwise the request fails with an <see cref="HttpRequestException"/>. Null for a service that was not
    /// declared, and on older hosts. Dispose nothing: the host owns the client.
    /// </summary>
    HttpClient? Http(string serviceId) => null;

    /// <summary>The weather in the header (<see cref="IPluginWeather"/>). Capability "weather". Older hosts do nothing.</summary>
    void RegisterWeather(IPluginWeather weather) { }

    /// <summary>The source of the News tab (<see cref="IPluginNews"/>). Capability "news". Older hosts do nothing.</summary>
    void RegisterNews(IPluginNews news) { }

    /// <summary>A cloud provider for the assistant (<see cref="IPluginChatProvider"/>). Capability "assistant.provider", CouchHUD's own plugins only.</summary>
    void RegisterChatProvider(IPluginChatProvider provider) { }

    // ---- SDK 5, additive (older hosts answer with the defaults: false, nothing, null) ----

    /// <summary>
    /// May the declared service <paramref name="serviceId"/> go out now? True only when Offline mode is off, the service's switch in
    /// Settings › Privacy is on and the plugin is not paused. Fail closed: false for a service that was not declared (or not approved), for a
    /// plugin without a network capability ("network:internet" or "network:lan") and when the host cannot tell. For a plugin that opens its
    /// own sockets in the home network (capability "network:lan", service host <see cref="PluginNetServices.LanHost"/>) this is the question
    /// whether it may talk to the user's device at all. This is a declaration plus a privacy switch, not a technical fence: CouchHUD cannot
    /// fence those sockets, so the plugin must ask before it connects and stop when <see cref="ServiceAllowedChanged"/> says false. Needs no capability.
    /// </summary>
    bool ServiceAllowed(string serviceId) => false;

    /// <summary>
    /// The answer of <see cref="ServiceAllowed"/> changed for one of the plugin's services (Offline mode, a switch in Settings › Privacy).
    /// The value is only a hint (whether any declared service is allowed now); for the actual state ask <see cref="ServiceAllowed"/> for each service. Called on a pool thread, also
    /// while the plugin is paused (that is the moment it has to stop).
    /// </summary>
    event Action<bool>? ServiceAllowedChanged
    {
        add { }
        remove { }
    }

    /// <summary>CouchHUD's own state, read only. Capability "host.state"; null without it and on older hosts.</summary>
    IPluginHostState? Host => null;

    // ---- sensor board (additive; see PluginSensorReading) ----

    /// <summary>
    /// First-party only, capability "sensors.publish": replaces this plugin's values on the sensor board (null or empty takes them off).
    /// The host drops values out of range, strips the names, keeps at most <see cref="PluginSensorSnapshot.MaxReadings"/> and names the
    /// source itself. Publish again at least every few seconds: values older than <see cref="PluginSensorSnapshot.MaxAge"/> are not handed out.
    /// Older hosts do nothing (the default implementation).
    /// </summary>
    void PublishSensors(IReadOnlyList<PluginSensorReading>? readings) { }

    /// <summary>
    /// Capability "sensors.read": the newest values on the sensor board, or null when no plugin published any lately (no CaseHUD, CaseHUD
    /// without sensors, its plugin off or paused). Read only. Older hosts answer null (the default implementation).
    /// </summary>
    PluginSensorSnapshot? ReadSensors() => null;
    /// <summary>
    /// Registers the protocol of one kind of TV (<see cref="ITvProvider"/>). Capability "tv.control" and "sdk": 5 in plugin.json. The core
    /// refuses an id another plugin owns or that is reserved for one of CouchHUD's own TV plugins, and more than two providers per plugin.
    /// Older hosts do nothing (the default implementation).
    /// </summary>
    void RegisterTvProvider(ITvProvider provider) { }
}

/// <summary>
/// The SDK version this build of CouchHud implements. Plugins can declare the version
/// they need in plugin.json ("sdk": 1); newer ones are not loaded.
/// Version 2 adds <see cref="IPluginContext.Locked"/> and <see cref="IPluginEvents.LockChanged"/>; plugins for version 1 keep working unchanged.
/// Version 3 adds capabilities, richer pages (sections, images, steppers, choices, links), pushed state and the host services.
/// Version 4 adds page actions (<see cref="PluginPage.Actions"/>): button hints in the footer.
/// Version 5 adds declared network services (<see cref="IPluginContext.Http"/>) and the slots for the weather, the news and the assistant's providers, and
/// (additively, without a new number) <see cref="IPluginContext.ServiceAllowed"/>, the read-only <see cref="IPluginContext.Host"/>, <see cref="IPluginEvents.HostStopping"/> and <see cref="IPluginSettings.SettingChanged"/>.
/// TV control (<see cref="IPluginContext.RegisterTvProvider"/>, capability "tv.control") is part of version 5 as well.
/// </summary>
public static class SdkVersion
{
    public const int Current = 5;
}
