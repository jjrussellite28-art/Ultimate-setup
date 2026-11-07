using System.Text.Json;
using System.Windows;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Remote;
using CouchHud.Core.Skins;
using CouchHud.Sdk;
using CouchHud.Shell.Services;
using CouchHud.Shell.UiHost;

namespace CouchHud.Shell;

/// <summary>
/// The phone remote's encrypted channel (0.7), HUD side: what a phone may see and do beyond buttons and text. Every library operation ends in
/// the bridge's own dispatch (<see cref="UiBridge.InvokeAsync"/>) with the couch's gates in their order (waiting profile picker, couch lock,
/// switchable areas, then the host method with its parental checks), on the UI thread like the TV's own calls. The phone's level is checked
/// by the channel first (<see cref="RemoteChannel"/>) and here once more on the UI thread, where a profile switch cannot come between.
/// </summary>
public sealed partial class Hud
{
    /// <summary>The DPAPI scope of the phones' channel keys (no plugin id can have a '#').</summary>
    private const string RemoteKeyScope = "#remote-channel";

    /// <summary>The TV's sleep timer as power.js reports it: when it ends (Unix ms) and what it does; null when off.</summary>
    private (long At, string Action)? _sleepTimerState;

    (string Profile, bool Child) IRemoteChannelTarget.Couch
    {
        get
        {
            var c = config.Current;
            return (c.ActiveProfile.Id, ProfileSwitch.IsChild(c, c.ActiveProfile.Id));
        }
    }

    string IRemoteChannelTarget.CouchLock => config.Current.CouchLock;

    bool IRemoteChannelTarget.PcMode => _pcMode;

    /// <summary>Who set the running sleep timer: a child's phone (it may end it again) or anybody else (the TV, a parent's phone: a child's phone may not).</summary>
    private bool _sleepTimerByChildPhone;

    /// <summary>
    /// A phone set a timer: its random token travels to power.js with the start and comes back with the TV's report of that very timer
    /// (SetSleepTimerState); only a report with this token belongs to the phone. A timer started at the TV carries none.
    /// </summary>
    private (bool Child, string Token)? _phoneTimerPending;

    /// <summary>
    /// PC mode is entered or left: what a phone may do changes at once (<see cref="RemoteRights.Effective"/>), the phones get the signal. On
    /// entering, a running sleep timer ends (set at the TV or on a phone): the desktop is in use, nothing shuts it down unasked. power.js
    /// stops the timer on this event, and refuses to start or fire one while PC mode lasts (the second line, should the event ever be late).
    /// </summary>
    private void PcModeChangedForPhones(bool entered)
    {
        Emit("remote", new { type = "pcMode", on = entered });
        if (entered)
        {
            if (_sleepTimerState is not null || _phoneTimerPending is not null)
            {
                Console.Error.WriteLine("[remote] sleep timer ended: PC mode");
                Emit("toast", Strings.Get("remote.sleepTimer.pcMode"));
            }
            _sleepTimerState = null;
            _phoneTimerPending = null;
            _sleepTimerByChildPhone = false;
            Emit("remote", new { type = "sleepTimer", minutes = 0 });
            _remote.Channel?.Publish("sleepTimer");
        }
        _remote.Channel?.Publish("level");
    }

    void IRemoteChannelTarget.Paired(RemoteDevice device) => Ui(() =>
    {
        // Seen at once on the TV: a pairing nobody at the couch expected does not go unnoticed.
        Emit("toast", Strings.Get("remote.paired", RemotePower.CleanName(device.Name)));
        Emit("remote", new { type = "paired" });
    });

    /// <summary>The TV's events that phones get a signal of; everything else stays with the TV.</summary>
    private void PublishToPhones(string name)
    {
        if (_remote.Channel is not { } channel) return;
        switch (name)
        {
            case "library" or "session" or "downloads" or "collections":
                channel.Publish(name);
                break;
            // Another profile on the couch can change what a phone may do (a child's phone).
            case "profiles" or "areas" or "unlocked":
                channel.Publish("level");
                break;
        }
    }

