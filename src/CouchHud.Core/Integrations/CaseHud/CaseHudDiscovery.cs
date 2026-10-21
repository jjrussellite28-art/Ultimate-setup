using System.Text.Json;
using CouchHud.Core.Localization;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Finds CaseHUD: the running instance through its api.json (which can be stale after a crash, so the process behind
/// the pid must really run the announced exe) and the installation through its uninstall entry.
/// </summary>
public sealed class CaseHudDiscovery(string apiJsonPath, Func<int, string?> processImage, IRegistry registry, Func<string, bool>? fileExists = null, Func<string, string>? canonicalPath = null)
{
    /// <summary>
    /// When set, the running process must be the installed CaseHUD: its image has to be the exe this returns (null or empty = not
    /// installed = nothing matches). A program that merely writes a convincing api.json is not CaseHUD. Left unset for tests and
    /// for a CaseHUD started by hand from another folder.
    /// </summary>
    public Func<string?>? ExpectedExe { get; set; }

    /// <summary>Remembers per CaseHUD.exe build whether it knows <c>--background</c>; unset = every start probes.</summary>
    public CaseHudCapabilityStore? Capabilities { get; set; }

    public const int StartWaitSeconds = 15;

    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;

    /// <summary>%APPDATA%\casehud\api.json.</summary>
    public static string DefaultApiJsonPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "casehud", "api.json");

    /// <summary>The running CaseHUD, or null (no file, broken file, wrong API version, dead pid, pid runs another program).</summary>
    public CaseHudApiInfo? ReadRunning()
    {
        CaseHudApiInfo? info;
        try
        {
            if (!File.Exists(apiJsonPath)) return null;
            var json = File.ReadAllText(apiJsonPath);
            if (json.Length > 16384) return null;
            info = JsonSerializer.Deserialize<CaseHudApiInfo>(json, CaseHudJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (info is null || info.ApiVersion != CaseHudInfo.ApiVersion) return null;
        if (info.Port is < 1 or > 65535 || info.Token.Length < 8 || info.Pid <= 0 || string.IsNullOrWhiteSpace(info.Exe)) return null;
        var image = processImage(info.Pid);
        if (image is null) return null;
        try
        {
            if (!SamePath(image, info.Exe)) return null;
            if (ExpectedExe is { } expected && (expected() is not { Length: > 0 } installed || !SamePath(image, installed))) return null;
            return info;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Same file; with <c>canonicalPath</c> (the shell resolves links and 8.3 names through the file handle) not just the same text.</summary>
    private bool SamePath(string a, string b)
    {
        string Normal(string path)
        {
            var full = Path.GetFullPath(path);
            return canonicalPath?.Invoke(full) ?? full;
        }
        return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installation (uninstall entry with an existing exe), or null.</summary>
    public CaseHudInstall? FindInstall()
    {
        var values = registry.ReadValues(CaseHudInfo.UninstallKey);
        if (!values.TryGetValue("InstallLocation", out var dir) || string.IsNullOrWhiteSpace(dir)) return null;
        string exe;
        try { exe = Path.Combine(dir.Trim().Trim('"'), CaseHudInfo.ExeName); }
        catch (ArgumentException) { return null; }
        if (!_exists(exe)) return null;
        values.TryGetValue("DisplayVersion", out var version);
        return new CaseHudInstall(exe, Path.GetDirectoryName(exe)!, version ?? "");
    }

    /// <summary>
    /// Starts <c>CaseHUD.exe --background</c> (with <paramref name="noTray"/> also <c>--no-tray</c>: there is no taskbar to put an
    /// icon on; a running instance just keeps running) and waits up to 15 s for a valid api.json. <paramref name="start"/> starts
    /// the program and returns its exit code when it ended within about a second, otherwise null.
    /// Capability is found out by trying, not read off the version: a build that ends at once with code 2 on every
    /// <c>--background</c> form does not know the flag, counts as old (remembered for this exe build), is only started with
    /// <c>--minimized</c> and the caller is told to update. A build whose api.json appears is remembered as capable.
    /// </summary>
    public async Task<CaseHudApiInfo> StartAsync(CaseHudInstall install, Func<string, string, int?> start, Func<TimeSpan, CancellationToken, Task>? delay = null, bool noTray = false, CancellationToken ct = default)
    {
        if (ReadRunning() is { } running) return running;
        if (Capabilities?.Get(install) == CaseHudCapability.Old) return OldBuild(install, start);
        var attempts = noTray ? new[] { "--background --no-tray", "--background" } : ["--background"];
        var unknownFlag = true;
        foreach (var arguments in attempts)
        {
            var exit = Launch(start, install.Exe, arguments);
            // Ended at once without announcing itself: the next form follows (a build that only lacks --no-tray still gets --background).
            if (exit is not null && ReadRunning() is null)
            {
                unknownFlag &= exit == UnknownFlagExitCode;
                continue;
            }
            unknownFlag = false;
            break;
        }
        if (unknownFlag) return OldBuild(install, start);
        delay ??= Task.Delay;
        var step = TimeSpan.FromMilliseconds(250);
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(StartWaitSeconds); waited += step)
        {
            await delay(step, ct);
            if (ReadRunning() is { } info)
            {
                Capabilities?.Set(install, capable: true);
                return info;
            }
        }
        throw new CaseHudException("start_timeout", Strings.Get("casehud.startTimeout"));
    }

    /// <summary>Exit code of a program that is given a flag it does not know.</summary>
    public const int UnknownFlagExitCode = 2;

    private CaseHudApiInfo OldBuild(CaseHudInstall install, Func<string, string, int?> start)
    {
        Capabilities?.Set(install, capable: false);
        Launch(start, install.Exe, "--minimized");
        throw new CaseHudException("needs_update", Strings.Get("casehud.needsUpdate"));
    }

    /// <summary>One start; a program that cannot be started counts as ended at once (-1).</summary>
    private static int? Launch(Func<string, string, int?> start, string exe, string arguments)
    {
        try
        {
            return start(exe, arguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return -1;
        }
    }
}
