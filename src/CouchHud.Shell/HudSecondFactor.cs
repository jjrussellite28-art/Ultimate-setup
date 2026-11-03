using System.Text.Json;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Remote;
using CouchHud.Shell.Services;

namespace CouchHud.Shell;

/// <summary>Marks a method that writes protection state but needs no second factor, with the reason (checked by a test that walks the sources).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
internal sealed class SecondFactorExemptAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;

    /// <summary>True: the method's callers hold the guard; a test checks that every caller is guarded (before the call) or exempt itself.</summary>
    public bool CallersGuard { get; init; }
}

/// <summary>
/// The second factor (0.7), HUD side: the guard that every critical action (<see cref="SecondFactorActions"/>) passes after its code was
/// accepted, the question for the phone, the TV page's calls and the phone's two operations. The rules are in Core
/// (<see cref="SecondFactorChallenges"/>, <see cref="SecondFactorRules"/>); here only the wiring to the config, the grants and the phones.
/// Enforcement sits in the HUD process (the one that holds the grants and the phone channel), see SECURITY.md for the limit.
/// </summary>
public sealed partial class Hud
{
    /// <summary>The failed tries and lock-outs per phone live in a small file next to the settings, so a restart is no fresh start.</summary>
    private readonly SecondFactorChallenges Factor = new(MonoNow, null, Path.Combine(dataDir, "second-factor-state.json"));

    /// <summary>
    /// When the pairing of a new phone was confirmed on a phone (the second factor on): the QR code works until then, for one request at a time
    /// and one phone. Read and written under <see cref="_pairLock"/> only (Kestrel threads and the UI thread meet here).
    /// </summary>
    private DateTimeOffset _pairOpenUntil = DateTimeOffset.MinValue;

    private bool _pairBusy;
    private readonly object _pairLock = new();

    /// <summary>
    /// On while the owner chose it. Not in PC mode: there the couch lock and the parental controls are not enforced either, and a phone is a
    /// plain remote there, so nobody could confirm (SECURITY.md).
    /// </summary>
    private bool SecondFactorOn => config.Current.SecondFactor && !_pcMode;

    private IReadOnlyList<RemoteDevice> Confirmers()
    {
        var active = config.Current.ActiveProfile.Id;
        return RemoteDevicesSnapshot().Where(d => SecondFactorRules.MayConfirm(d, active, _pcMode)).ToList();
    }

    /// <summary>The phone that confirmed may still confirm when the ticket is used (still paired, same rules as for answering).</summary>
    private bool TicketPhoneStillOk(string deviceId) =>
        Pairing.ById(deviceId) is { } d && SecondFactorRules.MayConfirm(d, config.Current.ActiveProfile.Id, _pcMode);

    /// <summary>One guarded step: the action of the list and the parameters that are bound into the question.</summary>
    private readonly record struct Step(string Action, string?[] Parameters);

    /// <summary>
    /// The guard. Call it where an action of the list is about to change something, AFTER the usual checks that do not use the code up
    /// and BEFORE the code is used up. <paramref name="codeAccepted"/>: the code (or the unlocked couch) the site itself asks for is in order
    /// already and not yet used; if not, the usual code check answers first (the code always comes first, and the ticket is not touched before
    /// that). Throws <see cref="SecondFactorNeededException"/> when a confirmation is needed: the question is asked, the page repeats the call
    /// after the confirmation, and the ticket (this action, these parameters, once, 60 s, the same phone still allowed) lets it through.
    /// </summary>
    private void RequireSecondFactor(string action, bool codeAccepted, params string?[] parameters) =>
        Guard([new Step(action, parameters)], codeAccepted, null);

    /// <summary>Like <see cref="RequireSecondFactor"/>, with a short line under the action on the TV and the phone (a profile, a rule, a plugin).</summary>
    private void RequireSecondFactorFor(string action, bool codeAccepted, string? detail, params string?[] parameters) =>
        Guard([new Step(action, parameters)], codeAccepted, detail);

