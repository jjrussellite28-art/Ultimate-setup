# Gets the newest Mega Hub from GitHub and puts it INTO PROGRAM FILES (the installed copy, where shell mode works).
#   Screens/buttons only changed -> copies them into the installed copy (a few seconds, one Windows "Yes").
#   Program code (C#) changed    -> builds the Setup and installs it over the old one (5-10 minutes, one Windows "Yes").
#   Not installed yet            -> the same install, the first time. It stays there until you uninstall it.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip  = Join-Path $env:TEMP 'mega-hub-newest.zip'
$tmp  = Join-Path $env:TEMP 'mega-hub-newest'
$stamp = Join-Path $here '.installed-stamp'
$installDir = Join-Path $env:ProgramFiles 'MegaHub'
$installedExe = Join-Path $installDir 'MegaHub.exe'

# Progress for the screen inside Mega Hub (the update runs without a window of its own): %APPDATA%\MegaHub\update-status.json.
$statusFile = Join-Path $env:APPDATA 'MegaHub\update-status.json'
function Say([string]$text, [string]$err = '') {
  Write-Host $text
  try {
    New-Item -ItemType Directory -Force -Path (Split-Path $statusFile) | Out-Null
    $json = '{"text":' + (ConvertTo-Json $text) + ',"error":' + (ConvertTo-Json $err) + '}'
    [IO.File]::WriteAllText($statusFile, $json)
  } catch { }
}

function Stop-MegaHub {
  Get-Process MegaHub -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
}

# Own plugins of the project (plugins\MegaHub.Plugin.*): rebuilt when their source changed and put into the plugin folder of Mega Hub.
function Update-Plugins {
  $src = Join-Path $here 'plugins'
  if (-not (Test-Path $src)) { return }
  $dest = Join-Path $env:APPDATA 'MegaHub\plugins'
  foreach ($d in @(Get-ChildItem $src -Directory | Where-Object { $_.Name -like 'MegaHub.Plugin.*' })) {
    $name = $d.Name
    $target = Join-Path $dest $name
    $h = (Get-ChildItem $d.FullName -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA1).Hash }) -join ''
    $stampFile = Join-Path $here ".plugin-$name-stamp"
    if ((Test-Path (Join-Path $target "$name.dll")) -and (Test-Path $stampFile) -and ((Get-Content $stampFile -Raw).Trim() -eq $h)) { continue }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Say "Plugin $name skipped: .NET is missing."; continue }
    Say "[plugins] Updating $name..."
    $out = Join-Path $env:TEMP "mega-hub-plugin-$name"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $d.FullName "$name.csproj") -c Release -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { Say "Plugin $name could not be built; the old one stays."; continue }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $out "$name.dll") $target -Force
    Copy-Item (Join-Path $d.FullName 'plugin.json') $target -Force
    if ((Test-Path (Join-Path $d.FullName 'sample-data')) -and -not (Test-Path (Join-Path $target 'sample-data'))) { Copy-Item (Join-Path $d.FullName 'sample-data') $target -Recurse -Force }
    Set-Content -Path $stampFile -Value $h
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Find-Iscc {
  foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) { if (Test-Path $p) { return $p } }
  return $null
}

