namespace CouchHud.Core.Library;

public enum CloseDecision
{
    /// <summary>Not started by CouchHud (it ran before, or the user started it): never touched.</summary>
    NotOurs,
    /// <summary>Ours and nothing in the way: ended cleanly.</summary>
    Close,
    /// <summary>Ours, but it is downloading or updating: left open (with a notice in PC mode).</summary>
    Downloading,
    /// <summary>Ours, but a game is running (a session, Steam's RunningAppID, a child process of the client): left open.</summary>
    GameRunning,
    /// <summary>Ours, but closing it is not known to be harmless (no download detection, no proof that closing only goes to the tray): left open.</summary>
    Unsafe
}

/// <summary>
/// Which game clients CouchHud started itself, and which instance of them. Only those are closed again; a client that ran when
/// CouchHud started, that the user started later, or that the user ended and started again is never ended by CouchHud.
/// When in doubt it is not closed: order gives way to safety.
/// </summary>
public sealed class ClientOwnership
{
    /// <summary>How long a client may be gone before a new instance counts as the user's, not as the client's own restart after an update.</summary>
    public static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly HashSet<string> _ranBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Instance> _started = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Instance
    {
        /// <summary>Start time of the process we saw first (null: started, not seen yet).</summary>
        public long? Start { get; set; }

        public DateTimeOffset? GoneSince { get; set; }
    }

    /// <summary>
    /// Only Steam: it has a clean end (-shutdown) and a download check. For the others nothing proves that closing ends in the tray
    /// and does not cut off a download, so CouchHud leaves them open until that is known (see <see cref="Decide"/>).
    /// </summary>
    public static bool ClosesSafely(string id) => string.Equals(id, "steam", StringComparison.OrdinalIgnoreCase);

    /// <summary>At the start: these clients were already running, they stay the user's.</summary>
    public void NoteRunningAtStart(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            foreach (var id in ids) _ranBefore.Add(id);
        }
    }

    /// <summary>CouchHud started this client (it was not running).</summary>
    public void NoteStarted(string id)
    {
        lock (_gate)
        {
            if (!_ranBefore.Contains(id)) _started[id] = new Instance();
        }
    }

    /// <summary>The client is gone for good (ended by us or the user): the next start is a new one, and an earlier run of the user no longer counts.</summary>
    public void NoteEnded(string id)
    {
        lock (_gate)
        {
            _started.Remove(id);
            _ranBefore.Remove(id);
        }
    }

    /// <summary>Light mode: CouchHud lets go of all of them.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _started.Clear();
            _ranBefore.Clear();
        }
    }

    /// <summary>
    /// A look at the client (every tick). The first process seen after our start is the one we started. Another one (or the same
    /// client after a gap) means the user ended and started it again: no longer ours. The exception is the client's own restart after
    /// an update: it comes back within <see cref="RestartGrace"/> and is started by a process of the client itself.
    /// </summary>
    /// <param name="start">Start time of the oldest running process of the client (null: not running).</param>
    /// <param name="startedByClient">The newest process was started by a process of the client (not by the shell or the Start menu).</param>
    public void Observe(string id, bool running, long? start, bool startedByClient, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_started.TryGetValue(id, out var inst)) return;
            if (!running)
            {
                inst.GoneSince ??= now;
                if (now - inst.GoneSince.Value > RestartGrace) _started.Remove(id);
                return;
            }
            var gap = inst.GoneSince;
            inst.GoneSince = null;
            if (inst.Start is null || start is null)
            {
                inst.Start = start ?? inst.Start;
                return;
            }
            if (start == inst.Start) return;
            // A different process: only the client's own restart in quick succession stays ours; not sure (no gap seen, other parent): not ours.
            if (gap is { } g && now - g <= RestartGrace && startedByClient) inst.Start = start;
            else _started.Remove(id);
        }
    }

    public bool IsOurs(string id)
    {
        lock (_gate) return _started.ContainsKey(id);
    }

    public IReadOnlyList<string> Started
    {
        get
        {
            lock (_gate) return _started.Keys.ToList();
        }
    }

    /// <summary>
    /// What happens to one client when CouchHud quits or the PC mode starts. Anything that is not clearly harmless leaves it open.
    /// </summary>
    public CloseDecision Decide(string id, bool running, bool downloading, bool gameRunning = false)
    {
        if (!running || !IsOurs(id)) return CloseDecision.NotOurs;
        if (gameRunning) return CloseDecision.GameRunning;
        if (!ClosesSafely(id)) return CloseDecision.Unsafe;
        return downloading ? CloseDecision.Downloading : CloseDecision.Close;
    }
}
