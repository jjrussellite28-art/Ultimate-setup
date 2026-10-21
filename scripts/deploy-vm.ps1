<#
Publishes CouchHUD locally and copies it into the Hyper-V test VM over PowerShell Direct.
Run on the host as a member of "Hyper-V-Administratoren". The VM credential is stored once with:
    Get-Credential | Export-Clixml "$env:USERPROFILE\couchhud-vm.cred"

    scripts\deploy-vm.ps1                 # publish, copy, start in the signed-in desktop session
    scripts\deploy-vm.ps1 -NoBuild        # copy the last publish again
    scripts\deploy-vm.ps1 -Arguments --dev
#>
param(
    [string]$VMName = 'Win11-Test',
    [string]$CredentialPath = "$env:USERPROFILE\couchhud-vm.cred",
    [string]$InstallDir = 'C:\Program Files\CouchHud',
    [string[]]$Arguments = @(),
    [switch]$NoBuild,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'

if (-not $NoBuild) {
    Write-Host '› dotnet publish'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish "$root\src\CouchHud.Shell\CouchHud.Shell.csproj" -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet publish "$root\plugins\CouchHud.Plugin.Wand\CouchHud.Plugin.Wand.csproj" -c Release -o "$publish\plugins\CouchHud.Plugin.Wand" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$session = New-PSSession -VMName $VMName -Credential (Import-Clixml $CredentialPath)
try {
    Write-Host "› stop CouchHUD in $VMName"
    # As the shell, explorer.exe is not there to take over; the copy only replaces files, it never touches the shell entry.
    Invoke-Command -Session $session -ScriptBlock {
        Get-Process CouchHud -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Host "› copy to $InstallDir"
    Invoke-Command -Session $session -ScriptBlock { New-Item -ItemType Directory -Force $using:InstallDir | Out-Null }
    Copy-Item -ToSession $session -Path "$publish\*" -Destination $InstallDir -Recurse -Force

    if (-not $NoStart) {
        Write-Host '› start in the desktop session'
        # PowerShell Direct is a non-interactive logon; a one-shot task with an interactive principal
        # puts the window on the signed-in user's desktop.
        Invoke-Command -Session $session -ScriptBlock {
            $user = (Get-CimInstance Win32_ComputerSystem).UserName
            if (-not $user) { throw 'Nobody is signed in to the VM desktop.' }
            $exe = Join-Path $using:InstallDir 'CouchHud.exe'
            $action = if ($using:Arguments) {
                New-ScheduledTaskAction -Execute $exe -Argument ($using:Arguments -join ' ') -WorkingDirectory $using:InstallDir
            } else {
                New-ScheduledTaskAction -Execute $exe -WorkingDirectory $using:InstallDir
            }
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
            Register-ScheduledTask -TaskName 'CouchHudDeploy' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName 'CouchHudDeploy'
            Start-Sleep -Seconds 2
            Unregister-ScheduledTask -TaskName 'CouchHudDeploy' -Confirm:$false
            Get-Process CouchHud -ErrorAction SilentlyContinue | Select-Object Id, SessionId, StartTime
        }
    }
}
finally {
    Remove-PSSession $session
}
Write-Host '✓ deployed'
