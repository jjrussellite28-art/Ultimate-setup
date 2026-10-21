using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Logging;

/// <summary>
/// The last events per account, in memory, for <see cref="Operation.GetRecentEvents"/> (each caller sees only its own SID's).
/// Bounded per account and in the number of accounts; messages are redacted like the event log.
/// </summary>
public sealed class RecentEvents(IMonotonicClock clock, Func<long>? wallClockMs = null)
{
    public const int PerAccount = RequestValidator.MaxEvents;
    public const int MaxAccounts = 256;

    private readonly Func<long> _now = wallClockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly Dictionary<string, (Queue<EventEntry> Events, TimeSpan LastUsed)> _bySid = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Add(string sid, string kind, string message)
    {
        var entry = new EventEntry(_now(), kind, RedactingEventSink.Redact(message));
        lock (_lock)
        {
            if (!_bySid.TryGetValue(sid, out var slot))
            {
                if (_bySid.Count >= MaxAccounts)
                {
                    var oldest = _bySid.OrderBy(e => e.Value.LastUsed).First().Key;
                    _bySid.Remove(oldest);
                }
                slot = (new Queue<EventEntry>(), clock.Now);
            }
            slot.Events.Enqueue(entry);
            while (slot.Events.Count > PerAccount) slot.Events.Dequeue();
            _bySid[sid] = (slot.Events, clock.Now);
        }
    }

    /// <summary>The newest <paramref name="count"/> events of the account, oldest first.</summary>
    public EventEntry[] For(string sid, int count)
    {
        lock (_lock)
        {
            if (!_bySid.TryGetValue(sid, out var slot)) return [];
            return slot.Events.Skip(Math.Max(0, slot.Events.Count - count)).ToArray();
        }
    }
}
