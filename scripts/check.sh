#!/usr/bin/env bash
# Everything CI checks, before a push: build (Windows projects too), tests, UI syntax.
# Works on Windows (Git Bash), Linux and macOS with the .NET 10 SDK and Node.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -e catalog/index.json.unsigned ]; then
  echo "! WARNUNG: catalog/index.json.unsigned existiert, die Katalog-Signatur ist noch zu erneuern (scripts/catalog-sign.ps1), dann die Datei löschen."
  if [ "${RELEASE:-}" = "1" ]; then
    echo "  RELEASE=1: so wird nichts veröffentlicht."
    exit 1
  fi
fi

echo "› dotnet build"
dotnet build MegaHub.slnx -c Release -p:EnableWindowsTargeting=true --nologo -v quiet

echo "› dotnet test"
if [ "${RELEASE:-}" = "1" ]; then echo "  RELEASE=1: the catalog test also demands that index.json.sig matches index.json (pinned key from the code)."; fi
dotnet test MegaHub.slnx -c Release -p:EnableWindowsTargeting=true --no-build --nologo -v quiet

echo "› UI syntax"
# The UI files are ES modules; plain `node --check file.js` can pass a module with a syntax error.
find ui scripts \( -name '*.js' -o -name '*.mjs' \) -not -path '*/node_modules/*' | while read -r f; do
  node --input-type=module --check < "$f" || { echo "  in $f"; exit 1; }
done

echo "› UI lint (ESLint: real bugs only, no style rules)"
npm ci --ignore-scripts --prefix scripts --no-audit --no-fund --loglevel=error
scripts/node_modules/.bin/eslint

echo "› UI logic"
node scripts/ui-logic-check.mjs

echo "› Edge profile cleanup (fake temp folder, no Edge)"
node scripts/edge-profile-check.mjs

echo "› UI in headless Edge (browser, companion apps)"
# The two page checks drive the mock UI in headless Edge (--headless=new --mute-audio, no window, no sound). EDGE names msedge.exe when it is elsewhere.
edge_found=0
for edge in "${EDGE:-}" "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" "/c/Program Files/Microsoft/Edge/Application/msedge.exe"; do
  if [ -n "$edge" ] && [ -e "$edge" ]; then edge_found=1; fi
done
if [ "$edge_found" = "1" ]; then
  # MEGAHUB_UI_PORT / MEGAHUB_UI_DEBUG move the checks to other ports when several runs share a machine.
  ui_port="${MEGAHUB_UI_PORT:-5198}"
  ui_debug="${MEGAHUB_UI_DEBUG:-9334}"
  node scripts/browser-ui-check.mjs --port "$ui_port" --debug "$ui_debug"
  node scripts/companions-ui-check.mjs --port "$((ui_port + 1))" --debug "$((ui_debug + 1))"
else
  echo "  skipped: no Edge found (set EDGE to msedge.exe)"
fi

echo "› Catalog addresses"
node scripts/catalog-check.mjs

echo "› CSS syntax"
node scripts/css-check.mjs

echo "› Skins keep the tabs"
node scripts/skin-check.mjs

echo "› UI texts (de/en)"
node scripts/i18n-check.mjs

echo "› Browser UI (headless Edge, muted)"
# Headless only (--headless=new --mute-audio, no window, no sound); says "skipped" without an Edge or without Node 22.
# SKIP_BROWSER_UI=1 leaves it out (e.g. on a machine where no Edge may start).
if [ "${SKIP_BROWSER_UI:-}" = "1" ]; then
  echo "browser ui: skipped (SKIP_BROWSER_UI=1)"
else
  node scripts/browser-ui-check.mjs
fi

echo "› Tab bar in every skin (headless Edge, muted)"
# Same rules as the browser UI check: headless only, free ports, "skipped" without an Edge. SKIP_BROWSER_UI=1 leaves it out as well.
if [ "${SKIP_BROWSER_UI:-}" = "1" ]; then
  echo "tabs: skipped (SKIP_BROWSER_UI=1)"
else
  node scripts/tabs-check.mjs
fi

echo "› Release notes (newest CHANGELOG version has a Kurz / Summary block)"
if command -v pwsh >/dev/null 2>&1; then
  newest=$(grep -m1 -oE '^## [0-9]+\.[0-9]+\.[0-9]+' CHANGELOG.md | cut -c4-)
  notes=$(mktemp)
  pwsh -NoProfile -File scripts/release-notes.ps1 -Tag "v$newest" -Out "$notes"
  rm -f "$notes"
else
  echo "release notes: skipped (no pwsh)"
fi

echo "✓ alles grün"
