using CouchHud.Privileged;

namespace CouchHud.Service.Authorization;

/// <summary>What SERVICE_CONTROL_SESSIONCHANGE reported, reduced to what grants care about.</summary>
public enum SessionChange
{
    Other,
    Logon,
    Unlock,
    Lock,
    Logoff,
    Disconnect
}

/// <summary>
/// The Windows grants of P9 (RequestWindowsGrant "unlock") come from what the service itself sees through the SCM, never from what a
/// caller claims: a real unlock (<see cref="SessionChange.Unlock"/>, WTS_SESSION_UNLOCK) of the caller's own session within
/// <see cref="Window"/>. Each unlock gives one grant (it is used up), only to a caller of that session whose account is the session's user.
/// A sign-in (WTS_SESSION_LOGON) gives none (review H2): an automatic sign-in types nothing, and the shell's recovery after a crash asks an
/// administrator instead. A lock, a disconnect or a sign-out ends the unlock and every grant of the session.
/// </summary>
/// <remarks>
/// What this proves: someone who could unlock this Windows session was at it a moment ago. That is the person at the couch in the Windows
/// lock mode only (the handler checks the mode and refuses accounts without a password, review H1); it is never a parent's or an
/// administrator's approval, so these grants are of kind <see cref="GrantKinds.WindowsUnlock"/>, which no parent-only operation accepts.
/// </remarks>
/// <param name="sessionUser">The SID of the user signed in to a session now (WTS), null when unknown.</param>
public sealed class SessionEvents(IMonotonicClock clock, GrantStore grants, Func<int, string?> sessionUser, IEventSink log)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);
    public const int MaxSessions = 64;

    private readonly Dictionary<int, TimeSpan> _unlocked = [];
    private readonly Lock _lock = new();

    /// <summary>From the SCM's thread: quick, no lookups here.</summary>
    public void OnChange(SessionChange change, int sessionId)
    {
        if (sessionId <= 0) return;
        switch (change)
        {
            case SessionChange.Unlock:
                lock (_lock)
                {
                    if (!_unlocked.ContainsKey(sessionId) && _unlocked.Count >= MaxSessions) _unlocked.Remove(_unlocked.OrderBy(e => e.Value).First().Key);
                    _unlocked[sessionId] = clock.Now;
                }
                break;
            case SessionChange.Lock or SessionChange.Logoff or SessionChange.Disconnect:
                lock (_lock) _unlocked.Remove(sessionId);
                var revoked = grants.RevokeSession(sessionId);
                if (revoked > 0) log.Info($"[grant] {revoked} grant(s) of session {sessionId} ended ({change.ToString().ToLowerInvariant()})");
                break;
        }
    }

    /// <summary>Takes the unlock of the caller's session, if there is a fitting one (it is used up).</summary>
    public bool TryTake(CallerInfo caller)
    {
        var session = caller.TokenSessionId;
        if (session <= 0 || session != caller.PipeSessionId) return false;
        TimeSpan at;
        lock (_lock)
        {
            if (!_unlocked.TryGetValue(session, out at)) return false;
            if (clock.Now - at > Window)
            {
                _unlocked.Remove(session);
                return false;
            }
        }
        // Outside the lock: this asks Windows. A process of another account that runs in this session (runas) gets nothing.
        string? user;
        try
        {
            user = sessionUser(session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            user = null;
        }
        if (user is null || !string.Equals(user, caller.Sid, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock)
        {
            // Used up, and only if it is still the same unlock (a lock in between removed it, a newer one replaced it).
            if (!_unlocked.TryGetValue(session, out var now) || now != at) return false;
            _unlocked.Remove(session);
            return true;
        }
    }
}

/// <summary>
/// Whether an account has no password (review H1): such an account is unlocked by anyone with a click, so its unlock proves nothing.
/// Asked once per account and kept for the life of the service (a boot, as a rule): LogonUser with an empty password; success or
/// ERROR_ACCOUNT_RESTRICTION (1327, "blank passwords may only sign in at the console") means blank. Only "wrong password" (1326) means
/// there is one; every other answer, and an account that cannot be resolved, counts as blank (fail-closed).
/// </summary>
/// <remarks>One failed sign-in per account and service start appears in the security log (and counts towards an account lockout policy).</remarks>
public sealed class BlankPasswordCheck(IAccountLookup accounts, ILogonVerifier logon)
{
    public const int ErrorAccountRestriction = 1327;

    private readonly Dictionary<string, bool> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public bool IsBlank(string sid)
    {
        lock (_lock)
        {
            if (_known.TryGetValue(sid, out var known)) return known;
        }
        bool blank;
        try
        {
            if (accounts.Lookup(sid) is not { } account) blank = true;
            else
            {
                var result = logon.Verify(account.User, account.Domain.Length == 0 ? null : account.Domain, "");
                blank = result.Error != LogonErrors.LogonFailure;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;   // not remembered: asked again next time
        }
        lock (_lock)
        {
            if (_known.Count < 1024) _known[sid] = blank;
        }
        return blank;
    }
}
