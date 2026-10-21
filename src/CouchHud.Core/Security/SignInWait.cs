namespace CouchHud.Core.Security;

/// <summary>Where the wait for the Windows sign-in learns about the lock and the unlock: events and a poll of the session state.</summary>
public interface ISessionSource
{
    /// <summary>The session was locked (true) or unlocked (false). May come from any thread.</summary>
    event Action<bool> LockChanged;

    /// <summary>The lock state asked for right now; null when it cannot be told (then only the events count).</summary>
    bool? IsLocked();
}

public enum SignInWaitResult
{
    /// <summary>The session was locked and has been unlocked again.</summary>
    SignedIn,
    /// <summary>Windows never showed the lock within the grace time: the caller must not carry on as if the account had signed in.</summary>
    NeverLocked,
    /// <summary>The wait was cancelled.</summary>
    Cancelled
}

/// <summary>
/// The wait of the recovery for the Windows sign-in, as a plain loop over a <see cref="ISessionSource"/>: a lock has to be seen
/// first (LockWorkStation returns before the lock screen is up, so "not locked" right after the call means nothing), then the
/// unlock ends it. The session events are the quick way, the poll of the state every <c>poll</c> is the net below them: an event that
/// never arrives (a message window of a blocked thread, a missed notification) must not leave the screen black for ever.
/// </summary>
public static class SignInWait
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    /// <summary>How long Windows gets to show the lock before the wait gives up (the caller then treats it like a failed lock).</summary>
    public static readonly TimeSpan DefaultLockGrace = TimeSpan.FromSeconds(20);

    public static SignInWaitResult Wait(ISessionSource source, Func<DateTime> now, Action<TimeSpan> sleep, TimeSpan poll, TimeSpan lockGrace,
        CancellationToken cancel = default, Action<string>? log = null)
    {
        var seenLocked = false;
        var unlockedAfterLock = false;
        var gate = new object();
        void OnChanged(bool locked)
        {
            lock (gate)
            {
                if (locked) seenLocked = true;
                else if (seenLocked) unlockedAfterLock = true;
            }
        }
        source.LockChanged += OnChanged;
        try
        {
            var started = now();
            while (true)
            {
                if (cancel.IsCancellationRequested) return SignInWaitResult.Cancelled;
                bool? polled = null;
                try
                {
                    polled = source.IsLocked();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log?.Invoke($"session state not readable: {ex.Message}");
                }
                lock (gate)
                {
                    if (polled == true) seenLocked = true;
                    else if (polled == false && seenLocked) unlockedAfterLock = true;
                    if (unlockedAfterLock) return SignInWaitResult.SignedIn;
                    if (!seenLocked && now() - started >= lockGrace) return SignInWaitResult.NeverLocked;
                }
                sleep(poll);
            }
        }
        finally
        {
            source.LockChanged -= OnChanged;
        }
    }
}
