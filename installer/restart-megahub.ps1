# Called by MegaHub.iss after the files are replaced: starts Mega Hub again in every session where
# stop-megahub.ps1 ended it. Winlogon starts the shell only at sign-in, so without this a session that had
# Mega Hub as its shell stays black. The setup cannot start a program in another session directly, so a one-shot
# scheduled task runs it as that user (Interactive logon type, no password needed, limited rights: Mega Hub
# never runs elevated). Registered, started, and removed again once MegaHub.exe shows up (a running instance
# survives the removal of its task). A second start in a session that was restarted already (the self-update's
# helper) is skipped, and would end at once anyway: one instance per session.
param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$StateFile
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $AppDir 'MegaHub.exe'
if (-not (Test-Path -LiteralPath $StateFile) -or -not (Test-Path -LiteralPath $exe)) { exit 0 }

function Test-HudInSession([int]$Session) {
    [bool](Get-CimInstance Win32_Process -Filter "Name='MegaHub.exe'" |
        Where-Object { $_.SessionId -eq $Session -and $_.ExecutablePath -ieq $exe })
}

$i = 0
foreach ($line in Get-Content -LiteralPath $StateFile) {
    $session, $user = $line -split '\|', 2
    if (-not $user) { continue }
    if (Test-HudInSession ([int]$session)) { continue }
    $i++
    $name = "MegaHub-Restart-$i"
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
