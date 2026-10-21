#requires -Version 7
# Signs any file with ECDSA P-256 (SHA-256) and writes <file>.sig (Base64 over a context line plus the exact file bytes).
# Used for the catalog index (catalog-sign.ps1) and the checksum file of a release (sign-release.ps1).
#
# The private key stays OUTSIDE the repository: %USERPROFILE%\.couchhud\catalog-signing.key, protected with
# DPAPI (only this Windows user on this PC can read it). Never commit it, never copy it into the repo.
# The public key is compiled into CouchHUD (CatalogSignature.PublicKeyPem).
#
#   .\scripts\sign-file.ps1 -GenerateKey          # creates the key pair if missing, prints the public key
#   .\scripts\sign-file.ps1 -File .\catalog\index.json
#
# Sign the file exactly as it will be published (no later line-ending or whitespace changes).
#
# Domain separation: the signed bytes are a context line followed by the file, so a signature made for one purpose
# is worthless for the other (CatalogSignature.CatalogContext / ReleaseContext):
#   -Context catalog   "couchhud-catalog-v1`n" + file   (default; catalog index.json)
#   -Context release   "couchhud-release-v1`n" + file   (CouchHud-Setup-<v>.exe.sha256)
param(
    [string] $File,
    [ValidateSet('catalog', 'release')] [string] $Context = 'catalog',
    [switch] $GenerateKey,
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue

function Get-Fingerprint([System.Security.Cryptography.ECDsa] $key) {
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
}

function Write-PublicKey([System.Security.Cryptography.ECDsa] $key) {
    Write-Host ''
    Write-Host 'Public key (CatalogSignature.PublicKeyPem):'
    Write-Host $key.ExportSubjectPublicKeyInfoPem()
    Write-Host "Fingerprint (SHA-256 of the key): $(Get-Fingerprint $key)"
}

if (-not (Test-Path $KeyFile)) {
    if (-not $GenerateKey -and -not $File) { throw "No key at $KeyFile. Run with -GenerateKey first." }
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    if ((Resolve-Path (Split-Path $KeyFile -Parent) -ErrorAction SilentlyContinue) -and ((Split-Path $KeyFile -Parent) -like "$repoRoot*")) { throw 'The private key must not live inside the repository.' }
    New-Item -ItemType Directory -Force (Split-Path $KeyFile -Parent) | Out-Null
    $new = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $plain = $new.ExportPkcs8PrivateKey()
    $protected = [System.Security.Cryptography.ProtectedData]::Protect($plain, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllText($KeyFile, [Convert]::ToBase64String($protected))
    [Array]::Clear($plain, 0, $plain.Length)
    Write-Host "Created key pair. Private key (DPAPI, this user only): $KeyFile"
    Write-PublicKey $new
    $new.Dispose()
}

$protectedBytes = [Convert]::FromBase64String([IO.File]::ReadAllText($KeyFile).Trim())
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protectedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$read = 0
$ecdsa.ImportPkcs8PrivateKey($plainBytes, [ref]$read)
[Array]::Clear($plainBytes, 0, $plainBytes.Length)

if ($GenerateKey -and -not $File) { Write-PublicKey $ecdsa; return }
if (-not $File) { throw 'Pass -File <path of the file to sign>.' }

$path = (Resolve-Path $File).Path
$bytes = [IO.File]::ReadAllBytes($path)
$prefix = [Text.Encoding]::ASCII.GetBytes("couchhud-$Context-v1`n")
$signature = $ecdsa.SignData([byte[]]($prefix + $bytes), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
[IO.File]::WriteAllText("$path.sig", [Convert]::ToBase64String($signature))
Write-Host "Signed ($Context) $path -> $path.sig ($($bytes.Length) bytes)"
Write-Host "Key fingerprint: $(Get-Fingerprint $ecdsa)"
$ecdsa.Dispose()
