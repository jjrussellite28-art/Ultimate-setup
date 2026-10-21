<#
Confirms (or declines) a UAC prompt in the Hyper-V test VM through the VM's virtual keyboard,
which reaches the secure desktop. Waits for consent.exe, sends the keys, checks the prompt is gone.
    scripts\vm-uac.ps1            # Yes
    scripts\vm-uac.ps1 -Decline   # No
#>
param(
    [string]$VMName = 'Win11-Test',
    [string]$CredentialPath = "$env:USERPROFILE\couchhud-vm.cred",
    [int]$TimeoutSeconds = 30,
    [switch]$Decline
)
$ErrorActionPreference = 'Stop'
$cred = Import-Clixml $CredentialPath
$ns = 'root\virtualization\v2'
$vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
$kb = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard

function Test-Prompt {
    # PowerShell Direct drops a session now and then; one retry covers it.
    for ($try = 0; ; $try++) {
        try { return Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) } }
        catch { if ($try -ge 2) { throw }; Start-Sleep -Seconds 1 }
    }
}
function Send-Key([string]$method, [int]$vk) { [void](Invoke-CimMethod -InputObject $kb -MethodName $method -Arguments @{ keyCode = [uint32]$vk }) }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not (Test-Prompt)) {
    if ((Get-Date) -gt $deadline) { throw 'No UAC prompt appeared.' }
    Start-Sleep -Milliseconds 500
}
# The secure desktop needs a moment before it takes input.
Start-Sleep -Seconds 2
if ($Decline) {
    Send-Key TypeKey 0x1B   # Esc = No
} else {
    # Alt+J ("Ja") on German Windows, Alt+Y on English.
    foreach ($letter in 0x4A, 0x59) {
        Send-Key PressKey 0x12; Send-Key TypeKey $letter; Send-Key ReleaseKey 0x12
        Start-Sleep -Milliseconds 800
        if (-not (Test-Prompt)) { break }
    }
}
Start-Sleep -Seconds 1
if (Test-Prompt) { throw 'UAC prompt is still open.' }
if ($Decline) { 'UAC: declined' } else { 'UAC: confirmed' }