    /// <summary>
    /// Several steps of one change as ONE question (the integration mode switches shell, lock and Task Manager together): one confirmation
    /// covers exactly this set. An empty set needs nothing.
    /// </summary>
    private void RequireSecondFactorAll(bool codeAccepted, params (string Action, string?[] Parameters)[] steps) =>
        Guard(steps.Select(s => new Step(s.Action, s.Parameters)).ToList(), codeAccepted, null);

    private void Guard(IReadOnlyList<Step> steps, bool codeAccepted, string? detail)
    {
        if (steps.Count == 0 || !SecondFactorOn || !steps.All(s => SecondFactorRules.Required(true, s.Action))) return;
        if (!codeAccepted) return;
        var action = steps[0].Action;
        var hash = steps.Count == 1
            ? SecondFactorChallenges.Bind(action, steps[0].Parameters)
            : SecondFactorChallenges.BindAll(steps.Select(s => SecondFactorChallenges.Bind(s.Action, s.Parameters)).ToArray());
        if (Factor.TryUse(action, hash, TicketPhoneStillOk)) return;
        var label = string.Join(", ", steps.Select(s => Strings.Get(SecondFactorActions.TextKey(s.Action))).Distinct());
        var cleanDetail = SecondFactorRules.CleanDetail(detail);
        var ask = Factor.Ask(action, hash, label, cleanDetail);
        if (ask.Question is not { } question)
            throw new SecondFactorNeededException(Strings.Get("twofa.throttled", (int)Math.Ceiling(ask.Throttled.TotalSeconds)), "", 0, action, label, 0, false, 0)
            {
                ThrottledSeconds = (int)Math.Ceiling(ask.Throttled.TotalSeconds),
                Detail = cleanDetail
            };
        Console.Error.WriteLine($"[twofa] asked for {action}");
        _remote.Channel?.Publish("secondFactor");
        Emit("twoFactor", new { kind = "asked" });
        var open = _remote.Channel?.Events.OpenDevices() ?? new HashSet<string>();
        throw new SecondFactorNeededException(Strings.Get("twofa.needed"), question.RequestId, question.Number, action, label,
            (long)(question.Expires - MonoNow()).TotalMilliseconds,
            Confirmers().Any(d => open.Contains(d.Id)), (int)Math.Ceiling(Factor.LockedFor(Confirmers().Select(d => d.Id)).TotalSeconds))
        { Detail = cleanDetail };
    }

    /// <summary>
    /// A change that takes the phones away (<paramref name="affected"/>: unpaired or lowered) and would leave nobody who can confirm ends the
    /// second factor through <see cref="ReconcileSecondFactor"/>. That is switching it off, so it takes the second factor itself. Needs only
    /// the unlocked couch otherwise (as the change always did), so the code comes first here too.
    /// </summary>
    private void RequireSecondFactorUnlessStays(Func<RemoteDevice, bool> affected, params string?[] parameters)
    {
        if (!SecondFactorOn) return;
        if (RemoteDevicesSnapshot().Where(d => !affected(d)).Any(SecondFactorRules.IsConfirmer)) return;
        RequireSecondFactor(SecondFactorActions.SecondFactorOff, IsUnlocked, parameters);
    }

    /// <summary>The code for this action is in order and not used up: the grant fits (with a parents' code) or the couch is unlocked.</summary>
    private bool CodeAcceptedFor(string grantAction, string? item = null) => SecondFactorRules.CodeAccepted(HasParentCode, _grant.Fits(grantAction, item), IsUnlocked);

    /// <summary>
    /// The same for a site that asks the parents' grant only with parental controls on (and the unlocked couch otherwise): exactly what the
    /// site itself demands, no stricter (a stricter reading would skip the second factor where the site lets the action through).
    /// </summary>
    private bool SiteCodeAccepted(string grantAction, string? item = null) =>
        SecondFactorRules.SiteCodeAccepted(config.Current.ParentalEnabled, HasParentCode, _grant.Fits(grantAction, item), IsUnlocked);

    /// <summary>The question and the tickets go (a phone was unpaired or lowered, a profile switched, PC mode started); the pairing window closes too.</summary>
    private void ResetSecondFactor()
    {
        Factor.Reset();
        lock (_pairLock) _pairOpenUntil = DateTimeOffset.MinValue;
    }

