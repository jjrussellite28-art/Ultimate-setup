using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Shell.Services;

/// <summary>
/// CaseHUD (the LCD tool for fans and pumps) next to CouchHUD: finds it, starts it, installs and updates it
/// and relays the bridge calls of the CaseHUD page to its local API v1. CouchHUD contains none of CaseHUD's code.
/// The event stream and the gaming lease run only while there is something to tell; without CaseHUD this is
/// a file check now and then.
/// </summary>
public sealed class CaseHudManager : IDisposable
{
    private readonly ConfigStore _config;
    private readonly string _dataDir;
    private readonly Func<bool> _gameRunning;
    private readonly Action<string, object?> _emit;
    private readonly HttpClient _http;
    private readonly HttpClient _github;
    private readonly CaseHudDiscovery _discovery;
    private readonly CaseHudCapabilityStore _capabilities;
    private readonly CaseHudReleaseService _releases;
    private readonly CaseHudGamingLease _lease;
    private readonly CaseHudInstaller _installer;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private CaseHudStatus? _lastStatus;
    private CaseHudLightingState? _lighting;
    private CaseHudReleaseCheck? _release;
    private volatile string? _busy;
    private volatile string? _error;
    private bool _loopStarted;
    private bool _connected;
    private System.Threading.Timer? _leaseTimer;
    private (CaseHudInstall? Install, CaseHudCapability Capability, DateTimeOffset At)? _installState;
    private CaseHudApiInfo? _cachedInfo;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public CaseHudManager(ConfigStore config, string dataDir, Func<bool> gameRunning, Action<string, object?> emit)
    {
        _config = config;
        _dataDir = dataDir;
        _gameRunning = gameRunning;
        _emit = emit;
        // Local API: no proxy, no redirects; the timeouts are the client's own (an event stream stays open for hours).
        _http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _github = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _github.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchHud/{CatalogRules.AppVersion}");
        // Developer/test switch: a CaseHUD started with --data <dir> (e.g. --demo) keeps its api.json there, and may run from any folder.
        // Debug builds only: in a release it would let any program that sets the variable (user environment) point the
        // discovery at its own api.json and drop the check of PID and program file.
#if DEBUG
        var dataOverride = Environment.GetEnvironmentVariable("COUCHHUD_CASEHUD_DATA");
#else
        string? dataOverride = null;
#endif
        var overridden = !string.IsNullOrWhiteSpace(dataOverride);
        _discovery = new CaseHudDiscovery(overridden ? Path.Combine(dataOverride!, "api.json") : CaseHudDiscovery.DefaultApiJsonPath(),
            ProcessImage, new WindowsRegistry(), canonicalPath: CanonicalPath);
        _capabilities = SharedCapabilities(dataDir);
        _discovery.Capabilities = _capabilities;
        // Only the installed CaseHUD counts as CaseHUD (not any program that writes an api.json).
        if (!overridden) _discovery.ExpectedExe = () => _discovery.FindInstall()?.Exe;
        _releases = new CaseHudReleaseService(_github);
        _lease = new CaseHudGamingLease(CurrentClient);
        _installer = new CaseHudInstaller(_releases, new CaseHudInstallGate(), _discovery.FindInstall, CurrentClientFresh, new ShellSetupRunner(() => _discovery.FindInstall() is not null),
            StartCoreAsync, _gameRunning, () => (_config.Current.CaseHudInstallAutostart, _config.Current.CaseHudInstallFps),
            Path.Combine(_dataDir, "casehud-temp"), text => { _busy = text; Publish(); });
        // Once, here: the manager is created once (Lazy in Hud), so this cannot delete a setup that is running.
        CleanTemp();
    }

    private static CaseHudCapabilityStore? _sharedCapabilities;
    private static readonly object SharedLock = new();

    /// <summary>One store for the manager and the autostart adapter of the Hud.</summary>
    internal static CaseHudCapabilityStore SharedCapabilities(string dataDir)
    {
        lock (SharedLock) return _sharedCapabilities ??= new CaseHudCapabilityStore(CapabilityFile(dataDir));
    }

    /// <summary>Where the result of the capability probe is kept (shared with the autostart adapter in the Hud).</summary>
    internal static string CapabilityFile(string dataDir) => Path.Combine(dataDir, "casehud-capability.json");

