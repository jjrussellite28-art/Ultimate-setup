using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Remote;

/// <summary>Why the phone may not switch the PC off right now (None: it may).</summary>
public enum RemotePowerRefusal
{
    None,
    /// <summary>Not one of <see cref="RemotePower.Actions"/>.</summary>
    UnknownAction,
    /// <summary>The owner switched power from the phone off (Settings › Phone).</summary>
    Disabled,
    /// <summary>A protected profile waits at the picker and the couch would not take the call either.</summary>
    ProfileHeld,
    /// <summary>The couch lock would ask for the code; the phone cannot give one.</summary>
    Locked,
    /// <summary>The area the call belongs to is switched off.</summary>
    AreaOff,
    /// <summary>CouchHUD is in PC mode: someone works at the desktop, switching off is done there.</summary>
    PcMode
}

/// <summary>What the couch would let a guest do right now, as the bridge sees it for the power call, plus the phone's own rules.</summary>
/// <param name="Locked">The couch lock is closed.</param>
/// <param name="ProfileHeld">A protected profile waits for its code at the picker.</param>
/// <param name="AreaAllowsPower">The switchable areas let the power call through (it belongs to none today).</param>
/// <param name="PcMode">CouchHUD is in PC mode (the desktop is in use).</param>
/// <param name="Enabled">The setting "the phone may switch the PC off" is on.</param>
public readonly record struct RemotePowerContext(bool Locked, bool ProfileHeld, bool AreaAllowsPower, bool PcMode = false, bool Enabled = true);

/// <summary>What the host did with a confirmed action: refused it (a gate), failed (Windows said no; the detail is in the log) or ran it.</summary>
public readonly record struct RemotePowerResult(RemotePowerRefusal Refusal, bool Failed)
{
    public static readonly RemotePowerResult Ran = new(RemotePowerRefusal.None, false);
    public bool Succeeded => Refusal == RemotePowerRefusal.None && !Failed;
}

/// <summary>The HUD side of power from the phone (Hud implements it; calls arrive on Kestrel threads).</summary>
public interface IRemotePowerTarget
{
    /// <summary>The setting "the phone may switch the PC off" (the page hides the section without it).</summary>
    bool PowerEnabled { get; }

    /// <summary>First step: may this action run now (<see cref="RemotePower.Decide(string?, RemotePowerContext)"/>), and for the warning whether a game runs and, only if this phone may know it, its name (the library's visibility rules for the phone's level).</summary>
    Task<(RemotePowerRefusal Refusal, string? Game, bool GameRunning)> PowerCheckAsync(string action, RemoteDevice device);

    /// <summary>Confirmed: checks the gates again and runs the couch's own power call.</summary>
    Task<RemotePowerResult> PowerAsync(string action, RemoteDevice device);
}

/// <summary>
/// Power from the paired phone: shut down, restart, standby. No operation of its own: the phone ends in the same host call as the
/// couch's power entries (the bridge's <c>power</c>, <see cref="BridgeMethod"/>) and gets exactly their gates, decided here by the
/// same lists the bridge uses. Where the couch would ask for a code, the phone is refused (it cannot type a code over plain HTTP).
/// On top: the owner's setting, and never while CouchHUD is in PC mode (someone works at the desktop).
/// </summary>
public static class RemotePower
{
    /// <summary>The only actions the phone may ask for, exactly as the host's power call spells them.</summary>
    public static readonly IReadOnlyList<string> Actions = ["shutdown", "restart", "sleep"];

    /// <summary>The bridge call the couch's entries use; its place in the lock lists decides for the phone too.</summary>
    public const string BridgeMethod = "power";

    /// <summary>
    /// The couch's power entries (ui/core/power.js) that ask for the couch lock before they run. None of the phone's actions is one of
    /// them today (a contract test pins this list against power.js); should one become one, the phone is refused on a locked couch.
    /// </summary>
    public static readonly IReadOnlySet<string> EntriesWithUnlock = new HashSet<string>(StringComparer.Ordinal) { "pc-mode", "restart-app", "restart-pc", "quit" };

    /// <summary>Longest phone name in the log and on the TV.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Exact, case sensitive: anything else (other spellings, "restart-pc", "logoff", padding) is no action.</summary>
    public static bool IsAction(string? action) => action is not null && Actions.Contains(action, StringComparer.Ordinal);

