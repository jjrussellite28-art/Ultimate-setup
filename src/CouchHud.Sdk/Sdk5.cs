using System.Text.RegularExpressions;

namespace CouchHud.Sdk;

// SDK 5: slots for what used to be built into the core (weather in the header, the News tab, the assistant's cloud providers) and a
// host-made HTTP client for the network services a plugin declares in plugin.json. A plugin that uses them declares "sdk": 5, so an
// older CouchHUD does not load it. Plugins for SDK 1 to 4 run unchanged.
//
// The core keeps the places where these things are shown (the header, the News tab, the assistant, the settings) and draws them itself
// from the plugin's data, as plain text; a plugin never ships HTML or script into the HUD.

/// <summary>
/// A network service a plugin declares in plugin.json and reaches through <see cref="IPluginContext.Http"/>:
/// <code>"services": [ { "id": "forecast", "name": { "de": "Wetterdienst", "en": "Weather service" }, "hosts": [ "api.open-meteo.com" ] } ]</code>
/// Settings › Privacy shows every declared service of a loaded plugin with its own switch (on by default); Offline stops all of them.
/// The host refuses any other host, and "*" (any public host, e.g. news feeds the user adds) is shown as such. At most
/// <see cref="MaxPerPlugin"/> services and <see cref="MaxHosts"/> hosts each; ids like a function id (lower case, digits, "-" and "_").
/// </summary>
public static class PluginNetServices
{
    public const int MinSdk = 5;
    public const int MaxPerPlugin = 4;
    public const int MaxHosts = 16;

    /// <summary>The host entry that means any public host.</summary>
    public const string AnyHost = "*";

    /// <summary>
    /// The host entry that means a target in the home network that the user types in (a hub, a TV, a PC): not a name CouchHUD can check. It
    /// grants no reach through <see cref="IPluginContext.Http"/> (which never goes to the home network); it only makes the service one that
    /// <see cref="IPluginContext.ServiceAllowed"/> answers for, with its own switch in Settings › Privacy, for plugins that open their own
    /// sockets (capability "network:lan"). "*" does not include it: a plugin that declares both gets two separate approvals.
    /// </summary>
    public const string LanHost = "lan";
}

// ---- weather (capability "weather") ----

/// <summary>A place for the weather.</summary>
/// <param name="Name">The place's name.</param>
/// <param name="Region">Region and country, as one line ("Bavaria, Germany"); may be empty.</param>
public sealed record PluginPlace(string Name, string Region, double Latitude, double Longitude);

/// <summary>One day of the forecast.</summary>
/// <param name="Date">yyyy-MM-dd.</param>
/// <param name="Code">The WMO weather code (0 clear … 99 thunderstorm with hail); the host draws the symbol for it.</param>
public sealed record PluginWeatherDay(string Date, double Min, double Max, int Code);

/// <summary>The weather now and the next days, temperatures in degrees Celsius.</summary>
public sealed record PluginWeatherReport(string Place, double Temperature, int Code, bool IsDay, IReadOnlyList<PluginWeatherDay> Days);

/// <summary>
/// The weather source behind the header (capability "weather"). The plugin keeps the chosen place in its own settings; the core's settings
/// page searches through <see cref="SearchAsync"/> and sets it with <see cref="SetPlace"/>. The host asks <see cref="GetAsync"/> about every
/// quarter of an hour (with a timeout); the plugin caches as it likes. Only one source is used: CouchHUD's own wins, else the first loaded.
/// </summary>
public interface IPluginWeather
{
    /// <summary>The chosen place; null hides the weather.</summary>
    PluginPlace? Place { get; }

    /// <summary>Keeps the place (null: no weather). Called after the user picked one in the core's settings (a change, behind the usual gates).</summary>
    void SetPlace(PluginPlace? place);

    /// <summary>Places that match what the user typed, best first (the host shows at most eight).</summary>
    Task<IReadOnlyList<PluginPlace>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>The weather at <see cref="Place"/>; null when there is none (no place, no answer).</summary>
    Task<PluginWeatherReport?> GetAsync(CancellationToken cancellationToken);
}

