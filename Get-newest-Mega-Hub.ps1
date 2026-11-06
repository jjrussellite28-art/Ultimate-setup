# Gets the newest Mega Hub (test builds) from GitHub and puts it into the app next to this folder.
# Screens/buttons only changed  -> copies them in (a few seconds).
# Program code (C#) changed     -> also rebuilds the app once (a few minutes).
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$app  = Join-Path $here 'Mega Hub App'
$zip  = Join-Path $env:TEMP 'mega-hub-newest.zip'
$tmp  = Join-Path $env:TEMP 'mega-hub-newest'
$stamp = Join-Path $here '.code-stamp'

try {
  Write-Host '[1/4] Downloading the newest Mega Hub from GitHub...'
  Invoke-WebRequest -Uri 'https://github.com/jjrussellite28-art/mega-hub/archive/refs/heads/mega-hub-rename.zip' -OutFile $zip -UseBasicParsing
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  Expand-Archive -Path $zip -DestinationPath $tmp -Force
  $new = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName

  Write-Host '[2/4] Closing Mega Hub if it is open...'
  Get-Process CouchHud -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800

  # Did the program code change? (a fingerprint of src, plugins and the version file)
  $files = @(Get-ChildItem (Join-Path $new 'src'), (Join-Path $new 'plugins') -Recurse -File) + (Get-Item (Join-Path $new 'Directory.Build.props'))
  $sha = [Security.Cryptography.SHA1]::Create()
  $text = ($files | Sort-Object { $_.FullName.Substring($new.Length) } | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA1).Hash }) -join ''
  $code = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))
  $old = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
  $rebuild = ($code -ne $old) -or -not (Test-Path (Join-Path $app 'CouchHud.exe'))

  Write-Host '[3/4] Copying the newest files...'
  # Everything from GitHub is copied over the project folder; your own files (ideas, test list, the built app) stay.
  robocopy $new $here /E /NFL /NDL /NJH /NJS /NP /XF '.code-stamp' | Out-Null
  if ($LASTEXITCODE -ge 8) { throw 'Copying failed.' }
  robocopy (Join-Path $here 'ui') (Join-Path $app 'ui') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw 'Copying the screens failed.' }

  if ($rebuild) {
    Write-Host '[4/4] The program code changed: rebuilding the app (a few minutes, do not close this window)...'
    Push-Location $here
    dotnet publish src\CouchHud.Shell\CouchHud.Shell.csproj -c Release -r win-x64 --self-contained true -o $app
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'The build failed.' }
    dotnet build plugins\MegaHub.Plugin.Achievements -c Release -o "$env:APPDATA\CouchHud\plugins\MegaHub.Plugin.Achievements"
    Pop-Location
    Set-Content -Path $stamp -Value $code
  } else {
    Write-Host '[4/4] No program code changed: no rebuild needed.'
  }
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host 'Done. Starting Mega Hub...'
  Start-Process (Join-Path $app 'CouchHud.exe') -WorkingDirectory $app
} catch {
  Write-Host ''
  Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
  exit 1
}