    /// <summary>power.js reports the TV's sleep timer (at: Unix ms of the end, 0 when off).</summary>
    public void SetSleepTimerState(long at, string? action, string? token = null)
    {
        _sleepTimerState = at > 0 && action is "shutdown" or "standby" ? (at, action) : null;
        if (_sleepTimerState is null)
        {
            _sleepTimerByChildPhone = false;
        }
        else if (_pcMode)
        {
            // No timer runs in PC mode (a late report of one that was running): it ends again.
            _sleepTimerState = null;
            Emit("remote", new { type = "sleepTimer", minutes = 0 });
        }
        else if (_phoneTimerPending is { } pending && token is { Length: > 0 } && string.Equals(token, pending.Token, StringComparison.Ordinal))
        {
            _sleepTimerByChildPhone = pending.Child;
            _phoneTimerPending = null;
        }
        else
        {
            // Set at the TV (or a timer that kept running): a child's phone does not end it.
            _sleepTimerByChildPhone = false;
        }
        // Plugins with "host.state" read the end of the timer (IPluginHostState.SleepTimerEnds): the page reports every start, change and end here.
        plugins.SleepTimerEnds = _sleepTimerState is { } running ? DateTimeOffset.FromUnixTimeMilliseconds(running.At) : null;
        _remote.Channel?.Publish("sleepTimer");
    }

    /// <summary>The parents set what one phone may do (Settings › Phone): their code, as for every change to who may do what.</summary>
    public object SetRemoteDeviceLevel(string id, string level)
    {
        if (!RemoteRights.IsName(level)) throw new ArgumentException(Strings.Get("remote.badRequest"));
        if (Pairing.ById(id) is null) throw new ArgumentException(Strings.Get("remote.noSuchDevice"));
        // Raising what a phone may do takes the second factor (after the code); lowering does not.
        if (SecondFactorRules.Raises(Pairing.ById(id)?.Level, level))
            RequireSecondFactorFor(SecondFactorActions.RemoteLevel, CodeAcceptedFor("remoteLevel", id), Pairing.ById(id)?.Name, id, level);
        // Lowering the last phone that can confirm ends the second factor: switching it off, so it needs it (no downgrade on the side).
        if (level != "full") RequireSecondFactorUnlessStays(d => d.Id == id, "lower", id, level);
        var ticket = CheckParentGrant("remoteLevel", id, Strings.Get("remote.level.reason"));
        UseUp(ticket);
        if (!Pairing.SetLevel(id, level)) throw new ArgumentException(Strings.Get("remote.noSuchDevice"));
        ResetSecondFactor();
        ReconcileSecondFactor();
        Console.Error.WriteLine($"[remote] level of {id} set to {level}");
        _remote.Channel?.Publish("level", "{}", id);
        return GetRemote();
    }

