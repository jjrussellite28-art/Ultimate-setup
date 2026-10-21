using CouchHud.Core.Library;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>What the runner needs to know about Steam and the couch, and how it asks Steam to end or start. Faked in tests.</summary>
public interface ISteamEnvironment
{
    /// <summary>Main Steam folder, null without Steam.</summary>
    string? SteamRoot { get; }

    IReadOnlyList<string> UserIds { get; }

    /// <summary>steam.exe runs.</summary>
    bool SteamRunning { get; }

    /// <summary>steam.exe or one of its web helpers still runs (they end a moment after steam.exe).</summary>
    bool ProcessesRunning { get; }

    /// <summary>How long steam.exe has been running (null: not running).</summary>
    TimeSpan? SteamAge { get; }

    /// <summary>A game is on or starting (CouchHud's own sessions, or Steam's RunningAppID while steam.exe runs).</summary>
    bool GameBusy { get; }

    /// <summary>The couch is in use for a client: controller as mouse, a login in a client.</summary>
    bool ClientBusy { get; }

    /// <summary>A Steam window is in front.</summary>
    bool SteamInForeground { get; }

    /// <summary>Steam is downloading or updating something.</summary>
    bool DownloadActive { get; }

    /// <summary>Nobody is signed in to Steam (its login or Steam Guard dialog is up).</summary>
    bool LoginOpen { get; }

    /// <summary>Asks the running Steam to end (steam -shutdown). Never forces it.</summary>
    void RequestShutdown();

    /// <summary>Starts Steam quietly in the background (steam -silent).</summary>
    void StartSilent();

    void Sleep(TimeSpan time);

    /// <summary>The clock the waiting loops follow (a test moves it with <see cref="Sleep"/>).</summary>
    DateTime Now { get; }
}