try {
  # So the button inside Mega Hub (Settings > Power > Update Mega Hub) finds this folder from Program Files.
  $remember = Join-Path $env:APPDATA 'MegaHub'
  New-Item -ItemType Directory -Force -Path $remember | Out-Null
  Set-Content -Path (Join-Path $remember 'update-folder.txt') -Value $here -Encoding UTF8

  Say '[1/5] Downloading the newest Mega Hub from GitHub...'
  Invoke-WebRequest -Uri 'https://github.com/jjrussellite28-art/mega-hub/archive/refs/heads/mega-hub-rename.zip' -OutFile $zip -UseBasicParsing
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  Expand-Archive -Path $zip -DestinationPath $tmp -Force
  $new = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName

  Say '[2/5] Looking at what changed...'

  # Did the program code change? (a fingerprint of src, plugins and the version file)
  $files = @(Get-ChildItem (Join-Path $new 'src'), (Join-Path $new 'plugins') -Recurse -File) + (Get-Item (Join-Path $new 'Directory.Build.props'))
  $sha = [Security.Cryptography.SHA1]::Create()
  $text = ($files | Sort-Object { $_.FullName.Substring($new.Length) } | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA1).Hash }) -join ''
  $code = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))
  $old = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
  $install = ($code -ne $old) -or -not (Test-Path $installedExe)

  Say '[3/5] Copying the newest files into the project folder...'
  # Everything from GitHub is copied over the project folder; your own files (ideas, test list, old builds) stay.
  robocopy $new $here /E /NFL /NDL /NJH /NJS /NP /XF '.installed-stamp' | Out-Null
  if ($LASTEXITCODE -ge 8) { throw 'Copying failed.' }

  # Which version this is (shown small at the bottom of Mega Hub): the newest commit on GitHub and the time of this update.
  $sha = ''
  try { $sha = (Invoke-RestMethod -Uri 'https://api.github.com/repos/jjrussellite28-art/mega-hub/commits/mega-hub-rename' -Headers @{ 'User-Agent' = 'MegaHubUpdate' } -UseBasicParsing).sha.Substring(0, 7) } catch { }
  $stampJson = '{"commit":"' + $sha + '","updated":"' + (Get-Date -Format 'MMM d, h:mm tt') + '"}'
  [IO.File]::WriteAllText((Join-Path $here 'ui\build.json'), $stampJson)

  if ($install) {
    Say '[4/5] Program code changed (or first install): building the installer. about 10 minutes, Mega Hub stays open until it is ready.'
    $iscc = Find-Iscc
    if (-not $iscc) {
      Say 'Inno Setup is missing. Installing it...'
      winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
      $iscc = Find-Iscc
    }
    if (-not $iscc) { throw 'Inno Setup could not be installed. Install it from https://jrsoftware.org/isdl.php and run this again.' }
    Push-Location $here
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    dotnet publish src\MegaHub.Shell\MegaHub.Shell.csproj -c Release -r win-x64 --self-contained true -o publish
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'The app build failed.' }
    dotnet publish src\MegaHub.Service\MegaHub.Service.csproj -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o publish\service
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'The service build failed.' }
    $wv = 'installer\MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path $wv)) { Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv -UseBasicParsing }
    $ver = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
    if (-not $ver) { $ver = '1.0.0' }
    if (Test-Path installer\output) { Remove-Item installer\output\*.exe -Force -ErrorAction SilentlyContinue }
    & $iscc "/DAppVersion=$ver-megahub" '/DPublishDir=..\publish' '/DWebView2Setup=MicrosoftEdgeWebview2Setup.exe' installer\MegaHub.iss
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Building Setup.exe failed.' }
    $setup = Get-ChildItem installer\output\MegaHub-Setup-*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Pop-Location
    Say '[5/5] Installing into Program Files. Windows asks once: say Yes...'
    Stop-MegaHub
    $p = Start-Process -FilePath $setup.FullName -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "The installer stopped (code $($p.ExitCode))." }
    Set-Content -Path $stamp -Value $code
  } else {
    Say '[4/5] No program code changed: no rebuild needed.'
    Say '[5/5] Copying the newest screens into Program Files. Windows asks once: say Yes...'
    # robocopy answers 1 when it copied files (fine); only 8 and up is an error. An explicit exit, else PowerShell reports its 1 as a failure.
    Stop-MegaHub
    $copy = "robocopy '$here\ui' '$installDir\ui' /MIR /NFL /NDL /NJH /NJS /NP; if (`$LASTEXITCODE -ge 8) { exit 1 } else { exit 0 }"
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $copy
    if ($p.ExitCode -ne 0) { throw 'Copying into Program Files failed.' }
  }
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Stop-MegaHub
  Update-Plugins
  Say 'Done. Starting Mega Hub...'
  Start-Process $installedExe -WorkingDirectory $installDir
} catch {
  Write-Host ''
  Say 'The update stopped.' $_.Exception.Message
  exit 1
}
