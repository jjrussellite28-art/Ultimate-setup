using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Platform;
using CouchHud.Core.Updates;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the Updates page shows; sent again whenever something changes.</summary>
public sealed record UpdateState(
    IReadOnlyList<UpdateItem> Windows,
    IReadOnlyList<UpdateItem> Apps,
    bool RebootRequired,
    bool WingetAvailable,
    string? WindowsError,
    string? Busy,
    long? CheckedAt);

/// <summary>
/// Windows Update (searched in-process through the Windows Update Agent, installed
/// by the CouchHUD service) and app updates through winget.
/// </summary>
public sealed class UpdateService
{
    private const string SearchCriteria = WindowsUpdateAgent.SearchCriteria;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateState _state = new([], [], false, true, null, null, null);

    public event Action<UpdateState>? Changed;

    public UpdateState State => _state;

    private void Set(UpdateState state)
    {
        _state = state;
        Changed?.Invoke(state);
    }

    public async Task<UpdateState> CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return _state;
        try
        {
            Set(_state with { Busy = Strings.Get("update.checking") });
            var windowsTask = Task.Run(SearchWindows);
            var appsTask = SearchWingetAsync();
            await Task.WhenAll(windowsTask, appsTask);
            var (windows, windowsError) = windowsTask.Result;
            var (apps, wingetAvailable) = appsTask.Result;
            Set(new UpdateState(windows, apps, RebootRequired(), wingetAvailable, windowsError, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return _state;
        }
        finally
        {
            if (_state.Busy is not null) Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    /// <summary>Upgrades one winget package, or all with <paramref name="id"/> null; returns a line for the toast.</summary>
    public async Task<string> UpgradeAppsAsync(string? id)
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("update.busy"));
        try
        {
            var name = id is null ? Strings.Get("update.allApps") : _state.Apps.FirstOrDefault(a => a.Id == id)?.Name ?? id;
            Set(_state with { Busy = Strings.Get("update.updating", name) });
            var (exit, _) = await RunWingetAsync(Winget.UpgradeArguments(id), TimeSpan.FromMinutes(60));
            Console.Error.WriteLine($"[updates] winget upgrade {id ?? "--all"} exited with {exit}");
            return exit == 0 ? Strings.Get("update.updated", name) : Strings.Get("update.failedNamed", name, exit);
        }
        finally
        {
            Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads and installs all pending Windows updates through the CouchHUD service (no UAC prompt): the service searches and
    /// installs as SYSTEM and reports its progress as a job; no automatic restart.
    /// </summary>
    public async Task<string> InstallWindowsAsync()
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("update.busy"));
        try
        {
            Set(_state with { Busy = Strings.Get("update.installingWindows") });
            var progress = new Progress<CouchHud.Service.Contracts.JobData>(job =>
                Set(_state with { Busy = Strings.Get("update.installingWindowsPercent", Math.Clamp(job.Percent, 0, 100)) }));
            var result = await AdminService.Ops.InstallWindowsUpdatesAsync(progress);
            if (!result.Result.Ok) return $"Windows Update: {AdminService.Message(result.Result)}";
            if (result.Job is not { } job) return Strings.Get("update.noResult");
            if (job.State != "done") return $"Windows Update: {Strings.Get("update.windowsJobFailed")}";
            Set(_state with { RebootRequired = job.RebootRequired || _state.RebootRequired });
            return job.Installed == 0
                ? Strings.Get("update.noneInstalled")
                : Strings.Plural("update.installed", job.Installed) + (job.RebootRequired ? Strings.Get("update.rebootNeeded") : "");
        }
        finally
        {
            Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // winget

    private static async Task<(IReadOnlyList<UpdateItem> Items, bool Available)> SearchWingetAsync()
    {
        try
        {
            var (exit, output) = await RunWingetAsync(["upgrade", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromMinutes(3));
            var items = Winget.ParseUpgrades(output);
            if (items.Count == 0 && exit != 0) Console.Error.WriteLine($"[updates] winget upgrade exited with {exit}");
            return (items, true);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ([], false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[updates] winget timed out");
            return ([], true);
        }
    }

    private static async Task<(int Exit, string Output)> RunWingetAsync(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("winget not started");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException();
        }
        await stderr;
        return (process.ExitCode, await stdout);
    }

    // -----------------------------------------------------------------------
    // Windows Update Agent (COM, late bound)

    private static (IReadOnlyList<UpdateItem>, string?) SearchWindows()
    {
        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            session.ClientApplicationID = "CouchHud";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search(SearchCriteria);
            dynamic updates = result.Updates;
            var list = new List<UpdateItem>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = updates.Item(i);
                string id = u.Identity.UpdateID;
                string title = u.Title;
                long? size = null;
                try { size = Convert.ToInt64(u.MaxDownloadSize); } catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException) { }
                bool important = u.AutoSelectOnWebSites;
                list.Add(new UpdateItem(id, "windows", title, null, null, size, important));
            }
            return (list, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Console.Error.WriteLine($"[updates] Windows Update search failed: {ex.Message}");
            return ([], Strings.Get("update.windowsUnreachable"));
        }
    }

    private static bool RebootRequired()
    {
        try
        {
            dynamic info = CreateCom("Microsoft.Update.SystemInfo");
            return info.RebootRequired;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    private static dynamic CreateCom(string progId) => WindowsUpdateAgent.CreateCom(progId);
}
