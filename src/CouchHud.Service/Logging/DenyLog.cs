using CouchHud.Privileged;
using CouchHud.Service.Authorization;

namespace CouchHud.Service.Logging;

/// <summary>
/// Refusals go to the log, but a caller that sends garbage in a loop must not flood it: per account at most
/// <see cref="PerWindow"/> lines per <see cref="Window"/>; the rest is counted and reported as one "N suppressed" line with the
/// next line that is let through. Bounded in the number of accounts (the rest share one key).
/// </summary>
public sealed class DenyLog(IEventSink log, IMonotonicClock clock)
{
    public const int PerWindow = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int MaxKeys = 256;
    private const string Overflow = "(other)";

    private sealed class Slot
    {
        public TimeSpan Start;
        public int Count;
        public int Suppressed;
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Logs <paramref name="line"/> as an error unless the account is over its budget.</summary>
    public void Error(string? sid, string line)
    {
        var key = sid ?? "(unknown)";
        int suppressed;
        lock (_lock)
        {
            var now = clock.Now;
            if (!_slots.TryGetValue(key, out var slot))
            {
                if (_slots.Count >= MaxKeys)
                {
                    foreach (var stale in _slots.Where(s => now - s.Value.Start >= Window && s.Value.Suppressed == 0).Select(s => s.Key).ToList()) _slots.Remove(stale);
                }
                if (_slots.Count >= MaxKeys) key = Overflow;
                if (!_slots.TryGetValue(key, out slot))
                {
                    slot = new Slot { Start = now };
                    _slots[key] = slot;
                }
            }
            if (now - slot.Start >= Window)
            {
                slot.Start = now;
                slot.Count = 0;
            }
            if (slot.Count >= PerWindow)
            {
                slot.Suppressed++;
                return;
            }
            slot.Count++;
            suppressed = slot.Suppressed;
            slot.Suppressed = 0;
        }
        if (suppressed > 0) log.Error($"[deny] {suppressed} suppressed for sid={key}");
        log.Error(line);
    }
}