    /// <summary>The couch's power entry an action stands for (its id in power.js).</summary>
    public static string EntryId(string action) => action == "sleep" ? "standby" : action;

    /// <summary>The same decision the bridge makes for the couch's power call, in the same order (profile picker, couch lock, areas), plus the phone's own rules.</summary>
    public static RemotePowerRefusal Decide(string? action, RemotePowerContext context) =>
        Decide(action, context, LockPolicy.OpenWhileLocked, ProfilePolicy.OpenWhileHeld, EntriesWithUnlock);

    /// <summary>The decision with the lists given (tests reach every branch with lists the couch does not have today).</summary>
    internal static RemotePowerRefusal Decide(string? action, RemotePowerContext context, IReadOnlySet<string> openWhileLocked, IReadOnlySet<string> openWhileHeld, IReadOnlySet<string> entriesWithUnlock)
    {
        if (!IsAction(action)) return RemotePowerRefusal.UnknownAction;
        if (!context.Enabled) return RemotePowerRefusal.Disabled;
        if (context.ProfileHeld && !openWhileHeld.Contains(BridgeMethod)) return RemotePowerRefusal.ProfileHeld;
        if (context.Locked && !openWhileLocked.Contains(BridgeMethod)) return RemotePowerRefusal.Locked;
        if (!context.AreaAllowsPower) return RemotePowerRefusal.AreaOff;
        if (context.PcMode) return RemotePowerRefusal.PcMode;
        if (context.Locked && entriesWithUnlock.Contains(EntryId(action!))) return RemotePowerRefusal.Locked;
        return RemotePowerRefusal.None;
    }

    /// <summary>The host text for a refusal (Strings key).</summary>
    public static string RefusalKey(RemotePowerRefusal refusal) => refusal switch
    {
        RemotePowerRefusal.Disabled => "remote.power.disabled",
        RemotePowerRefusal.ProfileHeld => "remote.power.profileHeld",
        RemotePowerRefusal.Locked => "remote.power.locked",
        RemotePowerRefusal.AreaOff => "areas.off",
        RemotePowerRefusal.PcMode => "remote.power.pcMode",
        _ => "remote.power.unknown"
    };

    /// <summary>
    /// The phone's name for the log and the TV: no control characters, no format characters (bidi overrides, zero-width joiners: a name
    /// must not turn the line around or hide text), trimmed, at most <see cref="MaxNameLength"/> characters; empty becomes "Handy".
    /// </summary>
    public static string CleanName(string? name)
    {
        var kept = new StringBuilder();
        foreach (var rune in (name ?? "").EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsControl(rune) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (kept.Length + rune.Utf16SequenceLength > MaxNameLength) break;
            kept.Append(rune.ToString());
        }
        var clean = kept.ToString().Trim();
        return clean.Length > 0 ? clean : Strings.Get("remote.defaultDevice");
    }

    /// <summary>
    /// The log line: action, the phone's short id and its cleaned name, the outcome. Never the token, the ticket or anything from the
    /// request beyond the checked action.
    /// </summary>
    public static string LogLine(string? action, RemoteDevice device, string outcome)
    {
        var id = device.Id.Length >= 8 ? device.Id[..8] : device.Id;
        return $"[remote] power {(IsAction(action) ? action : "?")} from phone {id} ({CleanName(device.Name)}): {outcome}";
    }
}

/// <summary>An answer of the power endpoints: the HTTP status and the JSON body (null: status only).</summary>
public sealed record RemotePowerReply(int Status, object? Body)
{
    internal static RemotePowerReply Unauthorized { get; } = new(401, null);
    internal static RemotePowerReply Error(int status, string key, string code) => new(status, new { error = Strings.Get(key), code });
}

