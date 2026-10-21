# Called by CouchHud.iss after the files are replaced: starts CouchHUD again in every session where
# stop-couchhud.ps1 ended it. Winlogon starts the shell only at sign-in, so without this a session that had
# CouchHUD as its shell stays black. The setup cannot start a program in another session directly, so a one-shot
# scheduled task runs it as that user (Interactive logon type, no password needed, limited rights: CouchHUD
# never runs elevated). Registered, started, and removed again once CouchHud.exe shows up (a running instance
# survives the removal of its task). A second start in a session that was restarted already (the self-update's
# helper) is skipped, and would end at once anyway: one instance per session.
param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$StateFile
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $AppDir 'CouchHud.exe'
if (-not (Test-Path -LiteralPath $StateFile) -or -not (Test-Path -LiteralPath $exe)) { exit 0 }

function Test-HudInSession([int]$Session) {
    [bool](Get-CimInstance Win32_Process -Filter "Name='CouchHud.exe'" |
        Where-Object { $_.SessionId -eq $Session -and $_.ExecutablePath -ieq $exe })
}

$i = 0
foreach ($line in Get-Content -LiteralPath $StateFile) {
    $session, $user = $line -split '\|', 2
    if (-not $user) { continue }
    if (Test-HudInSession ([int]$session)) { continue }
    $i++
    $name = "CouchHUD-Restart-$i"
    try {
        $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $AppDir
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $name
        for ($t = 0; $t -lt 40 -and -not (Test-HudInSession ([int]$session)); $t++) { Start-Sleep -Milliseconds 250 }
    } catch {
        Write-Output "restart for $user failed: $($_.Exception.Message)"
    } finally {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
}
Remove-Item -LiteralPath $StateFile -Force -ErrorAction SilentlyContinue
