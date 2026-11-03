namespace CouchHud.Core.Remote;

/// <summary>
/// The events waiting for one phone's stream. Coalescing: per kind only the newest is kept (a phone that reads slowly gets the current
/// state, never a growing backlog), and the kinds come out in the order they first changed.
/// </summary>
public sealed class RemoteEventQueue
{
    private readonly object _lock = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private bool _closed;

    internal RemoteEventQueue(string sessionId, string deviceId, Func<RemoteLevel?> level)
    {
        SessionId = sessionId;
        DeviceId = deviceId;
        Level = level;
    }

    public string SessionId { get; }
    public string DeviceId { get; }

    /// <summary>The phone's level right now (null: no longer paired, the stream ends).</summary>
    internal Func<RemoteLevel?> Level { get; }

    public bool Closed
    {
        get
        {
            lock (_lock) return _closed;
        }
    }

    internal void Put(string type, string json)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (!_latest.ContainsKey(type)) _order.Add(type);
            _latest[type] = json;
        }
        _signal.Release();
    }

    internal void Close()
    {
        lock (_lock) _closed = true;
        _signal.Release();
    }

    /// <summary>The next event (kind and data), or null after <paramref name="wait"/> without one or when the stream is closed.</summary>
    public async Task<(string Type, string Json)?> TakeAsync(TimeSpan wait, CancellationToken cancel)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_closed) return null;
                if (_order.Count > 0)
                {
                    var type = _order[0];
                    _order.RemoveAt(0);
                    var json = _latest[type];
                    _latest.Remove(type);
                    return (type, json);
                }
            }
            if (!await _signal.WaitAsync(wait, cancel)) return null;
        }
    }
}

/// <summary>
/// The server → phone event channel: what the TV gets as events (Hud.Emit) goes to the open streams of the phones whose level is high enough
/// for that kind (<see cref="RemoteRights.EventLevel"/>). Kinds only carry what the phone's level may see; for library and downloads it is a
/// signal and the phone asks again through its gated operations. At most 2 streams per phone and 16 in all (the oldest gives way).
/// </summary>
public sealed class RemoteEventHub
{
    public const int MaxPerDevice = 2;
    public const int MaxTotal = 16;

    private readonly object _lock = new();
    private readonly List<RemoteEventQueue> _queues = [];

    public RemoteEventQueue Subscribe(string sessionId, string deviceId, Func<RemoteLevel?> level)
    {
        var queue = new RemoteEventQueue(sessionId, deviceId, level);
        lock (_lock)
        {
            _queues.RemoveAll(q => q.Closed);
            // A session has one stream (a reconnect replaces it), a phone at most MaxPerDevice, all together MaxTotal.
            foreach (var old in _queues.Where(q => q.SessionId == sessionId).ToList()) Drop(old);
            var own = _queues.Where(q => q.DeviceId == deviceId).ToList();
            for (var i = 0; i <= own.Count - MaxPerDevice; i++) Drop(own[i]);
            if (_queues.Count >= MaxTotal) Drop(_queues[0]);
            _queues.Add(queue);
        }
        return queue;
    }

    public void Unsubscribe(RemoteEventQueue queue)
    {
        lock (_lock) Drop(queue);
    }

    /// <summary>Hands an event to every stream whose phone may see its kind now; streams of phones that are gone are closed.</summary>
    public void Publish(string type, string json, string? onlyDevice = null)
    {
        if (RemoteRights.EventLevel(type) is not { } need) return;
        List<RemoteEventQueue> targets;
        lock (_lock) targets = _queues.Where(q => onlyDevice is null || q.DeviceId == onlyDevice).ToList();
        foreach (var q in targets)
        {
            var level = q.Level();
            if (level is null)
            {
                Unsubscribe(q);
                continue;
            }
            if (level >= need) q.Put(type, json);
        }
    }

    /// <summary>Ends the streams of a phone (unpaired).</summary>
    public void DropDevice(string deviceId)
    {
        lock (_lock)
        {
            foreach (var q in _queues.Where(q => q.DeviceId == deviceId).ToList()) Drop(q);
        }
    }

    /// <summary>The phones that have an open event stream right now.</summary>
    public IReadOnlySet<string> OpenDevices()
    {
        lock (_lock) return _queues.Where(q => !q.Closed).Select(q => q.DeviceId).ToHashSet(StringComparer.Ordinal);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _queues.Count(q => !q.Closed);
        }
    }

    private void Drop(RemoteEventQueue queue)
    {
        queue.Close();
        _queues.Remove(queue);
    }
}
