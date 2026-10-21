using System.Diagnostics;
using System.Text.RegularExpressions;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;

namespace CouchHud.Shell.Services;

/// <summary>
/// Puts Steam's own settings in CouchHud's favour on the couch and back again (rules in <see cref="SteamGuardRunner"/>, what and why
/// in <see cref="SteamGuard"/>). This class is the thread, the lock between processes and the real Steam: the UI never waits for it.
/// Applying may end Steam cleanly (steam -shutdown, never killed) at a quiet moment and start it again quietly; putting back never
/// restarts Steam, it waits until Steam is not running. While values are changed, a Run entry of the user starts CouchHud with
/// <see cref="ReleaseSwitch"/> (it stays until everything is back and verified).
/// </summary>
public sealed class SteamGuardService(string dataDir, Func<bool> gameBusy, Func<bool>? clientBusy = null)
{
    public const string ReleaseSwitch = "--release-steam-guard";

    private const string MutexName = @"Local\CouchHud.SteamGuard";

    private readonly object _gate = new();
    private (bool Want, bool Cycle)? _next;
    private bool _running;
    private Task _worker = Task.CompletedTask;
    private readonly SteamGuardRunner _runner = CreateRunner(dataDir, gameBusy, clientBusy);
    private readonly ISteamEnvironment _steam = new SteamEnvironment(gameBusy, clientBusy);

    /// <summary>Steam is downloading or updating something right now.</summary>
    public bool SteamDownloading => _steam.DownloadActive;

    /// <summary>A game is on: one of CouchHud's sessions or Steam's RunningAppID (only believed while steam.exe runs).</summary>
    public bool SteamGameRunning => _steam.GameBusy;

    /// <summary>Asks the running Steam to end (steam -shutdown, never forced) and returns at once.</summary>
    public void EndSteam() => _steam.RequestShutdown();

    /// <summary>Waits (at most <paramref name="max"/>) until Steam has ended after <see cref="EndSteam"/>; false if it still runs.</summary>
    public bool WaitSteamEnded(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (_steam.ProcessesRunning && DateTime.UtcNow < until) Thread.Sleep(250);
        return !_steam.ProcessesRunning;
    }

    /// <summary>
    /// CouchHud is about to start Steam itself (Steam is not running): its settings go in first, so Steam starts the way the couch
    /// wants it and never has to be restarted for that. Quick and bounded; never touches a running Steam.
    /// </summary>
    public void PrepareForStart() => Run(want: true, allowCycle: false, quick: true, TimeSpan.FromSeconds(3));

    private static SteamGuardRunner CreateRunner(string dataDir, Func<bool> gameBusy, Func<bool>? clientBusy)
    {
        var exe = Environment.ProcessPath;
        return new SteamGuardRunner(new WindowsRegistry(), new FileVdfStore(), new SteamEnvironment(gameBusy, clientBusy), dataDir,
            string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" {ReleaseSwitch}", m => Console.WriteLine($"[steam-guard] {m}"));
    }

    /// <summary>Something had to wait: the next quiet moment (or the end of Steam) should ask again.</summary>
    public bool Deferred => _runner.Deferred;

    /// <summary>Waiting and the pause since the last try is over.</summary>
    public bool RetryDue => _runner.RetryDue && !_busy;

    private volatile bool _busy;

    /// <summary>The wish is queued; the latest one wins. <paramref name="want"/>: Steam's settings as CouchHud wants them, else the user's own.</summary>
    public void Request(bool want, bool allowCycle = true)
    {
        lock (_gate)
        {
            _next = (want, allowCycle);
            if (_running) return;
            _running = true;
            _worker = Task.Run(Loop);
        }
    }