    /// <summary>The TV page asks what is open: the number and the time left; null when nothing.</summary>
    public object? TwoFactorState()
    {
        if (Factor.Current is not { } q) return null;
        var open = _remote.Channel?.Events.OpenDevices() ?? new HashSet<string>();
        return new
        {
            requestId = q.RequestId, number = q.Number, label = q.Label, detail = q.Detail, action = q.Action,
            secondsLeft = Math.Max(0, (int)Math.Ceiling((q.Expires - MonoNow()).TotalSeconds)),
            reachable = Confirmers().Any(d => open.Contains(d.Id)),
            lockedOutSeconds = (int)Math.Ceiling(Factor.LockedFor(Confirmers().Select(d => d.Id)).TotalSeconds)
        };
    }

    /// <summary>The page gave up (B), the time ran out or the dialog closed.</summary>
    public void TwoFactorCancel(string requestId)
    {
        Factor.Cancel(requestId);
        _remote.Channel?.Publish("secondFactor");
    }

    /// <summary>
    /// The emergency way: a Windows administrator confirms through UAC (the existing approvals path, the secure desktop). Works without a
    /// phone and also while the phone way is locked out; it is the way when the phone is lost or empty. On an administrator account in Admin
    /// Approval Mode this is only a click (SECURITY.md); the switch-on dialog says so.
    /// </summary>
    public async Task<object> TwoFactorByAdminAsync(string requestId)
    {
        RequireUnlocked();
        try
        {
            if (!await AdminService.ObtainAdminGrantAsync([], Strings.Get("twofa.adminReason"))) return new { ok = false };
        }
        catch (InvalidOperationException ex)
        {
            return new { ok = false, blocked = ex.Message };
        }
        var ok = Factor.ApproveByAdmin(requestId);
        if (ok) Console.Error.WriteLine("[twofa] confirmed by a Windows administrator");
        _remote.Channel?.Publish("secondFactor");
        return new { ok };
    }

    /// <summary>Switch the second factor on (needs the unlocked couch, the code check and a phone that can confirm) or off (code and phone).</summary>
    public object SetSecondFactor(bool on)
    {
        RefuseWhileConfigFaulted();
        if (on) return EnableSecondFactor();
        if (!config.Current.SecondFactor) return GetSecondFactor();
        if (HasParentCode && !_grant.Fits("secondFactorOff")) throw new ParentCodeNeededException(Strings.Get("parental.codeRequired"), "secondFactorOff", null);
        if (!HasParentCode) RequireUnlocked();
        RequireSecondFactor(SecondFactorActions.SecondFactorOff, true);
        if (HasParentCode && !_grant.TryConsume("secondFactorOff")) throw new ParentCodeNeededException(Strings.Get("parental.codeRequired"), "secondFactorOff", null);
        config.UpdateOrThrow(c => c.SecondFactor = false);
        Factor.ResetAll();
        Console.Error.WriteLine("[twofa] off");
        return GetSecondFactor();
    }

    [SecondFactorExempt("switching the second factor ON raises protection; the OFF path in SetSecondFactor holds its guard")]
    private object EnableSecondFactor()
    {
        RequireUnlocked();
        switch (SecondFactorRules.CanEnable(config.Current.CouchLock, RemoteDevicesSnapshot()))
        {
            case SecondFactorBlock.NoCodeCheck: throw new InvalidOperationException(Strings.Get("twofa.needCodeCheck"));
            case SecondFactorBlock.NoPhone: throw new InvalidOperationException(Strings.Get("twofa.needPhone"));
        }
        if (config.Current.SecondFactor) return GetSecondFactor();
        config.UpdateOrThrow(c => c.SecondFactor = true);
        Console.Error.WriteLine("[twofa] on");
        return GetSecondFactor();
    }

    public object GetSecondFactor()
    {
        var block = SecondFactorRules.CanEnable(config.Current.CouchLock, RemoteDevicesSnapshot());
        return new
        {
            on = config.Current.SecondFactor, canEnable = block.ToString(), phones = RemoteDevicesSnapshot().Count(SecondFactorRules.IsConfirmer),
            // An administrator account (Admin Approval Mode): UAC is only a click, the emergency way is no real second proof (the dialog warns).
            adminAccount = SecondFactorRules.IsAdminAccount(TokenElevation.Type)
        };
    }