    async Task<RemoteCallResult> IRemoteChannelTarget.CallAsync(RemoteDevice device, RemoteLevel level, string op, JsonElement args)
    {
        var dispatcher = Application.Current.Dispatcher;
        switch (op)
        {
            case "state":
                return RemoteCallResult.Ok(await dispatcher.InvokeAsync(() => PhoneState(device)));
            case "button":
            {
                var button = PhoneString(args, "button");
                if (button is null || !RemoteHost.Buttons.Contains(button)) return Bad();
                ((IRemoteTarget)this).Button(button);
                return RemoteCallResult.Ok(null);
            }
            case "command":
            {
                // Mega Hub: the Android companion's named commands, each one a button of the phone remote (RemoteCommands).
                var name = PhoneString(args, "command");
                if (name == RemoteCommands.Ping) return RemoteCallResult.Ok(new { pong = true, t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                if (RemoteCommands.ButtonFor(name) is not { } mapped) return Bad();
                ((IRemoteTarget)this).Button(mapped);
                return RemoteCallResult.Ok(null);
            }
            case "text":
            {
                var text = PhoneString(args, "text");
                if (text is null || text.Length > 4000) return Bad();
                // The same rule as the plain endpoint: a password field on the TV is typed at the TV.
                if (_keyboard.Secret) return RemoteCallResult.Fail(Strings.Get("remote.passwordOnTv"), "secret");
                var submit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("submit", out var s) && s.ValueKind == JsonValueKind.True;
                var refused = await ((IRemoteTarget)this).TextAsync(text, submit);
                return refused is null ? RemoteCallResult.Ok(null) : RemoteCallResult.Fail(refused, "refused");
            }
            case "sleepTimer":
                return await dispatcher.InvokeAsync(() => SleepTimerFromPhone(device, args));
            case "secondFactor" or "secondFactorAnswer":
                return await dispatcher.InvokeAsync(() => SecondFactorFromPhone(device, op, args));
        }
        if (RemoteCalls.Map(op, args) is not { } call) return Bad();
        var confirm = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;
        return await dispatcher.InvokeAsync(() => PhoneBridgeAsync(device, op, call, confirm)).Task.Unwrap();
    }

    /// <summary>The ids of the entries this phone's library shows right now (the bridge's own library call, then the phone's hidden rule).</summary>
    private static async Task<IReadOnlySet<string>> VisibleToPhoneAsync(UiBridge bridge, RemoteLevel level)
    {
        var items = (IEnumerable<LibraryItem>)(await bridge.InvokeAsync(RemoteRights.Ops["library"].BridgeMethod!, JsonSerializer.SerializeToElement(Array.Empty<object>())))!;
        return RemoteCalls.VisibleIds(items, level);
    }

    private static RemoteCallResult Bad() => RemoteCallResult.Fail(Strings.Get("remote.badRequest"), "bad");

    private static string? PhoneString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>What the phone shows on top: its level, the TV's keyboard, the game, the sleep timer, whether power and the code work from it.</summary>
    private object PhoneState(RemoteDevice device)
    {
        var c = config.Current;
        var k = _keyboard;
        var level = RemoteRights.Effective(device, c.ActiveProfile.Id, _pcMode);
        return new
        {
            level = RemoteRights.Name(level),
            // The level is lowered in PC mode (no couch lock, no parental controls there): the page says why.
            pcMode = _pcMode,
            name = device.Name,
            keyboard = new { open = k.Open, title = k.Title, secret = k.Secret, text = k.Secret ? "" : k.Text },
            power = c.RemotePower,
            game = GameForPhone(level),
            sleepTimer = _sleepTimerState is { } t ? new { at = t.At, action = t.Action } : null,
            parentOnPhone = RemoteRights.MayEnterParentCode(device, sealedChannel: true, c.CouchLock),
            locked = Locked,
            // The artwork picker waits for the online search (RemoteRights.ArtworkHook): the page keeps its place hidden.
            artwork = false,
            minutes = RemoteCalls.SleepTimerMinutes
        };
    }

    /// <summary>
    /// The running game's name for a phone: only from the library level on and only if the game would be in that phone's library right now
    /// (its area on, not hidden unless the phone may show hidden entries); otherwise nothing, so a control-only phone learns no titles.
    /// </summary>
    private string? GameForPhone(RemoteLevel level)
    {
        if (level < RemoteLevel.Library || _sessions.Current?.Item is not { } item) return null;
        if (library.Find(item.Id) is not { } known || !Areas.Shows(known)) return null;
        return known.Hidden && level < RemoteLevel.Full ? null : known.Name;
    }

    /// <summary>
    /// The sleep timer from the phone: the timer itself lives on the TV (power.js); the host checks the same gates as for the power action
    /// it ends in (RemotePower.Decide, including the owner's setting and PC mode) before it tells the TV to start it. Off needs nothing.
    /// </summary>
    private RemoteCallResult SleepTimerFromPhone(RemoteDevice device, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("minutes", out var m) || !m.TryGetInt32(out var minutes)) return Bad();
        var name = RemotePower.CleanName(device.Name);
        if (minutes == 0)
        {
            // Off is checked like on: the same gates as for the power action the timer ends in (owner's setting, couch lock, waiting picker,
            // areas, PC mode), and a child's phone ends only a timer a child's phone set (never a parent's or the TV's).
            var running = _sleepTimerState?.Action == "standby" ? "sleep" : "shutdown";
            var offRefusal = RemotePower.Decide(running, RemotePowerNow());
            if (offRefusal != RemotePowerRefusal.None) return RemoteCallResult.Fail(Strings.Get(RemotePower.RefusalKey(offRefusal)), offRefusal.ToString());
            if (_sleepTimerState is not null && !RemoteRights.MayEndSleepTimer(device.Child, _sleepTimerByChildPhone))
                return RemoteCallResult.Fail(Strings.Get("remote.sleepTimer.notYours"), "level");
            Emit("remote", new { type = "sleepTimer", minutes = 0 });
            Emit("toast", Strings.Get("remote.sleepTimer.off", name));
            return RemoteCallResult.Ok(null);
        }
        var action = PhoneString(args, "action");
        if (!RemoteCalls.SleepTimerMinutes.Contains(minutes) || action is not ("shutdown" or "standby")) return Bad();
        // A timer of a parent or the TV is not replaced by a child's phone: setting one ends the old one, so the same rule as for ending it.
        if (_sleepTimerState is not null && !RemoteRights.MayEndSleepTimer(device.Child, _sleepTimerByChildPhone))
            return RemoteCallResult.Fail(Strings.Get("remote.sleepTimer.notYours"), "level");
        var refusal = RemotePower.Decide(action == "standby" ? "sleep" : "shutdown", RemotePowerNow());
        if (refusal != RemotePowerRefusal.None) return RemoteCallResult.Fail(Strings.Get(RemotePower.RefusalKey(refusal)), refusal.ToString());
        Console.Error.WriteLine($"[remote] sleep timer {minutes} min ({action}) from {device.Id} \"{name}\"");
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _phoneTimerPending = (device.Child, token);
        Emit("remote", new { type = "sleepTimer", minutes, action, token });
        Emit("toast", Strings.Get("remote.sleepTimer.set", name, minutes));
        return RemoteCallResult.Ok(null);
    }

    /// <summary>One library operation through the bridge's dispatch, on the UI thread; the answer shaped for the phone.</summary>
    private async Task<RemoteCallResult> PhoneBridgeAsync(RemoteDevice device, string op, RemoteCall call, bool confirm)
    {
        if (_window?.Bridge is not { } bridge) return RemoteCallResult.Fail(Strings.Get("remote.power.failed"), "notReady");
        // The level once more, here where no profile switch can come between.
        // In PC mode (no couch lock, no parental controls) a phone is a plain remote control: library operations are refused here, where
        // _pcMode cannot change between the check and the call.
        var activeProfile = config.Current.ActiveProfile.Id;
        var level = RemoteRights.Effective(device, activeProfile, _pcMode);
        if (!RemoteRights.Allows(level, op))
        {
            var code = RemoteRights.DenyCode(device, activeProfile, _pcMode, op);
            return RemoteCallResult.Fail(RemoteRights.DenyText(code), code);
        }
        try
        {
            switch (op)
            {
                case "launch":
                {
                    var id = call.Args[0].GetString()!;
                    if (library.Find(id) is null) return RemoteCallResult.Fail(Strings.Get("remote.notFound"), "notFound");
                    // A game runs: the phone asks first (the couch's switch question is at the TV; the phone repeats with confirm).
                    if (_sessions.Current is { } running && !confirm)
                        return RemoteCallResult.Fail(Strings.Get("remote.launch.running", running.Item.Name), "running");
                    // The parents' code for a locked game: the same check the TV makes before it asks (through the same dispatch).
                    var check = JsonSerializer.SerializeToElement(await bridge.InvokeAsync("parentalCheck", call.Args));
                    if (check.TryGetProperty("needsCode", out var needs) && needs.ValueKind == JsonValueKind.True)
                        return new RemoteCallResult(null, Strings.Get("parental.codeRequired"), "parent") { ParentAction = "launch", ParentItem = id };
                    var started = (LibraryItem)(await bridge.InvokeAsync(call.Method, call.Args))!;
                    Console.Error.WriteLine($"[remote] launch {started.Id} from {device.Id}");
                    return RemoteCallResult.Ok(new { name = started.Name });
                }
                case "favorite":
                {
                    var id = call.Args[0].GetString()!;
                    if (library.Find(id) is not { } item) return RemoteCallResult.Fail(Strings.Get("remote.notFound"), "notFound");
                    if (item.Favorite == call.Favorite) return RemoteCallResult.Ok(new { favorite = item.Favorite });
                    return RemoteCallResult.Ok(new { favorite = (bool)(await bridge.InvokeAsync(call.Method, call.Args))! });
                }
                case "setting":
                {
                    var key = call.Args[0].GetString()!;
                    // The TV page asks the parents' code before it saves a setting that is theirs (the screensaver with protected profiles: the
                    // host then wants the grant "idleSetting" for this key). A phone is asked the same way, for the same action and key, and
                    // answers through the same "parentCode" path; locked or waiting at the picker, the bridge's gates answer first, as on the TV.
                    if (!Locked && !ProfileHeld && HasParentCode && RemoteSettings.ParentScope(key, ProfilesGuarded) is { } scope && !_grant.Fits(scope.Action, scope.Item))
                        return new RemoteCallResult(null, Strings.Get("parental.codeRequired"), "parent") { ParentAction = scope.Action, ParentItem = scope.Item };
                    // The TV page's own setter, with its checks and side effects (the host consumes the grant there).
                    await bridge.InvokeAsync(call.Method, call.Args);
                    Console.Error.WriteLine($"[remote] setting {key} from {device.Id}");
                    // The TV page takes the new value (volume, screensaver, language …).
                    Emit("settings", null);
                    return RemoteCallResult.Ok(null);
                }
                case "skin":
                {
                    var id = call.Args[0].GetString()!;
                    await bridge.InvokeAsync(call.Method, call.Args);
                    Console.Error.WriteLine($"[remote] skin from {device.Id}");
                    Emit("skin", id);
                    return RemoteCallResult.Ok(null);
                }
                case "parentCode":
                {
                    var answer = JsonSerializer.SerializeToElement(await bridge.InvokeAsync(call.Method, call.Args));
                    var granted = answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                    var wait = answer.TryGetProperty("waitSeconds", out var w) && w.TryGetInt32(out var seconds) ? seconds : 0;
                    if (granted) Emit("toast", Strings.Get("remote.parent.accepted", RemotePower.CleanName(device.Name)));
                    return new RemoteCallResult(new { ok = granted, waitSeconds = wait }) { Granted = granted };
                }
            }
            var result = await bridge.InvokeAsync(call.Method, call.Args);
            switch (op)
            {
                case "library":
                    return RemoteCallResult.Ok(RemoteCalls.Library((IEnumerable<LibraryItem>)result!, level));
                case "rename" or "hide":
                    Console.Error.WriteLine($"[remote] {op} {call.Args[0].GetString()} from {device.Id}");
                    return RemoteCallResult.Ok(RemoteCalls.Entry((LibraryItem)result!));
                case "collectionMember" or "collectionCreate" or "collectionRename" or "collectionDelete":
                    // The TV shows the change at once (its collections module takes the list); the phones get the signal.
                    Emit("collections", GetCollections());
                    return RemoteCallResult.Ok(RemoteCalls.Project("collections", GetCollections(), await VisibleToPhoneAsync(bridge, level)));
                // Records of the host go to the TV page whole; the phone gets a fixed projection of the fields its page shows
                // (collections cut to the entries its library shows).
                case "collections":
                    return RemoteCallResult.Ok(RemoteCalls.Project(op, result, await VisibleToPhoneAsync(bridge, level)));
                case "downloads":
                    return RemoteCallResult.Ok(RemoteCalls.Project(op, result));
                case "settings":
                {
                    // The settings page of the phone: only the listed settings (fixed projection, RemoteSettings.Project), not the host's record.
                    var skins = (IEnumerable<SkinInfo>)(await bridge.InvokeAsync("listSkins", JsonSerializer.SerializeToElement(Array.Empty<object>())))!;
                    return RemoteCallResult.Ok(RemoteSettings.Project(JsonSerializer.SerializeToElement(result, RemoteChannel.Json), skins, Strings.Language));
                }
                default:
                    return RemoteCallResult.Ok(result);
            }
        }
        catch (ParentCodeNeededException ex)
        {
            return new RemoteCallResult(null, ex.Message, "parent") { ParentAction = ex.Action, ParentItem = ex.Item };
        }
        catch (LockedException ex)
        {
            return RemoteCallResult.Fail(ex.Message, "locked");
        }
        catch (ProfileHeldException ex)
        {
            return RemoteCallResult.Fail(ex.Message, "held");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException)
        {
            // A refusal of the host (an area off, a game runs with parental controls, a name not allowed): one line, the text to the phone.
            Console.Error.WriteLine($"[remote] {op} from {device.Id} refused: {ex.Message}");
            return RemoteCallResult.Fail(UiBridge.ShownText(ex), "refused");
        }
    }
}
