# CI only (GitHub's Windows runner, a throwaway VM where the job runs as administrator). Never on a developer's PC: it installs
# CouchHUD with its SYSTEM service and uninstalls it again.
# 1. Silent install, then the service as the SCM sees it: LocalSystem, auto start, sid type, privileges, DACL, failure actions,
#    event source, folder ACL - against the values in CouchHud.iss (InstallerTests pins those to ServiceHardening).
# 2. It runs, stays running and serves its pipe; the same after an update over itself the way the service runs one (DACL and
#    failure actions restored, {app}\service\data kept).
# 3. The emergency repair (repair-service.cmd) works and leaves the same configuration.
# 4. Silent uninstall: service, event source, service folder with its data are gone.
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Iss
)
$ErrorActionPreference = 'Stop'

$defines = @{}
foreach ($line in Get-Content -LiteralPath $Iss) {
    if ($line -match '^#define\s+(\w+)\s+"(.*)"\s*$') { $defines[$Matches[1]] = $Matches[2] }
}
$name = $defines.ServiceName
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
$app = Join-Path $env:ProgramFiles 'CouchHud'
$serviceDir = Join-Path $app $defines.ServiceFolder
$dataDir = Join-Path $serviceDir $defines.ServiceDataFolder
$log = Join-Path $env:RUNNER_TEMP 'couchhud-setup.log'
$uninstallLog = Join-Path $env:RUNNER_TEMP 'couchhud-uninstall.log'
$failures = [Collections.Generic.List[string]]::new()

function Check([bool]$Ok, [string]$What) {
    if ($Ok) { Write-Host "ok   $What" } else { Write-Host "FAIL $What"; $failures.Add($What) }
}

function Invoke-Sc([string[]]$Arguments) { (& $sc @Arguments 2>&1 | Out-String) }

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $Condition)
}

