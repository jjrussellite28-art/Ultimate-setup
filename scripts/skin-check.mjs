// Skin stylesheets: node scripts/skin-check.mjs [dir ...]
// Each dir holds skin folders (one sub folder per skin with a skin.css), like the catalog's skins/ folder, or is a skin folder itself (a
// skin.css inside; scripts/catalog-entry.ps1 calls it so for the folder it packs). Without arguments the sibling checkout
// ../couchhud-catalog-src/skins (also found from a git worktree, or via COUCHHUD_CATALOG_SRC) is checked when it exists. The rules are in
// scripts/skin-rules.mjs: since 0.7 the tab bar cannot be reached by a skin's selectors (closed shadow root in the top layer), so what is
// left are problems that fail the check (a @font-face that shrinks letters or replaces a system font, html/body rules that hide the page)
// and notes that do not fail it (rules for the old bar that do nothing now, --tab-* values that get clamped).
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { tabNotes, tabProblems } from './skin-rules.mjs';
import { catalogSkinsDir } from './catalog-src.mjs';

const dirs = process.argv.slice(2).length ? process.argv.slice(2) : [catalogSkinsDir()];
const bad = [];
const notes = [];
const files = [];
for (const dir of dirs) {
  if (!existsSync(dir)) continue;
  if (existsSync(join(dir, 'skin.css'))) {
    files.push(join(dir, 'skin.css'));
    continue;
  }
  for (const d of readdirSync(dir, { withFileTypes: true })) {
    const file = join(dir, d.name, 'skin.css');
    if (d.isDirectory() && existsSync(file)) files.push(file);
  }
}
for (const file of files) {
  const css = readFileSync(file, 'utf8');
  for (const p of tabProblems(css)) bad.push(`${file}: ${p}`);
  for (const n of tabNotes(css)) notes.push(`${file}: ${n}`);
}
if (notes.length) console.log(`skins: ${notes.length} note(s), not failing:\n${notes.map((n) => `  note: ${n}`).join('\n')}`);
if (bad.length) {
  console.error(bad.join('\n'));
  console.error(`skins: ${bad.length} problem(s)`);
  process.exit(1);
}
// Nothing to look at is said out loud, not reported as a pass.
if (!files.length) {
  // RELEASE=1 (check.sh) does not accept a check that looked at nothing.
  console.error('skins: SKIPPED, no catalog skins found (set COUCHHUD_CATALOG_SRC to the couchhud-catalog-src checkout)');
  if (process.env.RELEASE === '1') process.exit(1);
} else console.log(`skins: no problems (${files.length} skins)`);
