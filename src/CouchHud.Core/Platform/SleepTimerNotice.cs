namespace CouchHud.Core.Platform;

/// <summary>
/// When the host shows the sleep timer's notice over a game (Hud.SleepTimerWarning), without UI: the page arms it when it starts a timer
/// (action and seconds left) and disarms it when the timer is cancelled or fired. A notice comes only for the armed action, only close to
/// the armed end (from <see cref="Early"/> before to <see cref="Late"/> after it), only while the HUD is not in front, at most once
/// per <see cref="NoticeGap"/> (cancel and arm do not reset that), and only one notice per start of the timer; so the call cannot be used to put notices over a game or pull the HUD to the front at will.
/// </summary>
public sealed class SleepTimerNotice(Func<DateTimeOffset> now)
{
    /// <summary>How long before the armed end a notice may come (the page warns a minute before; some slack for a slow page). Real time must have passed since the arm: due minus this.</summary>
    public static readonly TimeSpan Early = TimeSpan.FromSeconds(90);

    /// <summary>How long after the armed end a notice may still come (the timer fires at the end; a slow page).</summary>
    public static readonly TimeSpan Late = TimeSpan.FromSeconds(30);

    /// <summary>At most one arm of a timer in this time (a cancel clears it, so a real restart works).</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(45);

    /// <summary>At most one notice in this time, whatever is cancelled or armed in between: the page cannot pull the HUD forward more often.</summary>
    public static readonly TimeSpan NoticeGap = TimeSpan.FromMinutes(5);

    /// <summary>The longest timer the host takes (the picker offers less).</summary>
    public const int MaxSecondsLeft = 24 * 60 * 60;

    private readonly object _lock = new();
    private string? _action;
    private DateTimeOffset _due;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>The two actions a timer can end in.</summary>
    public static bool IsAction(string? action) => action is "shutdown" or "standby";

    /// <summary>The shortest timer the host takes (the picker offers whole minutes); anything shorter is no timer and arms nothing.</summary>
    public const int MinSecondsLeft = 60;

    private DateTimeOffset _lastArm = DateTimeOffset.MinValue;

    /// <summary>
    /// A timer started (<paramref name="action"/>, ends in <paramref name="secondsLeft"/>) or ended (null; always taken, it clears the armed
    /// timer and the arm gap). Throws on an unknown action. A start is taken only with at least <see cref="MinSecondsLeft"/> seconds left
    /// and at most once per <see cref="MinGap"/> after the last start: the page asserts the time itself, so without this it could arm a
    /// timer that ends now, again and again, to pull notices over a game. Returns whether the timer is armed now.
    /// </summary>
    public bool Arm(string? action, int secondsLeft)
    {
        if (action is not null && !IsAction(action)) throw new ArgumentException("sleep timer: unknown action", nameof(action));
        lock (_lock)
        {
            if (action is null)
            {
                _action = null;
                _due = default;
                _lastArm = DateTimeOffset.MinValue;
                return false;
            }
            var t = now();
            if (secondsLeft < MinSecondsLeft || t - _lastArm < MinGap) return false;
            _lastArm = t;
            _action = action;
            _due = t + TimeSpan.FromSeconds(Math.Min(secondsLeft, MaxSecondsLeft));
            return true;
        }
    }

    /// <summary>
    /// Whether a notice goes over the game now, and the seconds it names (clamped to 1..600). Throws on an unknown action; counts a notice
    /// it allows for <see cref="MinGap"/>.
    /// </summary>
    public (bool Show, int Seconds) Decide(string? action, int seconds, bool hudInFront)
    {
        if (!IsAction(action)) throw new ArgumentException("sleep timer: unknown action", nameof(action));
        var n = Math.Clamp(seconds, 1, 600);
        if (hudInFront) return (false, n);
        lock (_lock)
        {
            var t = now();
            if (_action != action || t < _due - Early || t > _due + Late) return (false, n);
            if (t - _last < NoticeGap) return (false, n);
            _last = t;
            // One start, one notice: the armed timer is spent (a new start arms it again).
            _action = null;
            return (true, n);
        }
    }
}
