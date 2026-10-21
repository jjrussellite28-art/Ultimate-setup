using System.Security.Cryptography;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>
/// An administrator's approval through UAC (P9 review M-c): the administrator's credential never passes through the caller's process.
/// <list type="number">
/// <item>The caller registers what it wants approved (<see cref="Register"/>: the operations, or none for a plain "an administrator
/// confirmed") and gets a control code from 128 to 255.</item>
/// <item>The shell starts <c>%SystemRoot%\System32\sc.exe control CouchHud &lt;code&gt;</c> elevated; Windows' own UAC prompt (consent.exe on
/// the secure desktop) asks for an administrator. Only administrators hold SERVICE_USER_DEFINED_CONTROL in the service's access list, so
/// only an approved prompt delivers the code (<see cref="Approve"/>, from the SCM's thread).</item>
/// <item>The caller picks the approval up (<see cref="Collect"/>): only the same account, logon session and Windows session that registered it,
/// once; the service then issues a WindowsAdmin grant for exactly the registered operations.</item>
/// </list>
/// A request lives <see cref="Lifetime"/> and is used once. One request per Windows session: while it is open (not picked up, not expired)
/// a new one from that session is refused ("busy", the HUD waits), so nobody can swap it for other operations after the HUD's notice; a
/// later request of the session never gets the code of its last one again (an old prompt cannot approve a new request). Residual
/// (documented): a process of the same user can register its own request first, before the HUD does; UAC shows only
/// <c>sc.exe control CouchHud &lt;code&gt;</c>, not the operations, and an approval binds the operations, never their arguments.
/// </summary>
public sealed class AdminApprovals(IMonotonicClock clock, Func<int>? controlCode = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int FirstControl = 128;
    public const int LastControl = 255;
    public const int MaxPending = 64;

    private sealed class Pending
    {
        public required string Sid;
        public required long LogonId;
        public required int Session;
        public required IReadOnlyList<Operation> Ops;
        public required int Control;
        public required TimeSpan Expires;
        public bool Approved;
    }

    public enum State
    {
        None,
        Pending,
        Approved
    }

    private readonly Func<int> _controlCode = controlCode ?? (() => RandomNumberGenerator.GetInt32(FirstControl, LastControl + 1));
    private readonly Dictionary<int, Pending> _bySession = [];
    private readonly Dictionary<int, int> _lastControl = [];
    private readonly Lock _lock = new();

    /// <summary>A new request for the caller's session; the control code, or null when none can be given (one is open for the session: busy).</summary>
    public int? Register(CallerInfo caller, IReadOnlyList<Operation> ops)
    {
        if (caller.LogonId == 0 || caller.TokenSessionId <= 0 || caller.TokenSessionId != caller.PipeSessionId) return null;
        if (ops.Count > RequestValidator.MaxGrantOps) return null;
        lock (_lock)
        {
            var now = clock.Now;
            foreach (var session in _bySession.Where(e => now >= e.Value.Expires).Select(e => e.Key).ToList()) _bySession.Remove(session);
            // One open request per session: no swapping it for other operations while the HUD's notice and the prompt are up.
            if (_bySession.ContainsKey(caller.TokenSessionId)) return null;
            if (_bySession.Count >= MaxPending) return null;
            var used = _bySession.Values.Select(p => p.Control).ToHashSet();
            if (_lastControl.TryGetValue(caller.TokenSessionId, out var last)) used.Add(last);
            if (used.Count >= LastControl - FirstControl + 1) return null;
            int control;
            var tries = 0;
            do
            {
                control = _controlCode();
                if (++tries > 1000) return null;
            }
            while (control is < FirstControl or > LastControl || used.Contains(control));
            if (_lastControl.Count >= MaxPending * 4) _lastControl.Clear();
            _lastControl[caller.TokenSessionId] = control;
            _bySession[caller.TokenSessionId] = new Pending
            {
                Sid = caller.Sid,
                LogonId = caller.LogonId,
                Session = caller.TokenSessionId,
                Ops = [.. ops],
                Control = control,
                Expires = now + Lifetime
            };
            return control;
        }
    }

    /// <summary>
    /// False when the service's own access list lets anyone but administrators and SYSTEM send a control (checked at start, see
    /// <see cref="ServiceDacl"/>): then no control approves anything.
    /// </summary>
    public bool ControlsTrusted { get; set; } = true;

    /// <summary>Whether the session has a request open right now (the HUD waits for it instead of asking again).</summary>
    public bool IsOpen(int sessionId)
    {
        lock (_lock) return _bySession.TryGetValue(sessionId, out var pending) && clock.Now < pending.Expires;
    }

    /// <summary>The SCM delivered a user-defined control (only an administrator can send one): the request with that code is approved, once.</summary>
    public bool Approve(int control)
    {
        if (!ControlsTrusted) return false;
        lock (_lock)
        {
            var now = clock.Now;
            var pending = _bySession.Values.FirstOrDefault(p => p.Control == control && !p.Approved && now < p.Expires);
            if (pending is null) return false;
            pending.Approved = true;
            return true;
        }
    }

    /// <summary>The caller's own request: approved (taken out, with its operations), still pending, or none.</summary>
    public (State State, IReadOnlyList<Operation> Ops) Collect(CallerInfo caller)
    {
        lock (_lock)
        {
            if (!_bySession.TryGetValue(caller.TokenSessionId, out var pending) || caller.TokenSessionId != caller.PipeSessionId) return (State.None, []);
            // Another account or another sign-in in this session: not its request (and nothing is changed).
            if (!string.Equals(pending.Sid, caller.Sid, StringComparison.Ordinal) || pending.LogonId != caller.LogonId) return (State.None, []);
            if (clock.Now >= pending.Expires)
            {
                _bySession.Remove(caller.TokenSessionId);
                return (State.None, []);
            }
            if (!pending.Approved) return (State.Pending, []);
            _bySession.Remove(caller.TokenSessionId);
            return (State.Approved, pending.Ops);
        }
    }

    /// <summary>The session was locked, disconnected or signed out: its request goes.</summary>
    public void RevokeSession(int sessionId)
    {
        lock (_lock) _bySession.Remove(sessionId);
    }
}
