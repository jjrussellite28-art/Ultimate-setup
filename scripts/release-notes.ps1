#requires -Version 7
# Writes the release page text for a tag: the "### Kurz / Summary" block of the section "## <version> (...)" of
# CHANGELOG.md, followed by a link to that section of the full changelog at the tag. Fails when the section or its
# summary block is missing, so a release cannot go out without a summary.
#
#   .\scripts\release-notes.ps1 -Tag v0.7.0 -Out release-notes.md
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Out,
    [string] $Changelog = (Join-Path $PSScriptRoot '..' 'CHANGELOG.md'),
    [string] $Repo = 'kewe87/CouchHUD'
)
$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Tag '$Tag' is not vX.Y.Z" }
$version = $Matches[1]

$lines = Get-Content -LiteralPath $Changelog -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## $([regex]::Escape($version)) \(") { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section for $version" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}

$summary = -1
for ($i = $start + 1; $i -lt $end; $i++) {
    if ($lines[$i] -eq '### Kurz / Summary') { $summary = $i; break }
}
if ($summary -lt 0) { throw "Section $version of CHANGELOG.md has no '### Kurz / Summary' block; add one directly under the version heading" }
$summaryEnd = $end
for ($i = $summary + 1; $i -lt $end; $i++) {
    if ($lines[$i] -match '^###? ') { $summaryEnd = $i; break }
}
$text = ($lines[$summary..($summaryEnd - 1)] -join "`n").Trim()
if ($text.Length -le "### Kurz / Summary".Length) { throw "The '### Kurz / Summary' block of $version is empty" }

# GitHub's heading anchor: lower case, punctuation dropped (letters, digits, hyphens, spaces stay), spaces to hyphens.
$anchor = ($lines[$start].Substring(3).ToLowerInvariant() -replace '[^\p{L}\p{N}\- _]', '') -replace ' ', '-'

$body = "$text`n`nAlle Änderungen / All changes: [CHANGELOG.md](https://github.com/$Repo/blob/$Tag/CHANGELOG.md#$anchor)"
[IO.File]::WriteAllText($Out, "$body`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release notes for $Tag`: $($body.Length) characters -> $Out"
