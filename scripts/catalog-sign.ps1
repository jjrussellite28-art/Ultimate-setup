#requires -Version 7
# Signs a catalog index.json (writes index.json.sig). Thin wrapper around sign-file.ps1, which holds the
# key handling (private key outside the repository, DPAPI-protected) and documents it.
#
#   .\scripts\catalog-sign.ps1 -GenerateKey          # creates the key pair if missing, prints the public key
#   .\scripts\catalog-sign.ps1 -Index .\catalog\index.json
param(
    [string] $Index,
    [switch] $GenerateKey,
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
$arguments = @{ KeyFile = $KeyFile }
if ($Index) { $arguments.File = $Index }
if ($GenerateKey) { $arguments.GenerateKey = $true }
& (Join-Path $PSScriptRoot 'sign-file.ps1') @arguments
