using System.Security.Cryptography;

namespace CouchHud.Core.Remote;

/// <summary>One open session of a phone on the encrypted channel: its key, the replay window of its requests and its event counter.</summary>
public sealed class RemoteSession
{
    internal RemoteSession(string id, string deviceId, byte[] key, DateTimeOffset now)
    {
        Id = id;
        DeviceId = deviceId;
        Key = key;
        Opened = now;
        LastSeen = now;
    }

    public string Id { get; }
    public string DeviceId { get; }
    public byte[] Key { get; }
    public DateTimeOffset Opened { get; }
    public DateTimeOffset LastSeen { get; internal set; }
    internal ReplayWindow Window { get; } = new();
    internal List<DateTimeOffset> Requests { get; } = [];
    private long _eventSeq;

    /// <summary>The next number of an event to this phone (strictly increasing; the page drops anything else).</summary>
    public long NextEventSeq() => Interlocked.Increment(ref _eventSeq);
}

/// <summary>Why a sealed request was not taken.</summary>
public enum SessionRefusal
{
    None,
    /// <summary>Unknown, run out, or the phone was unpaired: the page opens a new one.</summary>
    NoSession,
    /// <summary>A number seen before or too far back: a recorded request sent again.</summary>
    Replay,
    /// <summary>More than <see cref="RemoteSessions.MaxRequestsPerMinute"/> in a minute.</summary>
    TooOften
}

/// <summary>
/// The sessions of the encrypted channel. A session is opened with a message sealed under the phone's device key (both sides add a random
/// nonce, the session key comes from both), lives 30 minutes without use and at most a day, and counts every request once (replay window).
/// Bounded: 4 per phone (a fifth replaces the oldest), 64 in all, 240 requests a minute each. Everything lives in memory and goes with the
/// server: a restart of the remote ends all sessions.
/// </summary>
public sealed class RemoteSessions
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);
    public const int MaxPerDevice = 4;
    public const int MaxTotal = 64;
    public const int MaxRequestsPerMinute = 240;

    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>A new session for the phone; returns it with the server's nonce (to be sent back sealed under the device key).</summary>
    public (RemoteSession Session, byte[] ServerNonce) Open(string deviceId, byte[] deviceKey, byte[] clientNonce, DateTimeOffset now)
    {
        if (clientNonce.Length != RemoteSeal.NonceSize) throw new ArgumentException("nonce size");
        var serverNonce = RandomNumberGenerator.GetBytes(RemoteSeal.NonceSize);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var session = new RemoteSession(id, deviceId, RemoteSeal.SessionKey(deviceKey, clientNonce, serverNonce), now);
        lock (_lock)
        {
            PruneLocked(now);
            var own = _sessions.Values.Where(s => s.DeviceId == deviceId).OrderBy(s => s.LastSeen).ToList();
            for (var i = 0; i <= own.Count - MaxPerDevice; i++) _sessions.Remove(own[i].Id);
            if (_sessions.Count >= MaxTotal) _sessions.Remove(_sessions.Values.OrderBy(s => s.LastSeen).First().Id);
            _sessions[id] = session;
        }
        return (session, serverNonce);
    }

    /// <summary>The live session with this id, or null.</summary>
    public RemoteSession? Get(string? id, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 32) return null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var s)) return null;
            if (Expired(s, now))
            {
                _sessions.Remove(id);
                return null;
            }
            return s;
        }
    }

    /// <summary>Counts a request of the session (after its tag was checked): once per number, and not too many.</summary>
    public SessionRefusal Admit(RemoteSession session, long seq, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(session.Id, out var live) || !ReferenceEquals(live, session) || Expired(session, now)) return SessionRefusal.NoSession;
            session.Requests.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (session.Requests.Count >= MaxRequestsPerMinute) return SessionRefusal.TooOften;
            if (!session.Window.Accept(seq)) return SessionRefusal.Replay;
            session.Requests.Add(now);
            session.LastSeen = now;
            return SessionRefusal.None;
        }
    }

    /// <summary>Keeps a session alive while its event stream is open.</summary>
    public void Touch(RemoteSession session, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(session.Id) && !Expired(session, now)) session.LastSeen = now;
        }
    }

    /// <summary>Is the session still open (not run out, not dropped with its phone)?</summary>
    public bool Alive(RemoteSession session, DateTimeOffset now)
    {
        lock (_lock) return _sessions.TryGetValue(session.Id, out var s) && ReferenceEquals(s, session) && !Expired(s, now);
    }

    /// <summary>Ends every session of a phone (unpaired, its level changed).</summary>
    public void DropDevice(string deviceId)
    {
        lock (_lock)
        {
            foreach (var id in _sessions.Values.Where(s => s.DeviceId == deviceId).Select(s => s.Id).ToList()) _sessions.Remove(id);
        }
    }

    /// <summary>Ends every session whose phone is no longer paired.</summary>
    public void Keep(IReadOnlySet<string> pairedIds)
    {
        lock (_lock)
        {
            foreach (var id in _sessions.Values.Where(s => !pairedIds.Contains(s.DeviceId)).Select(s => s.Id).ToList()) _sessions.Remove(id);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _sessions.Count;
        }
    }

    private static bool Expired(RemoteSession s, DateTimeOffset now) => now - s.LastSeen > IdleLifetime || now - s.Opened > MaxLifetime;

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var id in _sessions.Values.Where(s => Expired(s, now)).Select(s => s.Id).ToList()) _sessions.Remove(id);
    }
}

