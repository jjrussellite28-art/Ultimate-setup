namespace CouchHud.ServiceClient;

/// <summary>
/// The fail-closed rules of the couch lock with the service (review of P3/P4, M2): when the service is not there, is squatted or
/// answers against the protocol, nothing opens. No unlock, no removal of the shell, no release of the policies, and never a quiet
/// step down to "no lock". Pure: the shell asks, these answer.
/// </summary>
public static class FailClosed
{
    public static ServiceHealth Classify(ServiceState state) => state switch
    {
        ServiceState.Running => ServiceHealth.Healthy,
        ServiceState.NotExpected => ServiceHealth.NotExpected,
        ServiceState.Squatted or ServiceState.Inconsistent => ServiceHealth.Alarm,
        _ => ServiceHealth.Unavailable
    };

    /// <summary>
    /// May the couch lock open by code? Not while the service is expected and not healthy: the release that goes with the unlock could
    /// not happen, and a squatter could be waiting for exactly that. Without a lock there is nothing to keep; a copy without a service
    /// by design keeps the lock as before (in the UI only).
    /// </summary>
    public static bool MayUnlock(bool couchLockOn, ServiceState state) =>
        !couchLockOn || Classify(state) is ServiceHealth.Healthy or ServiceHealth.NotExpected;

    /// <summary>The way back (shell entry, tasks, policies go): only with a healthy service, which releases the policies first.</summary>
    public static bool MayRemoveShell(ServiceState state) => Classify(state) is ServiceHealth.Healthy or ServiceHealth.NotExpected;

    /// <summary>Policies are released only through a healthy service; otherwise they stay set.</summary>
    public static bool MayReleasePolicies(ServiceState state) => Classify(state) == ServiceHealth.Healthy;

    /// <summary>
    /// Whether a service is expected: the installed copy (protected folder), and any machine where the service is registered, whatever
    /// the folder says (review L2). Only an unprotected copy without a registered service has none by design.
    /// </summary>
    public static bool ServiceExpected(bool protectedInstall, ScmInfo scm) => protectedInstall || scm.State != ScmState.NotInstalled;

    /// <summary>--dev relaxes the lock (unlock rule, service watch) only where no service is expected (review L5).</summary>
    public static bool DevMayRelax(bool dev, bool serviceExpected) => dev && !serviceExpected;

    /// <summary>Whether the UI offers "repair the service" (UAC): whenever the service should be there and is not healthy.</summary>
    public static bool OfferRepair(ServiceState state) => Classify(state) is ServiceHealth.Unavailable or ServiceHealth.Alarm && state != ServiceState.Unknown;
}

/// <summary>What the shell should do right now about a service that is missing while the lock needs it.</summary>
public enum LockWatchAction
{
    None,

    /// <summary>Tell the user (once per incident): the lock's Windows policies are not active, offer the repair.</summary>
    Warn,

    /// <summary>Lock Windows (once per incident): the lock needs the service and it has been gone too long, or someone is in the way.</summary>
    LockWindows
}

/// <summary>
/// Watches the service while the couch lock wants its Windows policies (design §4, fail-closed coupling): unreachable for longer than
/// <see cref="Grace"/> counts as manipulation and Windows is locked; squatting or a contradicting answer locks at once. A service that
/// looks healthy but does not set the policies (refused, not applied, busy, rate limited: <c>policiesFailing</c>) is no better: the same
/// grace, then the same lock (review M1). Each incident warns once and locks Windows again every <see cref="Relock"/> while it lasts
/// (review L4); a healthy look with working policies ends it. Pure state machine over a clock the caller passes in.
/// </summary>
public sealed class LockWatch(TimeSpan grace, TimeSpan? relock = null)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultRelock = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _since;
    private DateTimeOffset? _lockedAt;
    private bool _warned;

    public TimeSpan Grace { get; } = grace;

    public TimeSpan Relock { get; } = relock ?? DefaultRelock;

    /// <summary>An incident is running: the lock wants the service and does not have it.</summary>
    public bool InIncident => _since is not null;

    public LockWatchAction Observe(DateTimeOffset now, bool policiesWanted, ServiceState state, bool policiesFailing = false)
    {
        var health = FailClosed.Classify(state);
        // Healthy only counts when the policies really get set through it.
        if (health == ServiceHealth.Healthy && policiesFailing) health = ServiceHealth.Unavailable;
        if (!policiesWanted || health is ServiceHealth.Healthy or ServiceHealth.NotExpected || state == ServiceState.Unknown)
        {
            _since = null;
            _lockedAt = null;
            _warned = false;
            return LockWatchAction.None;
        }
        _since ??= now;
        var due = _lockedAt is { } last ? now - last >= Relock : health == ServiceHealth.Alarm || now - _since.Value >= Grace;
        if (due)
        {
            _lockedAt = now;
            _warned = true;
            return LockWatchAction.LockWindows;
        }
        // A service that is still starting (boot) is not worth a warning yet; the grace above still runs for it.
        if (!_warned && state != ServiceState.Starting)
        {
            _warned = true;
            return LockWatchAction.Warn;
        }
        return LockWatchAction.None;
    }
}

/// <summary>What the repair button may do right now.</summary>
public enum RepairVerdict
{
    Allowed,

    /// <summary>The service is healthy (or there is none by design): nothing to repair, no UAC prompt.</summary>
    NotNeeded,

    /// <summary>A repair started less than <see cref="RepairGate.Cooldown"/> ago.</summary>
    CoolingDown
}

/// <summary>
/// The repair (an elevated script) only when the state calls for it, and not more often than every <see cref="Cooldown"/>: the bridge
/// call is open while locked, so a page must not be able to raise UAC prompts at will (review L3).
/// </summary>
public sealed class RepairGate(TimeSpan cooldown)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private DateTimeOffset? _last;

    public TimeSpan Cooldown { get; } = cooldown;

    public RepairVerdict TryBegin(DateTimeOffset now, ServiceState state)
    {
        if (!FailClosed.OfferRepair(state)) return RepairVerdict.NotNeeded;
        lock (_lock)
        {
            if (_last is { } last && now - last < Cooldown) return RepairVerdict.CoolingDown;
            _last = now;
            return RepairVerdict.Allowed;
        }
    }
}