// ---- news (capability "news") ----

/// <summary>A feed the user added.</summary>
public sealed record PluginNewsFeed(string Name, string Url);

/// <summary>One article of the News tab. All text is plain text (the host never renders markup).</summary>
/// <param name="Id">Stable for the article (a hash of its link, say), at most 128 characters.</param>
/// <param name="Feed">The feed's name.</param>
/// <param name="Image">An https address of a picture, or null.</param>
/// <param name="Link">The article's http(s) address; opened in the couch browser.</param>
/// <param name="Published">Unix time in milliseconds; 0 when unknown.</param>
/// <param name="Paragraphs">The article's text as far as the feed carries it.</param>
public sealed record PluginNewsItem(string Id, string Feed, string Title, string Summary, string Link, string? Image, long Published, IReadOnlyList<string> Paragraphs);

/// <summary>
/// The source behind the News tab (capability "news"). The core shows the tab only while a source is loaded and its area is on, and runs
/// the feed list on its settings page through <see cref="AddFeed"/> and <see cref="RemoveFeed"/>. Only one source is used: CouchHUD's own
/// wins, else the first loaded.
/// </summary>
public interface IPluginNews
{
    IReadOnlyList<PluginNewsFeed> Feeds { get; }

    /// <summary>Adds a feed (an http(s) address the host checked). Throw <see cref="ArgumentException"/> with a short text to refuse it.</summary>
    void AddFeed(string name, string url);

    void RemoveFeed(string url);

    /// <summary>The newest articles of all feeds, newest first (the host shows at most 100).</summary>
    Task<IReadOnlyList<PluginNewsItem>> GetItemsAsync(CancellationToken cancellationToken);
}

// ---- assistant providers (capability "assistant.provider", CouchHUD's own plugins only) ----

/// <summary>A tool call the model asked for. <paramref name="ArgumentsJson"/> is a JSON object.</summary>
public sealed record PluginToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>What a tool returned, for the model.</summary>
public sealed record PluginToolResult(string CallId, string Content, bool IsError);

/// <summary>One message of the conversation: "user" (text or tool results) or "assistant" (text and/or tool calls).</summary>
public sealed record PluginChatMessage(string Role, string? Text, IReadOnlyList<PluginToolCall>? Calls = null, IReadOnlyList<PluginToolResult>? Results = null);

/// <summary>A tool the model may call. <paramref name="InputSchemaJson"/> is a JSON schema object.</summary>
public sealed record PluginChatTool(string Name, string Description, string InputSchemaJson);

/// <summary>The model's answer: text and/or tool calls.</summary>
public sealed record PluginChatReply(string? Text, IReadOnlyList<PluginToolCall> Calls);

/// <summary>Why a provider call failed, as the assistant tells the user.</summary>
public enum PluginChatError
{
    /// <summary>No key, or the provider refused it (401/403).</summary>
    KeyInvalid,
    /// <summary>Too many requests (429).</summary>
    RateLimited,
    /// <summary>The model does not exist for this key (404).</summary>
    ModelNotFound,
    /// <summary>The answer was cut off at the length limit.</summary>
    TooLong,
    /// <summary>Anything else (the message is shown, cut and cleaned).</summary>
    Other
}

/// <summary>Thrown by <see cref="IPluginChatProvider.CompleteAsync"/> for a failure the user should understand.</summary>
public sealed class PluginChatException(PluginChatError error, string? message = null) : Exception(message ?? error.ToString())
{
    public PluginChatError Error { get; } = error;
}

/// <summary>
/// A cloud provider for CouchHUD's assistant (capability "assistant.provider", CouchHUD's own plugins only: the provider sees the whole
/// conversation, tool results included). The assistant itself, its tools and the MCP server stay in the core; the provider only turns a
/// conversation into the next answer. The API key lives in the plugin's own secret store (<see cref="IPluginSettings.SetSecret"/>): the
/// core's settings page hands a new key over with <see cref="SetKey"/> and never reads it back.
/// </summary>
public interface IPluginChatProvider
{
    /// <summary>Lower case, e.g. "anthropic"; what the settings store as the chosen provider.</summary>
    string Id { get; }