/// <summary>
/// The two power endpoints without Kestrel (RemoteHost maps them; tests call them directly). Order in both: origin, address and pairing
/// before the body is even read; then the rate limit (refused and unknown requests count too); then the action list and the gates.
/// </summary>
/// <param name="pairing">The paired phones.</param>
/// <param name="target">The HUD.</param>
/// <param name="log">Where the log lines go.</param>
/// <param name="clock">Now (tests).</param>
public sealed class RemotePowerApi(Pairing pairing, IRemotePowerTarget target, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public RemotePowerTickets Tickets { get; } = new();

    /// <summary>First step: the checks, then a one-time ticket and the running game's name.</summary>
    /// <param name="trusted">Same origin and an address from the home network (RemoteHost checks both like for the other endpoints).</param>
    public async Task<RemotePowerReply> PrepareAsync(bool trusted, string? token, Func<Task<JsonNode?>> readBody)
    {
        if (!trusted || pairing.Find(token, _now()) is not { } device) return RemotePowerReply.Unauthorized;
        return await PrepareForAsync(device, await readBody());
    }

    /// <summary>First step for a phone that is already known (the encrypted channel opened its sealed request): the same order from the count on.</summary>
    public async Task<RemotePowerReply> PrepareForAsync(RemoteDevice device, JsonNode? body)
    {
        var action = Str(body, "action");
        if (!Tickets.Count(device.Id, _now())) return RemotePowerReply.Error(429, "remote.power.tooOften", "tooOften");
        if (!RemotePower.IsAction(action)) return RemotePowerReply.Error(400, "remote.power.unknown", "unknown");
        var (refusal, game, gameRunning) = await target.PowerCheckAsync(action, device);
        if (refusal != RemotePowerRefusal.None)
        {
            log(RemotePower.LogLine(action, device, $"refused ({refusal})"));
            return RemotePowerReply.Error(403, RemotePower.RefusalKey(refusal), refusal.ToString());
        }
        if (Tickets.CoolingDown(_now())) return RemotePowerReply.Error(429, "remote.power.coolingDown", "cooldown");
        return new RemotePowerReply(200, new { ticket = Tickets.Issue(device.Id, action, _now()), game, gameRunning });
    }

    /// <summary>Confirmed: the ticket is spent first, then the gates are checked again and the couch's power call runs.</summary>
    public async Task<RemotePowerReply> RunAsync(bool trusted, string? token, Func<Task<JsonNode?>> readBody)
    {
        if (!trusted || pairing.Find(token, _now()) is not { } device) return RemotePowerReply.Unauthorized;
        return await RunForAsync(device, await readBody());
    }

    /// <summary>Confirmed, for a phone that is already known (the encrypted channel): the same order from the ticket on.</summary>
    public async Task<RemotePowerReply> RunForAsync(RemoteDevice device, JsonNode? body)
    {
        var action = Str(body, "action");
        switch (Tickets.Redeem(device.Id, action, Str(body, "ticket"), _now(), out var pause))
        {
            case TicketCheck.TooOften:
                return RemotePowerReply.Error(429, "remote.power.tooOften", "tooOften");
            case TicketCheck.CoolingDown:
                log(RemotePower.LogLine(action, device, "refused (cooldown)"));
                return RemotePowerReply.Error(429, "remote.power.coolingDown", "cooldown");
            case TicketCheck.Invalid:
                log(RemotePower.LogLine(action, device, "refused (ticket)"));
                return RemotePowerReply.Error(409, "remote.power.expired", "ticket");
        }
        // Tickets exist only for the three actions; checked again all the same.
        if (!RemotePower.IsAction(action))
        {
            Tickets.Failed(pause);
            return RemotePowerReply.Error(400, "remote.power.unknown", "unknown");
        }
        var result = await target.PowerAsync(action, device);
        if (result.Succeeded) return new RemotePowerReply(200, new { ok = true });
        // Nothing ran: the pause this confirmation started does not apply (a later one of another phone stays).
        Tickets.Failed(pause);
        return result.Refusal != RemotePowerRefusal.None
            ? RemotePowerReply.Error(403, RemotePower.RefusalKey(result.Refusal), result.Refusal.ToString())
            : RemotePowerReply.Error(500, "remote.power.failed", "failed");
    }

    private static string Str(JsonNode? body, string name)
    {
        try
        {
            return body is JsonObject o && o[name]?.GetValueKind() == JsonValueKind.String ? o[name]!.GetValue<string>() : "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }
}

/// <summary>How a confirmation fared with its ticket.</summary>
public enum TicketCheck
{
    Valid,
    /// <summary>Unknown, spent, of another phone or action, too young or too old.</summary>
    Invalid,
    /// <summary>Too many requests from this phone (the ticket is spent all the same).</summary>
    TooOften,
    /// <summary>An action just ran (the ticket is spent all the same).</summary>
    CoolingDown
}

/// <summary>
/// The second step of a power action from the phone: the first request (after the checks) gets a one-time ticket, the confirmation must
/// bring it back, for the same phone and the same action, within <see cref="Lifetime"/> and not faster than <see cref="MinConfirmDelay"/>
/// (a double tap never confirms). A ticket counts once, also when the try fails: a recorded request cannot be sent again. Per phone at
/// most <see cref="MaxAttempts"/> requests a minute (both steps, refused ones too); after an action ran, none for <see cref="Cooldown"/>,
/// from any phone (a confirmation holds that pause while the action runs, <see cref="Failed"/> lifts it when nothing ran, and only
/// that pause: a slow action that fails late never lifts the pause of another phone's action that ran meanwhile).
/// </summary>
public sealed class RemotePowerTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinConfirmDelay = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int MaxAttempts = 10;
    private const int MaxOpen = 32;
    private const int MaxDevices = 256;

    private sealed record Ticket(string Value, string Device, string Action, DateTimeOffset Issued);

    private readonly object _lock = new();
    private readonly List<Ticket> _open = [];
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private DateTimeOffset _quietUntil;
    private long _pause;

    /// <summary>Counts a request of this phone; false when it made <see cref="MaxAttempts"/> in the last minute.</summary>
    public bool Count(string device, DateTimeOffset now)
    {
        lock (_lock) return CountLocked(device, now);
    }

    /// <summary>An action ran a moment ago (or is running): no new question yet.</summary>
    public bool CoolingDown(DateTimeOffset now)
    {
        lock (_lock) return now < _quietUntil;
    }

    /// <summary>A fresh ticket for this phone and action; it replaces the phone's open one. Not counted (the caller counted the request).</summary>
    public string Issue(string device, string action, DateTimeOffset now)
    {
        lock (_lock)
        {
            _open.RemoveAll(t => now - t.Issued > Lifetime || t.Device == device);
            if (_open.Count >= MaxOpen) _open.RemoveAt(0);
            var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _open.Add(new Ticket(value, device, action, now));
            return value;
        }
    }

    /// <summary>
    /// Valid exactly once for a ticket of this phone and action that is neither too young nor too old. The ticket is spent before
    /// anything else is looked at, so no answer leaves it usable. A valid one starts the pause after an action.
    /// </summary>
    public TicketCheck Redeem(string device, string action, string? ticket, DateTimeOffset now) => Redeem(device, action, ticket, now, out _);

    /// <summary>As above; <paramref name="pause"/> names the pause a valid confirmation started (for <see cref="Failed"/>), 0 otherwise.</summary>
    public TicketCheck Redeem(string device, string action, string? ticket, DateTimeOffset now, out long pause)
    {
        pause = 0;
        lock (_lock)
        {
            Ticket? found = null;
            if (!string.IsNullOrEmpty(ticket) && ticket.Length == 32)
            {
                var given = Encoding.ASCII.GetBytes(ticket);
                found = _open.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t.Value), given));
                if (found is not null) _open.Remove(found);
            }
            if (!CountLocked(device, now)) return TicketCheck.TooOften;
            if (found is null || found.Device != device || found.Action != action) return TicketCheck.Invalid;
            var age = now - found.Issued;
            if (age < MinConfirmDelay || age > Lifetime) return TicketCheck.Invalid;
            if (now < _quietUntil) return TicketCheck.CoolingDown;
            _quietUntil = now + Cooldown;
            pause = ++_pause;
            return TicketCheck.Valid;
        }
    }

    /// <summary>The confirmed action did not run (refused or failed): the pause it started is lifted, if it is still the current one.</summary>
    public void Failed(long pause)
    {
        lock (_lock)
        {
            if (pause != 0 && pause == _pause) _quietUntil = default;
        }
    }

    private bool CountLocked(string device, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(device, out var times))
        {
            if (_attempts.Count >= MaxDevices)
            {
                foreach (var stale in _attempts.Where(a => a.Value.All(t => now - t > Window)).Select(a => a.Key).ToList()) _attempts.Remove(stale);
                if (_attempts.Count >= MaxDevices) return false;
            }
            _attempts[device] = times = [];
        }
        times.RemoveAll(t => now - t > Window);
        if (times.Count >= MaxAttempts) return false;
        times.Add(now);
        return true;
    }
}