    /// <summary>Completes when no queued work is left.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _running ? _worker : Task.CompletedTask;
    }

    private void Loop()
    {
        while (true)
        {
            (bool Want, bool Cycle) job;
            lock (_gate)
            {
                if (_next is not { } next)
                {
                    _running = false;
                    return;
                }
                job = next;
                _next = null;
            }
            Run(job.Want, job.Cycle, quick: false, TimeSpan.FromMinutes(3));
        }
    }

    /// <summary>
    /// Quit and sign-out: puts back what can be put back right now (Steam is not running) and leaves the rest to the Run entry. Takes
    /// the lock only briefly, waits for nothing and never restarts Steam.
    /// </summary>
    public void ReleaseOnQuit() => Run(want: false, allowCycle: false, quick: true, TimeSpan.FromSeconds(2));

    private void Run(bool want, bool allowCycle, bool quick, TimeSpan lockWait)
    {
        Mutex? mutex = null;
        var held = false;
        try
        {
            mutex = new Mutex(false, MutexName);
            try
            {
                held = mutex.WaitOne(lockWait);
            }
            catch (AbandonedMutexException)
            {
                held = true; // a process that held it ended: the state files are consistent (written before any change)
            }
            if (!held)
            {
                Console.WriteLine("[steam-guard] another process is at work, skipped");
                return;
            }
            _busy = true;
            _runner.Sync(want, allowCycle, quick);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[steam-guard] {ex.Message}");
        }
        finally
        {
            _busy = false;
            if (held) mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// For the Run entry (a short-lived process, only when no CouchHud runs): puts back as soon as Steam is not running. A Steam that
    /// starts with Windows is waited out in the background, for two hours at most, then the next sign-in tries again. Steam is never ended here.
    /// </summary>
    public static void ReleaseFromRunEntry(string dataDir)
    {
        var service = new SteamGuardService(dataDir, () => false);
        var deadline = DateTime.UtcNow + TimeSpan.FromHours(2);
        // The lock is held per pass only: a CouchHud that starts meanwhile is not kept waiting.
        while (true)
        {
            service.Run(want: false, allowCycle: false, quick: false, TimeSpan.FromSeconds(30));
            if (!service._runner.HasPending || DateTime.UtcNow >= deadline) return;
            Thread.Sleep(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>The user's Run entry is gone (uninstall); the snapshot file stays for a later start.</summary>
    public static void RemoveRunEntry() => new WindowsRegistry().DeleteValue(SteamGuardRunner.RunKey, SteamGuardRunner.RunValue);

    /// <summary>The real Steam on this PC.</summary>
    private sealed class SteamEnvironment(Func<bool> gameBusy, Func<bool>? clientBusy) : ISteamEnvironment
    {
        private static readonly Regex LoginTitle = new("Anmeld|Sign ?in|Log ?in|Steam Guard", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly WindowsRegistry _registry = new();

        public string? SteamRoot
        {
            get
            {
                var path = _registry.ReadString(SteamGuard.RegistryKey, "SteamPath")?.Trim().Trim('"').Replace('/', '\\');
                return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
            }
        }

        private string? Exe
        {
            get
            {
                var exe = _registry.ReadString(SteamGuard.RegistryKey, "SteamExe")?.Trim().Trim('"').Replace('/', '\\');
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
                var fallback = SteamRoot is { } root ? Path.Combine(root, "steam.exe") : null;
                return fallback is not null && File.Exists(fallback) ? fallback : null;
            }
        }

        public IReadOnlyList<string> UserIds
        {
            get
            {
                if (SteamRoot is not { } root) return [];
                var userdata = Path.Combine(root, "userdata");
                try
                {
                    return Directory.Exists(userdata)
                        ? SteamGuard.UserIds(Directory.EnumerateDirectories(userdata).Select(d => Path.GetFileName(d)))
                        : [];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            }
        }

        public bool SteamRunning => LauncherControl.IsRunning("steam");

        public bool ProcessesRunning => SteamRunning || LauncherControl.IsRunning("steamwebhelper");

        public TimeSpan? SteamAge
        {
            get
            {
                TimeSpan? youngest = null;
                foreach (var p in Process.GetProcessesByName("steam"))
                {
                    using (p)
                    {
                        try
                        {
                            var age = DateTime.Now - p.StartTime;
                            if (youngest is null || age < youngest) youngest = age;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                }
                return youngest;
            }
        }

        // Steam names the running game in the registry (0: none); after a crash the value is stale, so only believed while steam.exe runs.
        public bool GameBusy => gameBusy() || SteamRunning && _registry.ReadDword(SteamGuard.RegistryKey, "RunningAppID") is > 0;

        public bool ClientBusy => clientBusy?.Invoke() == true;

        public bool SteamInForeground
        {
            get
            {
                var pid = LauncherControl.ForegroundPid();
                if (pid == 0) return false;
                foreach (var name in LauncherControl.ClientProcesses("steam.exe"))
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            if (p.Id == pid) return true;
                        }
                    }
                }
                return false;
            }
        }

        public bool DownloadActive
        {
            get
            {
                try
                {
                    if (SteamRoot is { } root)
                    {
                        var downloading = Path.Combine(root, "steamapps", "downloading");
                        // Leftovers of a paused download do not count: an active one writes all the time.
                        // Top level only (cheap): the folder itself and what is directly in it, newest change within three minutes.
                        if (Directory.Exists(downloading)
                            && Directory.EnumerateFileSystemEntries(downloading).Append(downloading).Take(200)
                                .Any(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(3)))
                            return true;
                    }
                    foreach (var app in _registry.SubKeys(SteamGuard.RegistryKey + @"\Apps"))
                    {
                        if (_registry.ReadDword(SteamGuard.RegistryKey + @"\Apps\" + app, "Updating") is 1) return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                return false;
            }
        }

        public bool LoginOpen
        {
            get
            {
                // Nobody signed in: Steam sits at its login (or Steam Guard) dialog.
                if (_registry.ReadDword(SteamGuard.RegistryKey + @"\ActiveProcess", "ActiveUser") is 0) return true;
                foreach (var p in Process.GetProcessesByName("steam"))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero && LoginTitle.IsMatch(p.MainWindowTitle)) return true;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                }
                return false;
            }
        }

        public void RequestShutdown()
        {
            if (Exe is not { } exe) return;
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" })?.Dispose();
        }

        public void StartSilent()
        {
            if (Exe is not { } exe) return;
            try
            {
                LauncherControl.Start(exe, "-silent", minimized: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"[steam-guard] Steam did not start again: {ex.Message}");
            }
        }

        public void Sleep(TimeSpan time) => Thread.Sleep(time);

        public DateTime Now => DateTime.UtcNow;
    }
}
