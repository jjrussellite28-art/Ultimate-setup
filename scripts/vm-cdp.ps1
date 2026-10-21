<#
Talks to the CouchHUD UI in the Hyper-V test VM over the Chrome DevTools Protocol.
Needs WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222 for the VM user.

    scripts\vm-cdp.ps1 -Eval 'document.title'
    scripts\vm-cdp.ps1 -Screenshot shot.png
    scripts\vm-cdp.ps1 -Keys ArrowRight,ArrowRight,Enter
    scripts\vm-cdp.ps1 -Console            # errors and warnings since the last call
    scripts\vm-cdp.ps1 -Reload -Console    # reload the page and capture errors from the very start
#>
param(
    [string]$VMName = 'Win11-Test',
    [string]$CredentialPath = "$env:USERPROFILE\couchhud-vm.cred",
    [string]$Eval,
    [string]$Screenshot,
    [string[]]$Keys,
    [int]$KeyDelayMs = 250,
    [switch]$Console,
    [switch]$Reload
)
$ErrorActionPreference = 'Stop'

$remote = {
    param($Eval, $WantShot, $Keys, $KeyDelayMs, $Console, $Reload)
    # Right after a start the debug port needs a few seconds to come up.
    for ($try = 0; ; $try++) {
        try { $page = (Invoke-RestMethod http://127.0.0.1:9222/json -TimeoutSec 5) | Where-Object type -eq 'page' | Select-Object -First 1; if ($page) { break } }
        catch { if ($try -ge 15) { throw } }
        Start-Sleep -Seconds 2
    }
    if (-not $page) { throw 'No CouchHUD page on port 9222. Is CouchHUD running?' }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.KeepAliveInterval = [TimeSpan]::Zero
    $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:id = 0
    function Send-Cdp($method, $params = @{}) {
        $script:id++
        $msg = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $buffer = [byte[]]::new(1MB)
        while ($true) {
            $sb = [Text.StringBuilder]::new()
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, [Threading.CancellationToken]::None).Result
                [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
            } until ($r.EndOfMessage)
            $reply = $sb.ToString() | ConvertFrom-Json
            if ($reply.id -eq $script:id) {
                if ($reply.error) { throw "$method failed: $($reply.error.message)" }
                return $reply.result
            }
        }
    }
    function Invoke-Js($expr) {
        $r = Send-Cdp 'Runtime.evaluate' @{ expression = $expr; returnByValue = $true; awaitPromise = $true }
        if ($r.exceptionDetails) { throw "JS: $($r.exceptionDetails.exception.description) $($r.exceptionDetails.text)" }
        $r.result.value
    }
    try {
        $out = [ordered]@{}
        # Runs in Windows PowerShell 5.1 inside the VM. Console capture lives in the page, so it survives between calls until the page reloads.
        $capture = @'
if (!window.__cdpLog) {
  window.__cdpLog = [];
  for (const level of ['error', 'warn']) {
    const orig = console[level];
    console[level] = (...a) => { window.__cdpLog.push(level + ': ' + a.map(String).join(' ')); orig.apply(console, a); };
  }
  addEventListener('error', e => window.__cdpLog.push('uncaught: ' + e.message + ' @ ' + e.filename + ':' + e.lineno));
  addEventListener('unhandledrejection', e => window.__cdpLog.push('rejection: ' + (e.reason && e.reason.stack || e.reason)));
}
'@
        if ($Reload) {
            # Installed before any page script runs, so errors during start-up are caught too.
            [void](Send-Cdp 'Page.addScriptToEvaluateOnNewDocument' @{ source = $capture })
            [void](Send-Cdp 'Page.reload' @{ ignoreCache = $true })
            Start-Sleep -Seconds 8
        }
        [void](Invoke-Js $capture)
        foreach ($key in $Keys) {
            $codes = @{ ArrowUp = 38; ArrowDown = 40; ArrowLeft = 37; ArrowRight = 39; Enter = 13; Escape = 27; Backspace = 8; Tab = 9; Home = 36; PageUp = 33; PageDown = 34; ' ' = 32 }
            $vk = if ($codes.ContainsKey($key)) { $codes[$key] } else { [int][char]$key.ToUpper()[0] }
            $code = if ($key.Length -eq 1) { 'Key' + $key.ToUpper() } else { $key }
            $text = if ($key.Length -eq 1) { $key } else { $null }
            $down = @{ type = 'rawKeyDown'; key = $key; code = $code; windowsVirtualKeyCode = $vk }
            [void](Send-Cdp 'Input.dispatchKeyEvent' $down)
            if ($text) { [void](Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'char'; text = $text; key = $key }) }
            [void](Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = $key; code = $code; windowsVirtualKeyCode = $vk })
            Start-Sleep -Milliseconds $KeyDelayMs
        }
        if ($Eval) { $out.eval = Invoke-Js $Eval }
        if ($WantShot) { $out.png = (Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }).data }
        if ($Console) { $out.console = Invoke-Js 'JSON.stringify(window.__cdpLog.splice(0))' | ConvertFrom-Json }
        [pscustomobject]$out
    }
    finally { $ws.Dispose() }
}

$cred = Import-Clixml $CredentialPath
$result = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock $remote -ArgumentList @($Eval, ([bool]$Screenshot), $Keys, $KeyDelayMs, ([bool]$Console), ([bool]$Reload))
if ($Screenshot) {
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Screenshot), [Convert]::FromBase64String($result.png))
    "screenshot: $Screenshot"
}
if ($Eval) { $result.eval }
if ($Console) { if ($result.console) { $result.console } else { 'console: clean' } }
