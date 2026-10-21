#!/usr/bin/env bash
# Everything CI checks, before a push: build (Windows projects too), tests, UI syntax.
# Works on Windows (Git Bash), Linux and macOS with the .NET 10 SDK and Node.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "› dotnet build"
dotnet build CouchHud.slnx -c Release -p:EnableWindowsTargeting=true --nologo -v quiet

echo "› dotnet test"
dotnet test CouchHud.slnx -c Release -p:EnableWindowsTargeting=true --no-build --nologo -v quiet

echo "› UI syntax"
# The UI files are ES modules; plain `node --check file.js` can pass a module with a syntax error.
find ui scripts -name '*.js' -o -name '*.mjs' | while read -r f; do
  node --input-type=module --check < "$f" || { echo "  in $f"; exit 1; }
done

echo "› UI logic"
node scripts/ui-logic-check.mjs

echo "› CSS syntax"
node scripts/css-check.mjs

echo "› UI texts (de/en)"
node scripts/i18n-check.mjs

echo "✓ alles grün"
