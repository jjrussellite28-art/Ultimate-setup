# Called by CouchHud.iss (PrepareToInstall), elevated, possibly in another session than CouchHUD (remote
# maintenance, software distribution): ends every program from the install folder - CouchHUD and its windowless
# guard, which are the same CouchHud.exe - in all sessions, notes who ran it (restart-couchhud.ps1 needs that),
# and waits until the files can be replaced. Exit code 0: free, 1: something is still locked (setup goes on).
# CouchHud.Service ({app}\service) is not touched here: the setup stops it through the SCM right after this script (killing it
# would only make the SCM restart it). -KeepSession: the session of whoever started the update through the service. The setup
# then runs as SYSTEM in session 0, so "the setup's own session" below would not spare that user's session.
# -ServiceLeftovers: called again once the SCM reports the service stopped. Ends whatever still runs from {app}\service (a child the
# service left behind), never the SCM itself; each process is checked on the handle that ends it (same image path, same start
# time as listed, like SafeTermination), so a reused process id hits nothing. The state file is not touched in this mode.
param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$StateFile,
    [int]$MaxSeconds = 15,
    [int]$KeepSession = -1,
    [switch]$ServiceLeftovers
)
$ErrorActionPreference = 'Stop'
$prefix = ($AppDir.TrimEnd('\') + '\').ToLowerInvariant()   # "c:\program files\couchhud\"
$servicePrefix = $prefix + 'service\'
# The service's data folder: a setup the service started runs from its staging folder (Inno's second stage from the tmp folder next
# to it). That is the running setup itself, never a leftover to end. (Still true since data is SYSTEM and Administrators only: the
# setups run from there all the same; the exclusion only spares them, it gives no user anything.)
$dataPrefix = $servicePrefix + 'data\'

function Test-Ours([string]$Path) {
    $p = $Path.ToLowerInvariant()
    $p.StartsWith($prefix) -and -not $p.StartsWith($servicePrefix)
}

if ($ServiceLeftovers) {
    Add-Type -Namespace CouchHud -Name Leftover -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(System.IntPtr process, out long creation, out long exit, out long kernel, out long user);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(System.IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
[DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(System.IntPtr process, uint code);
[DllImport("kernel32.dll")] static extern bool CloseHandle(System.IntPtr handle);
// Ends the process only while it is still the one that was listed: same start time (within 1 ms) and same image path.
public static bool EndIfSame(int pid, string path, long created) {
    System.IntPtr h = OpenProcess(0x0001 | 0x1000, false, pid);   // PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION
    if (h == System.IntPtr.Zero) return false;
    try {
        long c, e, k, u;
        if (!GetProcessTimes(h, out c, out e, out k, out u) || System.Math.Abs(c - created) > 10000) return false;
        var name = new System.Text.StringBuilder(32768);
        int size = name.Capacity;
        if (!QueryFullProcessImageNameW(h, 0, name, ref size)) return false;
        if (!string.Equals(name.ToString(), path, System.StringComparison.OrdinalIgnoreCase)) return false;
        return TerminateProcess(h, 1);
    } finally { CloseHandle(h); }
}
'@
    $scm = @(Get-CimInstance Win32_Process -Filter "Name='services.exe'" | ForEach-Object ProcessId)
    $until = (Get-Date).AddSeconds($MaxSeconds)
    do {
        $left = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ProcessId -ne $PID -and $_.ProcessId -notin $scm -and $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($servicePrefix) -and
                -not $_.ExecutablePath.ToLowerInvariant().StartsWith($dataPrefix)
        })
        if ($left.Count -eq 0) { exit 0 }
        foreach ($p in $left) {
            if (-not $p.CreationDate) { continue }
            [void][CouchHud.Leftover]::EndIfSame([int]$p.ProcessId, $p.ExecutablePath, $p.CreationDate.ToFileTimeUtc())
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $until)
    exit 1
}

function Get-Running {
    Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -ne $PID -and $_.ExecutablePath -and (Test-Ours $_.ExecutablePath)
    }
}

$running = @(Get-Running)
$sessions = foreach ($p in $running) {
    if ($p.SessionId -le 0) { continue }
    $o = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction SilentlyContinue
    if (-not $o -or -not $o.User -or $o.User -in 'SYSTEM', 'LOCAL SERVICE', 'NETWORK SERVICE') { continue }
    "$($p.SessionId)|$($o.Domain)\$($o.User)"
}
Set-Content -LiteralPath $StateFile -Value @($sessions | Sort-Object -Unique) -Encoding UTF8

# A silent setup from another session (remote maintenance, software distribution) must not leave a locked couch open: once
# CouchHUD and its guard are gone nothing locks Windows any more. Disconnecting the session puts it behind the Windows
# sign-in (the console shows the lock screen); the restart script starts CouchHUD again inside that session afterwards.
# The session of the setup itself stays: whoever runs the setup there is at the keyboard. So does -KeepSession (an update the
# service runs for the user in that session).
try {
    Add-Type -Namespace CouchHud -Name Wts -MemberDefinition '[DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSDisconnectSession(System.IntPtr server, int sessionId, bool wait);'
    $own = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($id in @($running | Where-Object { $_.SessionId -gt 0 -and $_.SessionId -ne $own -and $_.SessionId -ne $KeepSession } | ForEach-Object { $_.SessionId } | Sort-Object -Unique)) {
        # WTS_CURRENT_SERVER_HANDLE is 0.
        if (-not [CouchHud.Wts]::WTSDisconnectSession([IntPtr]::Zero, $id, $true)) {
            Write-Warning "session $id could not be disconnected (error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
        }
    }
} catch { Write-Warning "sessions not disconnected: $_" }

$until = (Get-Date).AddSeconds($MaxSeconds)
do {
    foreach ($p in @(Get-Running)) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    if (@(Get-Running).Count -eq 0) {
        # Process gone is not file free: a loaded dll or exe refuses write access until the image is released.
        $locked = $false
        foreach ($f in Get-ChildItem -LiteralPath $AppDir -File -Include *.exe, *.dll -Recurse -ErrorAction SilentlyContinue | Where-Object { Test-Ours $_.FullName }) {
            try { [IO.File]::Open($f.FullName, 'Open', 'Write', 'None').Dispose() } catch { $locked = $true; break }
        }
        if (-not $locked) { exit 0 }
    }
} while ((Get-Date) -lt $until)
exit 1