/// <summary>
/// The Steam settings for the couch (see <see cref="SteamGuard"/>) applied and put back, with the rules for when Steam may be touched.
/// Steam writes its files itself, so values go in only while it is not running. Steam is restarted actively only to apply, and only
/// at a really quiet moment (<see cref="Quiet"/>, checked again right before it is asked to end). Putting back never restarts anything:
/// it happens as soon as Steam is not running, the Run entry stays until it is done and verified.
/// </summary>
public sealed class SteamGuardRunner(
    IRegistry registry,
    IVdfStore vdf,
    ISteamEnvironment env,
    string dataDir,
    string? runCommand,
    Action<string>? log = null)
{
    public const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "CouchHudReleaseSteamGuard";
    public const string MarkerFile = "guard-steam.restart";

    private readonly object _state = new();
    private readonly HashSet<string> _overruled = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _retryAt;
    private int _failures;
    private bool _giveUp;
    private volatile bool _deferred;

    public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownWait { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan HelperWait { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan VerifyDelay { get; set; } = TimeSpan.FromSeconds(4);
    public int MaxFailures { get; set; } = 5;

    /// <summary>Something had to wait (a game, a busy moment, Steam still running for a restore, a Steam that did not end).</summary>
    public bool Deferred => _deferred;

    public bool RetryDue => _deferred && env.Now >= _retryAt;

    private string StatePath => Path.Combine(dataDir, SteamGuard.StateFile);
    private string Marker => Path.Combine(dataDir, MarkerFile);

    private GuardSnapshot Snapshot() => new(registry, vdf, StatePath, m => log?.Invoke(m));

    public bool HasPending => Snapshot().HasPending;

    /// <summary>Nothing is playing, nothing is in front, nobody is logging in, nothing is downloading.</summary>
    public bool Quiet() => QuietReason() is null;

    private string? QuietReason() =>
        env.GameBusy ? "a game is on"
        : env.ClientBusy ? "a client is in use"
        : env.SteamInForeground ? "Steam is in front"
        : env.LoginOpen ? "Steam is at its login"
        : env.DownloadActive ? "Steam is downloading"
        : null;

    /// <summary>
    /// One pass. <paramref name="want"/>: Steam as the couch wants it, else as the user had it. <paramref name="allowCycle"/> only
    /// matters when applying. <paramref name="quick"/> (quit): no waiting and no verification pause.
    /// </summary>
    public void Sync(bool want, bool allowCycle = true, bool quick = false)
    {
        lock (_state)
        {
            var root = env.SteamRoot;
            var snapshot = Snapshot();
            if (!want)
            {
                _overruled.Clear(); // a new couch session starts afresh
                _giveUp = false;
            }
            CleanTemp(root);

            // We asked Steam to end earlier and it came back (the user started it): nothing of ours to start.
            var markerSet = File.Exists(Marker);
            if (markerSet && env.SteamRunning)
            {
                TryDelete(Marker);
                markerSet = false;
            }

            IReadOnlyList<GuardChange> changes = [];
            bool needs;
            if (want)
            {
                if (root is null || _giveUp)
                {
                    RestartIfWeEndedIt(markerSet);
                    return;
                }
                // A value of ours that is not ours any more: Steam or the user changed it. Respected for this couch session.
                foreach (var e in snapshot.Entries.Where(e => e.OnlyIfUnchanged && !snapshot.StillApplied(e))) _overruled.Add(Key(e.Kind, e.Location, e.Name));
                changes = SteamGuard.Changes(root, env.UserIds, vdf, registry).Where(c => !_overruled.Contains(Key(c.Kind, c.Location, c.Name))).ToList();
                needs = SteamGuard.WouldChange(changes, vdf, registry);
            }
            else
            {
                needs = snapshot.HasPending && SteamGuard.WouldRestore(snapshot.Entries, vdf, registry);
            }

            if (!needs)
            {
                // Entries that are not ours any more are only bookkeeping: clear them.
                if (!want && snapshot.HasPending)
                {
                    snapshot.Restore(); // only entries that are not ours any more are left: bookkeeping
                    SyncRunEntry(snapshot);
                }
                _deferred = false;
                RestartIfWeEndedIt(markerSet);
                return;
            }

            if (!env.SteamRunning)
            {
                WriteAndVerify(want, snapshot, changes, quick);
                RestartIfWeEndedIt(markerSet);
                return;
            }

            // Steam runs.
            if (!want)
            {
                Wait(TimeSpan.FromSeconds(20), "the settings are put back when Steam has ended");
                return;
            }
            if (!allowCycle)
            {
                Wait(TimeSpan.FromSeconds(90), "no restart now");
                return;
            }
            if (QuietReason() is { } reason)
            {
                Wait(TimeSpan.FromSeconds(90), reason);
                return;
            }
            Cycle(snapshot, changes);
        }
    }

    private void Cycle(GuardSnapshot snapshot, IReadOnlyList<GuardChange> changes)
    {
        // A Steam that has only just started is still loading: let it settle.
        var settleEnd = env.Now + Settle + TimeSpan.FromSeconds(5);
        while (env.SteamAge is { } age && age < Settle && env.Now < settleEnd) env.Sleep(TimeSpan.FromSeconds(1));
        // The moment may have changed while waiting: look again, directly before asking.
        if (!env.SteamRunning)
        {
            WriteAndVerify(true, snapshot, changes, false);
            return;
        }
        if (QuietReason() is { } reason)
        {
            Wait(TimeSpan.FromSeconds(90), reason);
            return;
        }
        log?.Invoke("ending Steam for a moment");
        // Remembered before asking: even a Steam that ends late (after the wait) gets started again by whoever finds it off.
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Marker, env.Now.ToString("O"));
        env.RequestShutdown();
        var deadline = env.Now + ShutdownWait;
        while (env.SteamRunning && env.Now < deadline) env.Sleep(TimeSpan.FromMilliseconds(500));
        if (env.SteamRunning)
        {
            log?.Invoke("Steam did not end in time, left alone");
            Fail();
            return;
        }
        var helpers = env.Now + HelperWait;
        while (env.ProcessesRunning && env.Now < helpers) env.Sleep(TimeSpan.FromMilliseconds(500));
        WriteAndVerify(true, snapshot, changes, false);
        RestartIfWeEndedIt(true);
    }

    /// <summary>Steam was ended by us and is off: start it quietly again (also when the write failed: leave Steam as it was).</summary>
    private void RestartIfWeEndedIt(bool markerSet)
    {
        if (!markerSet || env.SteamRunning) return;
        try
        {
            env.StartSilent();
        }
        finally
        {
            TryDelete(Marker);
        }
    }

    private bool WriteAndVerify(bool want, GuardSnapshot snapshot, IReadOnlyList<GuardChange> changes, bool quick)
    {
        var before = snapshot.Entries.ToList();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var errors = want ? (changes.Count > 0 ? snapshot.Apply(changes) : []) : snapshot.Restore();
            if (!quick) env.Sleep(VerifyDelay);
            // Steam may have started between the check and the write and written its own state over ours.
            var held = want ? !SteamGuard.WouldChange(changes, vdf, registry) : !SteamGuard.WouldRestore(before, vdf, registry);
            if (held && errors.Count == 0)
            {
                SyncRunEntry(snapshot);
                _failures = 0;
                _deferred = false;
                log?.Invoke($"{(want ? "applied" : "restored")}, pending {snapshot.Entries.Count}");
                return true;
            }
            if (env.SteamRunning)
            {
                if (!want) snapshot.Reinstate(before.Where(e => SteamGuard.WouldRestore([e], vdf, registry)));
                break;
            }
        }
        SyncRunEntry(snapshot);
        Fail();
        return false;
    }

    /// <summary>The Run entry follows the snapshot: there while something of ours is in Steam's files, gone once everything is back and verified.</summary>
    private void SyncRunEntry(GuardSnapshot snapshot)
    {
        if (snapshot.HasPending) WriteRunEntry();
        else registry.DeleteValue(RunKey, RunValue);
    }

    private void WriteRunEntry()
    {
        if (!string.IsNullOrEmpty(runCommand)) registry.WriteString(RunKey, RunValue, runCommand);
    }

    private void Wait(TimeSpan retryIn, string why)
    {
        _deferred = true;
        _retryAt = env.Now + retryIn;
        log?.Invoke($"waiting: {why}");
    }

    private void Fail()
    {
        _failures++;
        _deferred = true;
        // 90 s, 4.5 min, 13.5 min, then every 30 min; after MaxFailures not again in this couch session.
        _retryAt = env.Now + TimeSpan.FromSeconds(Math.Min(90 * Math.Pow(3, _failures - 1), 1800));
        if (_failures >= MaxFailures)
        {
            _giveUp = true;
            _deferred = false;
            log?.Invoke("giving up for this couch session");
        }
    }

    /// <summary>
    /// For the Run entry (a short-lived process): put back as soon as Steam is not running, waiting for that at most
    /// <paramref name="maxWait"/>. Never ends or starts Steam on its own accord.
    /// </summary>
    public void ReleaseWhenSteamEnds(TimeSpan maxWait, TimeSpan poll)
    {
        var deadline = env.Now + maxWait;
        while (true)
        {
            Sync(false, allowCycle: false);
            if (!HasPending) return;
            if (env.Now >= deadline) return;
            env.Sleep(poll);
        }
    }

    private static string Key(GuardKind kind, string location, string name) => $"{kind}|{location}|{name}";

    private void CleanTemp(string? root)
    {
        if (root is null || env.SteamRunning) return;
        foreach (var id in env.UserIds)
        {
            var tmp = SteamGuard.LocalConfigPath(root, id) + ".couchhud.tmp";
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
