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
$installDir = Join-Path $env:ProgramFiles 'CouchHud'
$installedExe = Join-Path $installDir 'CouchHud.exe'

function Find-Iscc {
  foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) { if (Test-Path $p) { return $p } }
  return $null
}

try {
  # So the button inside Mega Hub (Settings > Power > Update Mega Hub) finds this folder from Program Files.
  $remember = Join-Path $env:APPDATA 'CouchHud'
  New-Item -ItemType Directory -Force -Path $remember | Out-Null
  Set-Content -Path (Join-Path $remember 'update-folder.txt') -Value $here -Encoding UTF8

  Write-Host '[1/5] Downloading the newest Mega Hub from GitHub...'
  Invoke-WebRequest -Uri 'https://github.com/jjrussellite28-art/mega-hub/archive/refs/heads/mega-hub-rename.zip' -OutFile $zip -UseBasicParsing
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  Expand-Archive -Path $zip -DestinationPath $tmp -Force
  $new = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName

  Write-Host '[2/5] Closing Mega Hub if it is open...'
  Get-Process CouchHud -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800

  # Did the program code change? (a fingerprint of src, plugins and the version file)
  $files = @(Get-ChildItem (Join-Path $new 'src'), (Join-Path $new 'plugins') -Recurse -File) + (Get-Item (Join-Path $new 'Directory.Build.props'))
  $sha = [Security.Cryptography.SHA1]::Create()
  $text = ($files | Sort-Object { $_.FullName.Substring($new.Length) } | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA1).Hash }) -join ''
  $code = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))
  $old = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
  $install = ($code -ne $old) -or -not (Test-Path $installedExe)

  Write-Host '[3/5] Copying the newest files into the project folder...'
  # Everything from GitHub is copied over the project folder; your own files (ideas, test list, old builds) stay.
  robocopy $new $here /E /NFL /NDL /NJH /NJS /NP /XF '.installed-stamp' | Out-Null
  if ($LASTEXITCODE -ge 8) { throw 'Copying failed.' }

  # Which version this is (shown small at the bottom of Mega Hub): the newest commit on GitHub and the time of this update.
  $sha = ''
  try { $sha = (Invoke-RestMethod -Uri 'https://api.github.com/repos/jjrussellite28-art/mega-hub/commits/mega-hub-rename' -Headers @{ 'User-Agent' = 'MegaHubUpdate' } -UseBasicParsing).sha.Substring(0, 7) } catch { }
  $stampJson = '{"commit":"' + $sha + '","updated":"' + (Get-Date -Format 'MMM d, h:mm tt') + '"}'
  [IO.File]::WriteAllText((Join-Path $here 'ui\build.json'), $stampJson)

  if ($install) {
    Write-Host '[4/5] Program code changed (or first install): building the installer. 5-10 minutes, do not close this window...'
    $iscc = Find-Iscc
    if (-not $iscc) {
      Write-Host 'Inno Setup is missing. Installing it...'
      winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
      $iscc = Find-Iscc
    }
    if (-not $iscc) { throw 'Inno Setup could not be installed. Install it from https://jrsoftware.org/isdl.php and run this again.' }
    Push-Location $here
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    dotnet publish src\CouchHud.Shell\CouchHud.Shell.csproj -c Release -r win-x64 --self-contained true -o publish
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'The app build failed.' }
    dotnet publish src\CouchHud.Service\CouchHud.Service.csproj -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o publish\service
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'The service build failed.' }
    $wv = 'installer\MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path $wv)) { Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv -UseBasicParsing }
    $ver = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
    if (-not $ver) { $ver = '1.0.0' }
    if (Test-Path installer\output) { Remove-Item installer\output\*.exe -Force -ErrorAction SilentlyContinue }
    & $iscc "/DAppVersion=$ver-megahub" '/DPublishDir=..\publish' '/DWebView2Setup=MicrosoftEdgeWebview2Setup.exe' installer\CouchHud.iss
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Building Setup.exe failed.' }
    $setup = Get-ChildItem installer\output\CouchHud-Setup-*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Pop-Location
    Write-Host '[5/5] Installing into Program Files. Windows asks once: say Yes...'
    $p = Start-Process -FilePath $setup.FullName -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "The installer stopped (code $($p.ExitCode))." }
    Set-Content -Path $stamp -Value $code
  } else {
    Write-Host '[4/5] No program code changed: no rebuild needed.'
    Write-Host '[5/5] Copying the newest screens into Program Files. Windows asks once: say Yes...'
    # robocopy answers 1 when it copied files (fine); only 8 and up is an error. An explicit exit, else PowerShell reports its 1 as a failure.
    $copy = "robocopy '$here\ui' '$installDir\ui' /MIR /NFL /NDL /NJH /NJS /NP; if (`$LASTEXITCODE -ge 8) { exit 1 } else { exit 0 }"
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $copy
    if ($p.ExitCode -ne 0) { throw 'Copying into Program Files failed.' }
  }
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host 'Done. Starting Mega Hub...'
  Start-Process $installedExe -WorkingDirectory $installDir
} catch {
  Write-Host ''
  Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
  exit 1
}
