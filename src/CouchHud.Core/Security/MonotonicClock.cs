namespace CouchHud.Core.Security;

/// <summary>
/// Time that only moves forward: the wall clock at the start plus the ticks since. Used for everything that
/// must not be shortened or stretched by someone turning the system clock (unlock windows, parent grants, wait
/// times after wrong codes). The system clock is only read once, at construction.
/// </summary>
public sealed class MonotonicClock
{
    private readonly DateTimeOffset _start;
    private readonly long _startTicks;
    private readonly Func<long> _ticksMs;

    public MonotonicClock() : this(DateTimeOffset.UtcNow, () => Environment.TickCount64)
    {
    }

    public MonotonicClock(DateTimeOffset start, Func<long> ticksMs)
    {
        _start = start;
        _ticksMs = ticksMs;
        _startTicks = ticksMs();
    }

    public DateTimeOffset UtcNow => _start.AddMilliseconds(Math.Max(0, _ticksMs() - _startTicks));
}

/// <summary>
/// A one-time release by the parents' code for exactly one action on one entry (extending time, a locked game,
/// a change to the profile). Used up by that action; a different action or entry gets nothing from it.
/// </summary>
public sealed class ParentGrant(Func<DateTimeOffset> now, TimeSpan lifetime)
{
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private string? _action;
    private string? _item;

    /// <summary>Gives the grant. Without an action it fits any (the code was entered before the question was known).</summary>
    public void Give(string? action = null, string? item = null)
    {
        _until = now() + lifetime;
        _action = action;
        _item = item;
    }

    public bool Active => now() < _until;

    /// <summary>True (and used up) when the grant is alive and fits: an unbound grant fits everything, a bound one only its own action and entry.</summary>
    public bool TryConsume(string action, string? item = null)
    {
        if (!Fits(action, item)) return false;
        Clear();
        return true;
    }

    /// <summary>Alive and fitting, but not used up (a check before the action).</summary>
    public bool Fits(string action, string? item = null) =>
        Active && (_action is null || _action == action) && (_item is null || _item == item);

    public void Clear()
    {
        _until = DateTimeOffset.MinValue;
        _action = null;
        _item = null;
    }
}

/// <summary>
/// A pending request for the Windows lock screen (unlock the couch, or the parents' code): valid only for a short
/// time, only when the lock came from us, and used up by the first matching unlock. Another lock (Windows Hello,
/// standby, the guard) between the request and the unlock cancels it.
/// </summary>
public sealed class WindowsUnlockRequest(Func<DateTimeOffset> now, TimeSpan? lifetime = null, TimeSpan? lockDelay = null)
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(3);
    private readonly TimeSpan _lockDelay = lockDelay ?? TimeSpan.FromSeconds(15);
    private DateTimeOffset? _asked;
    private bool _lockedByUs;

    /// <summary>The prompt of the lock screen was requested (the caller locks Windows right after).</summary>
    public void Ask()
    {
        _asked = now();
        _lockedByUs = false;
    }

    public void Cancel()
    {
        _asked = null;
        _lockedByUs = false;
    }

    /// <summary>Waiting for the unlock right now (alive and not expired).</summary>
    public bool Pending
    {
        get
        {
            if (_asked is not { } asked) return false;
            if (now() - asked > _lifetime)
            {
                Cancel();
                return false;
            }
            return true;
        }
    }

    /// <summary>Windows reported a lock: ours if it came right after the request, any other cancels the request.</summary>
    public void OnLocked()
    {
        if (!Pending) return;
        if (_lockedByUs || now() - _asked!.Value > _lockDelay)
        {
            Cancel();
            return;
        }
        _lockedByUs = true;
    }

    /// <summary>Windows reported an unlock: true once if it answers our request.</summary>
    public bool OnUnlocked()
    {
        if (!Pending || !_lockedByUs) return false;
        Cancel();
        return true;
    }
}