    /// <summary>Shown in the settings, e.g. "Anthropic (Claude)".</summary>
    string Name { get; }

    /// <summary>The models to choose from, the default first.</summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>A key is stored.</summary>
    bool HasKey { get; }

    /// <summary>Stores a new key (empty or null removes it).</summary>
    void SetKey(string? key);

    /// <summary>The next answer. <paramref name="model"/> is one of <see cref="Models"/> or a name the user typed.</summary>
    Task<PluginChatReply> CompleteAsync(string model, string system, IReadOnlyList<PluginChatMessage> history, IReadOnlyList<PluginChatTool> tools, CancellationToken cancellationToken);
}

// SDK 5: TV control as a plugin ("tv.control"). The core keeps everything around the TV: the settings page, the pairing dialog, the
// triggers (TV on with CouchHUD, off with the PC), Wake-on-LAN, discovery, the address policy (home network only, only the paired TV)
// and the key storage (per TV, DPAPI). A plugin brings only the protocol of one kind of TV (an ITvProvider). It never opens a socket
// itself: every byte goes through the ITvLink the core hands it, and that link only reaches the one TV being paired or paired before,
// only on the ports the provider declared, with TLS pinned to the certificate seen at pairing, no proxy, no redirects and hard limits.
// A plugin that uses these types declares "sdk": 5 and the capability "tv.control"; it needs no network capability.

/// <summary>Why a TV call failed, as a code the UI translates (no finished sentences from the host or a plugin).</summary>
public enum TvError
{
    /// <summary>No answer on the TV's port (TV off, other address, network).</summary>
    Unreachable,
    /// <summary>The TV did not answer in time.</summary>
    Timeout,
    /// <summary>The TV's certificate is not the one seen at pairing: pair again (or someone else answers on that address).</summary>
    CertChanged,
    /// <summary>Pairing was declined on the TV, or the prompt was not answered.</summary>
    Denied,
    /// <summary>The TV does not know the stored key any more (reset, other TV): pair again.</summary>
    Unpaired,
    /// <summary>The TV refused the command (missing permission, wrong state), or the TV cannot do it at all.</summary>
    Refused,
    /// <summary>An answer that is not what the protocol says (too large, not JSON, wrong shape).</summary>
    BadAnswer,
    /// <summary>The address is not in the local network (public, loopback, multicast …).</summary>
    NotLocal,
    /// <summary>No TV is paired.</summary>
    NotPaired,
    /// <summary>Wake-on-LAN is not possible (no MAC known) or was sent just now.</summary>
    NoWake,
    /// <summary>The TV answers only on an unencrypted port; pairing over it needs an explicit confirmation (expert mode).</summary>
    PlainOnly,
    /// <summary>This address or MAC was paired over TLS before: an unencrypted pairing is refused (a downgrade by someone else in the network).</summary>
    Downgrade,
    /// <summary>A pairing is running already.</summary>
    Busy,
    /// <summary>The core refused a connection the plugin asked for (a port it did not declare, a key on an unencrypted port, too many connections).</summary>
    Blocked,
    /// <summary>The plugin for this kind of TV is not installed, switched off or not loaded.</summary>
    NoProvider
}

/// <summary>A failed TV call. Plugins throw it with the code that fits; anything else a plugin throws counts as <see cref="TvError.BadAnswer"/>.</summary>
public sealed class TvException(TvError code, string? detail = null) : Exception(detail ?? code.ToString())
{
    public TvError Code { get; } = code;
}

/// <summary>One external input of the TV ("HDMI_1", "HDMI 1"). The label comes from the TV: the core cleans it and shows it as text, never as markup.</summary>
public sealed partial record TvInput(string Id, string Label, bool Connected)
{
    /// <summary>At most this many inputs are taken from a list.</summary>
    public const int MaxInputs = 32;

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>An input id the core passes on: letters, digits, "_", ".", ":", "-", at most 64 characters. Anything else is dropped.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);
}