function Test-ServiceConfig([string]$When) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
    Check ($null -ne $service) "$When service exists"
    if ($null -eq $service) { return }
    Check ($service.StartName -eq 'LocalSystem') "$When runs as LocalSystem ($($service.StartName))"
    Check ($service.StartMode -eq 'Auto') "$When start type automatic ($($service.StartMode))"
    Check ($service.PathName -eq "`"$serviceDir\$($defines.ServiceExe)`"") "$When binary path quoted ($($service.PathName))"
    Check ((Invoke-Sc 'qc', $name) -notmatch 'DELAYED') "$When not delayed"
    Check ((Invoke-Sc 'qsidtype', $name) -match "SERVICE_SID_TYPE:\s+$($defines.ServiceSidType)") "$When sid type $($defines.ServiceSidType)"
    $privs = Invoke-Sc 'qprivs', $name
    $wanted = $defines.ServicePrivileges -split '/'
    $listed = @([regex]::Matches($privs, 'Se\w+Privilege') | ForEach-Object Value)
    Check ((@($listed | Sort-Object) -join ',') -eq (@($wanted | Sort-Object) -join ',')) "$When required privileges exactly ($($listed -join ', '))"
    $sd = (Invoke-Sc 'sdshow', $name).Trim()
    Check ($sd.StartsWith($defines.ServiceSddl)) "$When service DACL ($sd)"
    $failure = Invoke-Sc 'qfailure', $name
    Check ($failure -match "RESET_PERIOD \(in seconds\)\s*:\s*$($defines.ServiceFailureReset)") "$When failure reset period"
    $delays = @([regex]::Matches($failure, 'Delay = (\d+)') | ForEach-Object { $_.Groups[1].Value })
    $expected = @([regex]::Matches($defines.ServiceFailureActions, 'restart/(\d+)') | ForEach-Object { $_.Groups[1].Value })
    Check (($delays -join ',') -eq ($expected -join ',')) "$When failure actions restart $($delays -join '/')"
    Check ((Invoke-Sc 'qfailureflag', $name) -match 'TRUE') "$When failure flag"
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$($defines.EventSource)"
    $file = (Get-Item -LiteralPath $key -ErrorAction SilentlyContinue)?.GetValue('EventMessageFile', $null, 'DoNotExpandEnvironmentNames')
    Check ($file -eq $defines.EventMessageFile) "$When event source registered ($file)"
    Check (Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables($defines.EventMessageFile))) "$When event message file exists"
    $acl = Get-Acl -LiteralPath $serviceDir
    Check $acl.AreAccessRulesProtected "$When service folder does not inherit"
    Check ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') "$When service folder owned by Administrators"
    $writers = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544' -and
        ($_.FileSystemRights -band ([Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership, DeleteSubdirectoriesAndFiles')) -ne 0
    })
    Check ($writers.Count -eq 0) "$When nobody but SYSTEM and Administrators may change the service folder"
    Check (Test-Path -LiteralPath $dataDir -PathType Container) "$When data folder exists"
    if (Test-Path -LiteralPath $dataDir) {
        $dataAcl = Get-Acl -LiteralPath $dataDir
        $dataWriters = @($dataAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544' -and
            ($_.FileSystemRights -band ([Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership, DeleteSubdirectoriesAndFiles')) -ne 0
        })
        Check ($dataWriters.Count -eq 0) "$When nobody but SYSTEM and Administrators may change the data folder"
        Check ($dataAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -in 'S-1-5-18', 'S-1-5-32-544') "$When data folder owned by SYSTEM or Administrators"
        # Inheritance broken at data, and nobody but SYSTEM and Administrators may even read it (no Users read from the service folder).
        Check $dataAcl.AreAccessRulesProtected "$When data folder does not inherit"
        $dataOthers = @($dataAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544'
        })
        Check ($dataOthers.Count -eq 0) "$When data folder is SYSTEM and Administrators only ($(($dataOthers | ForEach-Object { $_.IdentityReference.Value }) -join ','))"
    }
}

function Test-Running([string]$When) {
    Check (Wait-Until { (Get-Service $name -ErrorAction SilentlyContinue).Status -eq 'Running' } 30) "$When service running"
    Start-Sleep -Seconds 5
    Check ((Get-Service $name -ErrorAction SilentlyContinue).Status -eq 'Running') "$When service still running after 5 s"
    Check (Wait-Until { [IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\CouchHud.Service.v1' } 15) "$When pipe CouchHud.Service.v1 served"
}

# ---- 1. install --------------------------------------------------------------------------------------------------------------
$p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "setup exit code 0 ($($p.ExitCode))"
Test-ServiceConfig 'install:'
Test-Running 'install:'

# ---- 2. update over itself as the service would run it -------------------------------------------------------------------------
$keep = Join-Path $dataDir 'ci-keep.txt'
Set-Content -LiteralPath $keep -Value 'kept'
$p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SERVICEUPDATE=1', '/KEEPSESSION=1', '/MERGETASKS=!autostart', "/LOG=`"$log.update`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "update exit code 0 ($($p.ExitCode))"
Test-ServiceConfig 'update:'
Test-Running 'update:'
Check (Test-Path -LiteralPath $keep) 'update keeps the data folder'

# ---- 3. emergency repair -------------------------------------------------------------------------------------------------------
$repair = Join-Path $app $defines.RepairScript
Check (Test-Path -LiteralPath $repair) 'repair script written'
# Break it a little: manual start, one privilege only. The repair must put both back.
& $sc config $name start= demand | Out-Null
& $sc privs $name SeChangeNotifyPrivilege | Out-Null
$cmd = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
$p = Start-Process -FilePath $cmd -ArgumentList '/d', '/c', "`"`"$repair`"`"" -Wait -PassThru -NoNewWindow
Check ($p.ExitCode -eq 0) "repair exit code 0 ($($p.ExitCode))"
Test-ServiceConfig 'repair:'
Test-Running 'repair:'

# ---- 4. uninstall --------------------------------------------------------------------------------------------------------------
$uninstaller = Join-Path $app 'unins000.exe'
Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"" -Wait
# The uninstaller hands over to a copy of itself in %TEMP% and returns at once: wait for the folder to go.
Check (Wait-Until { -not (Test-Path -LiteralPath $uninstaller) } 300) 'uninstaller finished'
Check (Wait-Until { -not (Get-Service $name -ErrorAction SilentlyContinue) } 30) 'service deleted'
Check (-not (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$($defines.EventSource)")) 'event source removed'
Check (-not (Test-Path -LiteralPath $serviceDir)) 'service folder removed'
Check (-not (Test-Path -LiteralPath $dataDir)) 'service data removed'
Check (-not (Test-Path -LiteralPath (Join-Path $env:ProgramData 'CouchHud\Service'))) 'nothing below ProgramData'

if ($failures.Count -gt 0) {
    foreach ($f in $log, "$log.update", $uninstallLog) {
        if (Test-Path -LiteralPath $f) { Write-Host "---- $f"; Get-Content -LiteralPath $f | Select-Object -Last 120 }
    }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $defines.EventSource } -MaxEvents 30 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "event $($_.Id) $($_.LevelDisplayName): $($_.Message)" }
    throw "$($failures.Count) service check(s) failed: $($failures -join '; ')"
}
Write-Host 'service smoke test passed'