/// <summary>Why the parents' code from a phone is not even passed on.</summary>
public enum ParentCodeRefusal
{
    None,
    /// <summary>The PC did not ask this phone for a code for exactly this action and entry (or that was more than two minutes ago).</summary>
    NotAsked,
    /// <summary>More than <see cref="RemoteParentCodes.MaxTries"/> tries from this phone in <see cref="RemoteParentCodes.Window"/>.</summary>
    TooOften
}

/// <summary>
/// The parents' code typed on a phone: only for what the PC just asked this phone for (the action and entry of the refusal it sent, at most
/// two minutes ago), and at most 5 tries per phone in 10 minutes, on top of the host's own lockout for the code (the same as at the TV).
/// </summary>
public sealed class RemoteParentCodes
{
    public static readonly TimeSpan AskLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaxTries = 5;
    private const int MaxDevices = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Action, string? Item, DateTimeOffset At)> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _tries = new(StringComparer.Ordinal);

    /// <summary>The PC answered this phone "the parents' code for this action and entry": the one code it may send next.</summary>
    public void Asked(string device, string action, string? item, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_asked.Count >= MaxDevices) _asked.Clear();
            _asked[device] = (action, item, now);
        }
    }

    /// <summary>May a code for this action and entry be passed on now? Counts the try; a passed try keeps the question for a retry after a wrong code.</summary>
    public ParentCodeRefusal Try(string device, string? action, string? item, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_asked.TryGetValue(device, out var asked) || now - asked.At > AskLifetime
                || !string.Equals(asked.Action, action, StringComparison.Ordinal) || !string.Equals(asked.Item, item, StringComparison.Ordinal))
                return ParentCodeRefusal.NotAsked;
            if (!_tries.TryGetValue(device, out var times))
            {
                if (_tries.Count >= MaxDevices)
                {
                    foreach (var stale in _tries.Where(t => t.Value.All(x => now - x > Window)).Select(t => t.Key).ToList()) _tries.Remove(stale);
                    if (_tries.Count >= MaxDevices) return ParentCodeRefusal.TooOften;
                }
                _tries[device] = times = [];
            }
            times.RemoveAll(t => now - t > Window);
            if (times.Count >= MaxTries) return ParentCodeRefusal.TooOften;
            times.Add(now);
            return ParentCodeRefusal.None;
        }
    }

    /// <summary>The code was right: the question is answered.</summary>
    public void Answered(string device)
    {
        lock (_lock) _asked.Remove(device);
    }
}