public sealed record TvVolume(int Volume, bool Muted);

/// <summary>
/// One TCP port of the TV that a provider talks to. Only declared ports are reachable through the <see cref="ITvLink"/>.
/// </summary>
/// <param name="Port">1 to 65535.</param>
/// <param name="Tls">TLS on this port. The TV's certificate is self-signed: its public key is pinned at pairing (trust on first use) and required afterwards.</param>
/// <param name="KeyFree">
/// A port without TLS that never carries the pairing key (device info, UPnP). Only plain HTTP requests go there, no web socket; in a session the
/// core refuses a request in which it recognises the key (a best-effort safety net against plugin bugs, not a barrier: URL, body, SOAP action and content type are looked at as is, URL-decoded, as JSON or XML character references, base64 and hex). A plain port that is not key-free counts as an unencrypted pairing.
/// </param>
public sealed record TvPort(int Port, bool Tls, bool KeyFree = false)
{
    /// <summary>At most this many ports per provider.</summary>
    public const int MaxPorts = 8;
}

/// <summary>An HTTP request to the TV (GET or POST, a path below the TV's address). The core adds the host, refuses redirects and limits the answer.</summary>
/// <param name="Port">One of the provider's <see cref="ITvProvider.Ports"/>.</param>
/// <param name="Path">Starts with "/", printable ASCII without spaces, at most 1024 characters (query included).</param>
public sealed record TvHttpRequest(int Port, string Path)
{
    /// <summary>"GET" or "POST".</summary>
    public string Method { get; init; } = "GET";

    /// <summary>The body (POST only), at most 16 KB as UTF-8.</summary>
    public string? Body { get; init; }

    /// <summary>The body's content type, e.g. "text/xml; charset=\"utf-8\"" (printable ASCII, at most 128 characters).</summary>
    public string? ContentType { get; init; }

    /// <summary>The UPnP SOAPACTION header, e.g. "\"urn:schemas-upnp-org:service:RenderingControl:1#GetVolume\"" (printable ASCII, at most 256 characters).</summary>
    public string? SoapAction { get; init; }
}

/// <summary>The TV's answer: the status code and the body as text (at most 64 KB; a longer answer is <see cref="TvError.BadAnswer"/>).</summary>
public sealed record TvHttpResponse(int Status, string Body);

/// <summary>An open web socket to the TV (text messages only). Closed by the core when the session ends.</summary>
public interface ITvChannel : IAsyncDisposable
{
    bool Open { get; }

    /// <summary>Sends one text message (at most 64 KB as UTF-8).</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next text message (at most 256 KB), or null when the TV closed the connection. A binary message is <see cref="TvError.BadAnswer"/>.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

/// <summary>
/// The only way a plugin reaches a TV: bound by the core to the one address being paired (or paired before) and to the provider's declared
/// ports. The core decides about TLS and the certificate, keeps proxies and redirects out and limits sizes, time and the number of
/// connections. Failures are <see cref="TvException"/>s (Unreachable, Timeout, CertChanged, PlainOnly, Blocked …).
/// </summary>
public interface ITvLink
{
    /// <summary>The TV's address (an IP literal of the local network), e.g. for a Host-like value inside a protocol message.</summary>
    string Address { get; }

    /// <summary>The user allowed an unencrypted pairing (expert mode), or the TV was paired that way. Otherwise a plain port that is not key-free is refused (<see cref="TvError.PlainOnly"/>).</summary>
    bool PlainAllowed { get; }

    /// <summary>Opens a web socket on a declared port ("ws" or "wss" as the port says). <paramref name="pathAndQuery"/>: as <see cref="TvHttpRequest.Path"/>.</summary>
    Task<ITvChannel> OpenAsync(int port, string pathAndQuery, CancellationToken ct);