    // -----------------------------------------------------------------------
    // Finding the process

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The image of a process; the limited right also works for a CaseHUD that runs elevated (Process.SafeHandle would be refused).</summary>
    private static string? ProcessImage(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    /// <summary>The real path of a file through an open handle (links and short names resolved); the text itself if it cannot be opened.</summary>
    internal static string CanonicalPath(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PathSafety.StripPrefix(SetupProbe.FinalPath(file.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static readonly object InstalledLock = new();
    private static (string? Exe, DateTimeOffset At) _installedExe = (null, DateTimeOffset.MinValue);

    /// <summary>
    /// The window of process <paramref name="pid"/> belongs to the installed CaseHUD: its image (canonical path) is CaseHUD's
    /// installed exe. Told by the path, not the name, so a program called CaseHUD.exe elsewhere does not count (couch lock, see LockedWindow.CaseHud).
    /// </summary>
    internal static bool IsCaseHudProcess(int pid)
    {
        string? exe;
        lock (InstalledLock)
        {
            if (DateTimeOffset.UtcNow - _installedExe.At > TimeSpan.FromSeconds(30))
            {
                var install = new CaseHudDiscovery("", _ => null, new WindowsRegistry(), canonicalPath: CanonicalPath).FindInstall();
                _installedExe = (install is null ? null : CanonicalPath(Path.GetFullPath(install.Exe)), DateTimeOffset.UtcNow);
            }
            exe = _installedExe.Exe;
        }
        if (exe is null) return false;
        var image = ProcessImage(pid);
        return image is not null && string.Equals(CanonicalPath(image), exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The running CaseHUD, looked up at most once a second (the process check is not free and the lease and the page ask often).</summary>
    private CaseHudApiInfo? CurrentInfo(bool fresh = false)
    {
        lock (_lock)
        {
            if (!fresh && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(1)) return _cachedInfo;
            var before = _cachedInfo;
            _cachedInfo = _discovery.ReadRunning();
            _cachedAt = DateTimeOffset.UtcNow;
            // A CaseHUD that is running with a valid api.json has shown that it can be controlled (the entry is rewritten at the next boot).
            if (_cachedInfo is not null && before is null && _discovery.FindInstall() is { } found && _capabilities.Get(found) == CaseHudCapability.Unknown)
            {
                _capabilities.Set(found, capable: true);
                _installState = null;
            }
            return _cachedInfo;
        }
    }

    private CaseHudClient? CurrentClient() => CurrentInfo() is { } info ? new CaseHudClient(_http, info) : null;

    /// <summary>Without the cache: for places that wait for CaseHUD to appear or go.</summary>
    private CaseHudClient? CurrentClientFresh() => CurrentInfo(fresh: true) is { } info ? new CaseHudClient(_http, info) : null;

    private CaseHudClient Client() => CurrentClient() ?? throw new CaseHudException("not_running", Strings.Get("casehud.notRunning"));

    // -----------------------------------------------------------------------
    // Game lease

    public void GameStarted(string name, int? pid)
    {
        if (!_config.Current.CaseHudGamingLease) return;
        _ = _lease.BeginAsync(name, pid);
        // Renew every 15 s; the lease itself decides whether 60 s have passed.
        _leaseTimer ??= new System.Threading.Timer(_ => _ = _lease.TickAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void GameEnded()
    {
        _ = _lease.EndAsync();
        _leaseTimer?.Dispose();
        _leaseTimer = null;
    }

    // -----------------------------------------------------------------------
    // Status

    private void StartEventLoop()
    {
        lock (_lock)
        {
            if (_loopStarted) return;
            _loopStarted = true;
        }
        var loop = new CaseHudEventLoop(_http, CurrentClient, s =>
        {
            _lastStatus = s;
            Publish();
        }, connected =>
        {
            _connected = connected;
            if (!connected) _lastStatus = null;
            Publish();
        });
        _ = Task.Run(() => loop.RunAsync(_stop.Token));
    }

    /// <summary>Install entry and probe result, kept for a few seconds: the page polls, registry and file are not read every time.</summary>
    private (CaseHudInstall? Install, CaseHudCapability Capability) InstallState()
    {
        lock (_lock)
        {
            if (_installState is { } s && DateTimeOffset.UtcNow - s.At < TimeSpan.FromSeconds(5)) return (s.Install, s.Capability);
            var install = _discovery.FindInstall();
            var capability = install is null ? CaseHudCapability.Unknown : _capabilities.Get(install);
            _installState = (install, capability, DateTimeOffset.UtcNow);
            return (install, capability);
        }
    }

    private void Publish() => _emit("caseHud", Describe());

    /// <summary>What the CaseHUD page shows: install state, running state, last status, update and progress.</summary>
    public object Describe()
    {
        var (install, capability) = InstallState();
        var release = _release?.Release;
        var running = _lastStatus is not null && _connected;
        var apiUpdate = _lastStatus?.UpdateAvailable;
        return new
        {
            installed = install is not null,
            version = install?.Version ?? "",
            // A build that was probed and does not know --background has no API: the page asks for the update instead of offering control.
            needsUpdate = install is not null && capability == CaseHudCapability.Old,
            running,
            status = running ? _lastStatus : null,
            update = new
            {
                available = _release?.UpdateAvailable == true || !string.IsNullOrEmpty(apiUpdate),
                version = _release?.UpdateAvailable == true ? release!.Version.ToString() : apiUpdate ?? "",
                notes = _release?.UpdateAvailable == true ? release!.Notes : "",
                installable = release?.Installable ?? false,
                problem = release?.Problem,
                unavailable = _release?.ErrorCode == "unavailable",
                error = _release?.ErrorCode is "unavailable" or "rateLimit" ? _release.Error : null,
                latest = release?.Version.ToString()
            },
            busy = _busy,
            error = _error,
            options = new { autostart = _config.Current.CaseHudInstallAutostart, fps = _config.Current.CaseHudInstallFps, lease = _config.Current.CaseHudGamingLease },
            unsigned = CaseHudPolicy.NeedsUnsignedConfirmation(CaseHudInfo.ReleasePublicKeyPem)
        };
    }

    // -----------------------------------------------------------------------
    // Bridge

    /// <summary>All calls of the CaseHUD page. The couch lock is checked by the caller (<see cref="CaseHudPolicy"/>).</summary>
    public async Task<object?> HandleAsync(string method, JsonElement args)
    {
        if (!CaseHudPolicy.All.Contains(method)) throw new InvalidOperationException($"unknown method {method}");
        try
        {
            var result = await DispatchAsync(method, args);
            if (method == "caseHudStatus") _error = null;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every failure ends up on the page, not only the ones of the install.
            _error = ex.Message;
            throw;
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        JsonElement Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;
        string Str(int i) => Arg(i).ValueKind == JsonValueKind.String ? Arg(i).GetString()! : throw new ArgumentException($"{method}: argument {i} must be a string");
        bool Bool(int i) => Arg(i).ValueKind == JsonValueKind.True;

        switch (method)
        {
            case "caseHudStatus":
                StartEventLoop();
                if (_lastStatus is null && CurrentClient() is { } c)
                {
                    try { _lastStatus = await c.StatusAsync(); _connected = true; }
                    catch (CaseHudException) { }
                }
                return Describe();
            case "caseHudCheckUpdate":
                _release = await _releases.CheckAsync(_discovery.FindInstall()?.Version, force: Bool(0));
                return Describe();
            case "caseHudPrepare":
            {
                // A fresh look at the release; the token is what install and update will accept, for this release only.
                var prepare = await _installer.PrepareAsync();
                var release = prepare.Release;
                _release = release is null ? _release : new CaseHudReleaseCheck(release, prepare.IsUpdate);
                return new
                {
                    token = prepare.Token,
                    version = release?.Version.ToString(),
                    installed = prepare.Installed,
                    isUpdate = prepare.IsUpdate,
                    unsigned = prepare.Unsigned,
                    notes = release?.Notes,
                    error = prepare.Error
                };
            }
            case "caseHudStart":
                await StartAsync();
                return Describe();
            case "caseHudInstall":
            case "caseHudUpdate":
                _error = null;
                await _installer.InstallAsync(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null);
                lock (_lock) _installState = null;
                _release = await _releases.CheckAsync(_discovery.FindInstall()?.Version);
                return Describe();
            case "caseHudOpenGui":
                await OpenGuiAsync();
                return null;
            case "caseHudPanels": return await Client().PanelsAsync();
            case "caseHudPatchPanel":
                // CaseHUD's refusal reaches the page as a readable, cleaned and capped text (as for lighting).
                try { return await Client().PatchPanelAsync(Str(0), CaseHudPanelPatch.FromJson(Arg(1))); }
                catch (CaseHudException ex) when (ex.Status > 0) { throw new InvalidOperationException(CaseHudLighting.ErrorText(ex)); }
            case "caseHudLighting": return await LightingAsync();
            case "caseHudPatchLighting": return await PatchLightingAsync(Arg(0));
            case "caseHudDesigns": return await Client().DesignsAsync();
            case "caseHudSets": return await Client().SetsAsync();
            case "caseHudApplySet":
            {
                var ids = Arg(2).ValueKind == JsonValueKind.Array ? Arg(2).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null;
                await Client().ApplySetAsync(Str(0), Str(1), ids is { Count: > 0 } ? ids : null);
                return null;
            }
            case "caseHudScenes": return await Client().ScenesAsync();
            case "caseHudActivateScene":
                try { await Client().ActivateSceneAsync(Str(0)); }
                catch (CaseHudException ex) when (ex.Code == "conflict") { throw new InvalidOperationException(Strings.Get("casehud.sceneConflict")); }
                return null;
            case "caseHudRotation": await Client().SetRotationAsync(Bool(0)); return null;
            case "caseHudNextScene": await Client().NextSceneAsync(); return null;
            case "caseHudPower": await Client().PowerAsync(Bool(0)); return null;
            case "caseHudIdentify": await Client().IdentifyAsync(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null); return null;
            case "caseHudCommunity": return await Client().CommunityAsync();
            case "caseHudImport": await Client().ImportCommunityAsync(Str(0)); return null;
            case "caseHudAwtrix": await Client().SetAwtrixAsync(Bool(0)); return null;
            case "caseHudPreview":
            {
                // As a data: URL: the token stays on the host side and never appears in a WebView URL.
                var size = Arg(2).ValueKind == JsonValueKind.Number ? Arg(2).GetInt32() : (int?)null;
                var png = await Client().PreviewAsync(Str(0), Str(1), size, Arg(3).ValueKind == JsonValueKind.String ? Arg(3).GetString() : null);
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            default:
                throw new InvalidOperationException($"unknown method {method}");
        }
    }

    // -----------------------------------------------------------------------
    // Lighting (CaseHUD 1.0)

    /// <summary>Only for a build whose status carries <c>lighting</c>: older ones have no endpoint and are not asked.</summary>
    private async Task<CaseHudLightingState> LightingAsync()
    {
        var client = Client();
        if (_lastStatus is null)
        {
            try { _lastStatus = await client.StatusAsync(); }
            catch (CaseHudException) { }
        }
        if (!CaseHudLighting.Supported(_lastStatus)) throw new InvalidOperationException(Strings.Get("casehud.lightingUnsupported"));
        try { return _lighting = await client.LightingAsync(); }
        catch (CaseHudException ex) when (ex.Status > 0) { throw new InvalidOperationException(CaseHudLighting.ErrorText(ex)); }
    }

    /// <summary>
    /// A change of the lighting: checked against the last state (CaseHudLighting.BuildPatch), so the page cannot send arbitrary JSON.
    /// CaseHUD answers 200 with the full state, which becomes the new one. The two refusals the user can resolve in CaseHUD's window
    /// (409 ack_required, 403 gui_only; nothing was applied) come back as { problem }; every other refusal as a readable error.
    /// </summary>
    private async Task<object?> PatchLightingAsync(JsonElement patch)
    {
        var last = _lighting ?? await LightingAsync();
        var body = CaseHudLighting.BuildPatch(patch, last);
        try
        {
            // Only an empty answer (not the documented 200 with the state) costs another read.
            _lighting = await Client().PatchLightingAsync(body) ?? await Client().LightingAsync();
            return new { state = _lighting };
        }
        catch (CaseHudException ex) when (CaseHudLighting.Problem(ex) is { } problem)
        {
            return new { problem };
        }
        catch (CaseHudException ex) when (ex.Status > 0)
        {
            // A 400 usually means the state moved on (a device went away, lighting was switched off): the next check uses the fresh one.
            if (ex.Status == 400)
            {
                try { _lighting = await Client().LightingAsync(); }
                catch (CaseHudException) { }
            }
            throw new InvalidOperationException(CaseHudLighting.ErrorText(ex));
        }
    }

    // -----------------------------------------------------------------------
    // Start, GUI

    private async Task StartAsync()
    {
        await StartCoreAsync();
        try { _lastStatus = await Client().StatusAsync(); _connected = true; }
        catch (CaseHudException ex) { _error = ex.Message; }
        Publish();
    }

    private async Task StartCoreAsync()
    {
        var install = _discovery.FindInstall() ?? throw new CaseHudException("not_installed", Strings.Get("casehud.notInstalled"));
        StartEventLoop();
        // Without a taskbar (CouchHUD as the shell) CaseHUD has no tray to put its icon on.
        await _discovery.StartAsync(install, (exe, arguments) =>
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = install.Directory });
            // Gone within a second: the flag was not known (exit code 2) or it could not run; the discovery tries the next form.
            return process is not null && process.WaitForExit(1000) ? process.ExitCode : null;
        }, noTray: !ShellManager.IsDesktopShellRunning());
        lock (_lock)
        {
            _cachedAt = DateTimeOffset.MinValue;
            _installState = null;
        }
    }

    /// <summary>CaseHUD's own window on the PC (design editor): the API opens it and puts it in front; older versions get started again instead.</summary>
    private async Task OpenGuiAsync()
    {
        var install = _discovery.FindInstall() ?? throw new CaseHudException("not_installed", Strings.Get("casehud.notInstalled"));
        // Running CaseHUD with the endpoint: let it take the foreground, then ask it to show its window.
        if (CurrentInfo(fresh: true) is { } info)
        {
            Native.NativeMethods.AllowSetForegroundWindow(info.Pid);
            try
            {
                await new CaseHudClient(_http, info).OpenGuiAsync();
                return;
            }
            catch (CaseHudException ex) when (ex.Status == 503) { throw new InvalidOperationException(Strings.Get("casehud.guiUnavailable")); }
            catch (CaseHudException ex) when (ex.Status is 404 or 405) { /* older CaseHUD: fall back to starting the exe */ }
        }
        Process.Start(new ProcessStartInfo(install.Exe) { UseShellExecute = false, WorkingDirectory = install.Directory })?.Dispose();
    }

    // -----------------------------------------------------------------------
    // The setup: updates through the service, the first installation with UAC

    /// <summary>
    /// An update of an installed CaseHUD goes to the CouchHUD service (owner decision E7, P0 spike): the verified bytes are streamed, the
    /// service verifies them again against the built-in CaseHUD key and runs the setup itself (no UAC prompt); CaseHUD is started again
    /// in this session afterwards by CouchHUD (CaseHudInstaller; the setup ends CaseHUD for the update, the job says RestartCaseHud). The first installation stays with Windows' UAC
    /// prompt (it needs the user's choices and is a deliberate decision of an administrator), as does a release without a signature
    /// (the service accepts signed setups only): the real path of the held file is checked first, then exactly that one is started.
    /// </summary>
    private sealed class ShellSetupRunner(Func<bool> installed) : ICaseHudSetupRunner
    {
        public async Task<int> RunAsync(VerifiedSetup setup, string arguments, Action<string> step, CancellationToken ct)
        {
            if (installed() && setup.ChecksumFile is not null && !string.IsNullOrEmpty(setup.Signature))
            {
                step(Strings.Get("casehud.installing", "{version}"));
                var header = new CouchHud.Service.Contracts.SetupStreamArgs(Path.GetFileName(setup.Path), setup.Length, Convert.ToBase64String(setup.ChecksumFile), setup.Signature);
                var result = await AdminService.Ops.InstallCaseHudAsync(header, setup.ReadFromStart(), null, ct);
                AdminService.ThrowIfFailed(result.Result);
                if (result.Job is { State: "failed" }) throw new InvalidOperationException(Strings.Get("casehud.serviceFailed"));
                return 0;
            }
            string startPath;
            try
            {
                var final = SetupProbe.FinalPath(setup.Handle);
                PathSafety.RequireSamePath(setup.Path, final);
                startPath = PathSafety.StripPrefix(final);
                PathSafety.RequireNoReparsePoints(startPath);
            }
            catch (Exception ex) when (ex is CatalogException or IOException)
            {
                Console.Error.WriteLine($"[casehud] rejected: {ex.Message}");
                throw new InvalidOperationException(ex.Message);
            }

            step(Strings.Get("admin.waiting"));
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(startPath, arguments) { UseShellExecute = true, Verb = "runas" })
                          ?? throw new InvalidOperationException(Strings.Get("selfupdate.setupNotStarted"));
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new SetupDeclinedException();
            }

            using (process)
            {
                step(Strings.Get("casehud.installing", "{version}"));
                if (!await Task.Run(() => process.WaitForExit(TimeSpan.FromMinutes(10)), ct))
                    throw new InvalidOperationException(Strings.Get("selfupdate.slow"));
                return process.ExitCode;
            }
        }
    }

    private void CleanTemp()
    {
        try
        {
            var dir = Path.Combine(_dataDir, "casehud-temp");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _leaseTimer?.Dispose();
        // End a running lease (CaseHUD would otherwise keep the gaming scene for up to two more minutes), but never wait long.
        try { Task.Run(() => _lease.EndAsync(TimeSpan.FromMilliseconds(1500))).Wait(TimeSpan.FromMilliseconds(2000)); }
        catch (AggregateException) { }
        _http.Dispose();
        _github.Dispose();
    }
}
