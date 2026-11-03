namespace CouchHud.Core.Remote;

/// <summary>
/// Wrong tries at the encrypted channel (a request that names a phone but does not open, an unknown phone) counted per source address: a
/// few of them pause that source for a short while, like the pairing does. Guessing is pointless anyway (2^256); the pause keeps a
/// device on the LAN from hammering the HUD. A good request clears the source's count. The table is hard-capped
/// (<see cref="MaxSources"/>): when it is full the oldest entry makes room (one not paused first), a full table is never emptied, so
/// flooding it with new sources cannot lift a pause. IPv6 sources count by their /64 (a host owns the whole prefix and could change the
/// rest at will), an IPv4-mapped IPv6 address counts as the IPv4 address. Loopback is never paused: only a process of this machine (or a
/// proxy on it that forwards the LAN, where one pause would lock out every phone) comes from there, and the key store is open to it anyway.
/// </summary>
internal sealed class SourceGuard
{
    internal const int MaxSources = 256;
    private const int MaxFailures = 5;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(1);
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _sources = [];
    private long _seq;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset BlockedUntil;
        public long Order;
    }

    /// <summary>The key a source counts under: IPv4 as is, IPv4-mapped as IPv4, IPv6 by its /64; null when it is loopback (never paused).</summary>
    internal static string? Normalize(string? source)
    {
        if (string.IsNullOrEmpty(source) || !System.Net.IPAddress.TryParse(source, out var ip)) return source ?? "";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        System.Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes).ToString() + "/64";
    }

    public bool Blocked(string? source, DateTimeOffset now)
    {
        if (Normalize(source) is not { } key) return false;
        lock (_lock)
        {
            if (!_sources.TryGetValue(key, out var e) || e.BlockedUntil == default) return false;
            if (now < e.BlockedUntil) return true;
            _sources.Remove(key);
            return false;
        }
    }

    public void Failed(string? source, DateTimeOffset now)
    {
        if (Normalize(source) is not { } key) return;
        lock (_lock)
        {
            Sweep(now);
            if (!_sources.TryGetValue(key, out var e))
            {
                if (_sources.Count >= MaxSources) EvictOldest(now);
                e = _sources[key] = new Entry { Order = ++_seq };
            }
            if (e.BlockedUntil > now) return;
            e.BlockedUntil = default;
            if (++e.Failures >= MaxFailures)
            {
                e.Failures = 0;
                e.BlockedUntil = now + Pause;
            }
        }
    }

    public void Succeeded(string? source)
    {
        if (Normalize(source) is not { } key) return;
        lock (_lock)
        {
            // A source that is paused stays so (only the pause's end lifts it); its count of wrong tries starts again.
            if (_sources.TryGetValue(key, out var e) && e.BlockedUntil == default) _sources.Remove(key);
        }
    }

    internal int Count { get { lock (_lock) return _sources.Count; } }

    /// <summary>Expired pauses go, at most once a second (not with every wrong try).</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepEvery) return;
        _lastSweep = now;
        foreach (var stale in _sources.Where(b => b.Value.BlockedUntil != default && b.Value.BlockedUntil <= now).Select(b => b.Key).ToList()) _sources.Remove(stale);
    }

    private void EvictOldest(DateTimeOffset now)
    {
        var victim = _sources.Where(b => b.Value.BlockedUntil <= now).OrderBy(b => b.Value.Order).Select(b => b.Key).FirstOrDefault()
                     ?? _sources.OrderBy(b => b.Value.Order).Select(b => b.Key).First();
        _sources.Remove(victim);
    }
}