    /// <summary>
    /// The second factor ends when its conditions go (the code check off, the last confirming phone unpaired or lowered): fail closed would
    /// lock the owner out of every critical action, so it falls back to the code alone, and says so. Both ways in need the second factor
    /// themselves (the lock change, the unpairing and lowering), so this is never a way around it.
    /// </summary>
    [SecondFactorExempt("only follows a change that took the second factor itself (lock off, last phone gone), see the callers")]
    private void ReconcileSecondFactor()
    {
        if (!config.Current.SecondFactor) return;
        if (SecondFactorRules.StillPossible(config.Current.CouchLock, RemoteDevicesSnapshot())) return;
        try
        {
            config.UpdateOrThrow(c => c.SecondFactor = false);
            Factor.ResetAll();
            Console.Error.WriteLine("[twofa] off: the conditions are gone");
            Ui(() => Emit("toast", Strings.Get("twofa.endedNoConditions")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"[twofa] could not end: {ex.Message}");
        }
    }

    bool IRemoteChannelTarget.BeginPairing()
    {
        if (!SecondFactorOn) return true;
        lock (_pairLock)
        {
            if (_pairBusy || MonoNow() >= _pairOpenUntil) return false;
            _pairBusy = true;
            return true;
        }
    }

    void IRemoteChannelTarget.EndPairing(bool paired)
    {
        lock (_pairLock)
        {
            if (!_pairBusy) return;
            _pairBusy = false;
            if (paired) _pairOpenUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>The window in which the QR code works (for the pairing page): only with the second factor on it can be closed.</summary>
    private bool PairingWindowClosed()
    {
        if (!SecondFactorOn) return false;
        lock (_pairLock) return MonoNow() >= _pairOpenUntil;
    }

    /// <summary>A new pairing under the second factor: confirmed on a phone first; then the QR code works for a short time, for one phone.</summary>
    private void OpenPairingWindow()
    {
        if (!SecondFactorOn) return;
        RequireSecondFactor(SecondFactorActions.RemotePair, true);
        lock (_pairLock) _pairOpenUntil = MonoNow().AddMinutes(10);
    }

    /// <summary>The phone reads the open question (without the number) or types the number. The HUD decides again who may.</summary>
    private RemoteCallResult SecondFactorFromPhone(RemoteDevice device, string op, JsonElement args)
    {
        var eligible = SecondFactorRules.MayConfirm(device, config.Current.ActiveProfile.Id, _pcMode);
        if (op == "secondFactor")
        {
            if (!eligible || Factor.Current is not { } q) return RemoteCallResult.Ok(new { pending = false });
            return RemoteCallResult.Ok(new
            {
                pending = true, requestId = q.RequestId, action = q.Action, label = q.Label, detail = q.Detail,
                asked = q.Asked.ToUnixTimeMilliseconds() - MonoNow().ToUnixTimeMilliseconds() + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                secondsLeft = Math.Max(0, (int)Math.Ceiling((q.Expires - MonoNow()).TotalSeconds)),
                lockedOutSeconds = (int)Math.Ceiling(Factor.LockedForDevice(device.Id).TotalSeconds)
            });
        }
        var id = PhoneString(args, "requestId");
        // The number is typed: two digits, an integer; anything else is a malformed request and counts for nothing.
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("number", out var c) || !c.TryGetInt32(out var typed) || typed is < 10 or > 99 || id is null) return Bad();
        var outcome = Factor.Answer(id, device.Id, typed, eligible);
        Console.Error.WriteLine($"[twofa] answer from {device.Id}: {outcome}");
        Ui(() => Emit("twoFactor", new { kind = outcome == SecondFactorAnswer.Confirmed ? "confirmed" : "answered", result = outcome.ToString() }));
        _remote.Channel?.Publish("secondFactor");
        return RemoteCallResult.Ok(new { result = outcome.ToString(), lockedOutSeconds = (int)Math.Ceiling(Factor.LockedForDevice(device.Id).TotalSeconds) });
    }
}