    /// <summary>One HTTP request and its answer.</summary>
    Task<TvHttpResponse> SendAsync(TvHttpRequest request, CancellationToken ct);
}

/// <summary>
/// What pairing produced. <see cref="Key"/> is the secret the TV handed out (a client key or token, printable ASCII, 8 to 256 characters):
/// the core stores it with DPAPI for the Windows user, never logs it and hands it back only to this provider.
/// </summary>
public sealed record TvPairResult(string Key)
{
    /// <summary>The TV's MAC for Wake-on-LAN, if the TV told it ("AA:BB:CC:DD:EE:FF").</summary>
    public string? Mac { get; init; }

    /// <summary>The TV's name, if it told one (cleaned by the core).</summary>
    public string? Name { get; init; }

    /// <summary>The model, if the TV told it (cleaned by the core).</summary>
    public string? Model { get; init; }

    /// <summary>The app in front on the TV while pairing (the PC's input, since pairing happens from CouchHUD on the TV). "TV off with the PC" only switches off while it is in front.</summary>
    public string? PcApp { get; init; }
}

/// <summary>
/// A connection to a paired TV. The core calls one member at a time, with its own timeout around each call, and checks every answer
/// (volume 0 to 100, input ids and labels, app ids).
/// </summary>
public interface ITvSession : IAsyncDisposable
{
    bool Open { get; }
    Task TurnOffAsync(CancellationToken ct);
    Task<TvVolume> GetVolumeAsync(CancellationToken ct);
    Task VolumeStepAsync(bool up, CancellationToken ct);
    Task SetVolumeAsync(int volume, CancellationToken ct);
    Task SetMuteAsync(bool mute, CancellationToken ct);
    Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct);

    /// <summary>Switches to an input of the TV's own list (the core has checked the id against a fresh list).</summary>
    Task SwitchInputAsync(string inputId, CancellationToken ct);

    /// <summary>The app in front on the TV (e.g. "com.webos.app.hdmi2"), or null when the TV does not say.</summary>
    Task<string?> GetForegroundAppAsync(CancellationToken ct);

    /// <summary>The input in front, one of <paramref name="inputs"/> (ids), or null (an app, live TV, unknown).</summary>
    Task<string?> GetActiveInputAsync(IReadOnlyList<TvInput> inputs, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>True: on. False: reachable but in standby (the screen is off). Null: unknown. The default: reachable means on.</summary>
    Task<bool?> GetPowerAsync(CancellationToken ct) => Task.FromResult<bool?>(true);

    /// <summary>Switches on a TV that answers in standby. Without it (the default) the core only has Wake-on-LAN.</summary>
    Task TurnOnAsync(CancellationToken ct) => throw new TvException(TvError.Refused);
}

/// <summary>
/// One kind of TV (one protocol), registered by a plugin with the capability "tv.control" (<see cref="IPluginContext.RegisterTvProvider"/>).
/// </summary>
public interface ITvProvider
{
    /// <summary>Short id stored with the pairing ("lg-webos"): lower case letters, digits and "-", at most 32 characters.</summary>
    string Id { get; }

    /// <summary>Shown name ("LG webOS"), plain text.</summary>
    string Name { get; }

    /// <summary>The ports the provider talks to (at most <see cref="TvPort.MaxPorts"/>). Read once when the provider is registered.</summary>
    IReadOnlyList<TvPort> Ports { get; }

    /// <summary>
    /// Pairs with the TV behind <paramref name="link"/>: the TV shows a prompt, <paramref name="onPrompt"/> is called when it does, and the
    /// result carries the key. Throws <see cref="TvException"/> (Denied, Timeout, Unreachable …). The core decides from what went over
    /// the link whether the pairing is encrypted and which certificate is pinned.
    /// </summary>
    Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct);

    /// <summary>Opens a session with the stored key (only ever this provider's own). A key the TV does not know any more is <see cref="TvError.Unpaired"/>.</summary>
    Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct);
}

/// <summary>The SDK version a plugin must declare to register a TV provider; below it the host refuses the registration.</summary>
public static class TvSdk
{
    public const int MinSdk = 5;
}
