// The tab bar in the preview (mock API, headless Edge, sound off). The bar is a closed shadow root in the top layer (ui/core/tab-bar.js); the
// preview (never the app) exposes the root as window.__couchhudTabBar for exactly this check.
//
// 1. The catalog skins and the default skin with the plugin tabs (?hatab: Smart Home, Lumen) at 1280x720 and 1920x1080, in two states:
//    "stress" (the downloads chip and every status icon shown) and "normal" (only the bell, weather and clock). Checked in every state of an
//    LB/RB walk over all tabs: no tab is invisible, none is narrower than the minimum (unless its label is shorter anyway), nothing is cut
//    while the bar scrolls (except names over the maximum), a tab that is cut shows at least 2rem or none of it, no tab part lies under an
//    arrow, the focused tab is whole, and Smart Home and Lumen are reached with LB/RB. In the normal state Smart Home must be fully readable
//    without scrolling in the default skin and in Stardust at 1920. The focused tab is never cut or faded, no arrow shows while the bar
//    fits, and on the setup assistant no tab is visible (another run per skin). A screenshot with Smart Home focused goes to --out.
// 2. Every skin in scripts/fixtures/skins (the hostile ones, the reviewers' skins of 0.6.4, the decorative one): none may need a fall back.
//    Each stays active, every tab is in the top layer, hit at its middle by the bar itself (nothing over it), set in an allowed font at a
//    readable size, without stroke, size adjust or more than one capped glow, the host unchanged, and its letters are really drawn: the
//    screenshot is read back (scripts/png.mjs) and each tab must show ink. "hostile" also walks LB/RB.
// 3b. The skin stylesheet sanitizer (scripts/skin-css.js) against Edge's own parser, see the "sanitizer" block.
// 3. The top layer: what covers the whole screen by design (boot screen, screensaver, pointer) stands over the bar while open and leaves the top layer when
//    shut; the bar stays when they close. The guide menu, the dialog, toasts and notification cards stand UNDER the bar on purpose (the bar is hit at its
//    whole line while they are open; the guide menu starts below it). Every fixture is also read with the guide menu and a dialog open.
//
//   node scripts/tabs-check.mjs [--catalog DIR] [--out DIR] [--skins a,b] [--only fixture,fixture] [--port 5321] [--debug 9421]  (the catalog skins folder defaults to ../couchhud-catalog-src/skins)
//
// Needs Edge (env EDGE or the usual places) and runs only headless (--headless=new --mute-audio, no window, no sound; no app is started).
// Part of scripts/check.sh and the CI. Without an Edge it says "skipped" and exits 0; in CI (CI=true) the same is a failure. Free ports.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { catalogSkinsDir } from './catalog-src.mjs';
import { findEdge, freePort } from './check-env.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { catalog: catalogSkinsDir(), out: join(here, '..', 'tabs-shots') };
for (let i = 0; i < args.length; i += 2) opt[args[i].replace(/^--/, '')] = args[i + 1];
opt.catalog = resolve(opt.catalog);
const skins = ['default', ...(existsSync(opt.catalog) ? readdirSync(opt.catalog, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name) : [])]
  .filter((s) => !opt.skins || opt.skins.split(',').includes(s));

const edge = findEdge();
if (!edge) {
  const ci = /^(1|true)$/i.test(process.env.CI ?? '');
  console.log(`tabs: ${ci ? 'FAIL, in CI not skipped' : 'skipped'} (no Microsoft Edge found; set EDGE to msedge.exe)`);
  process.exit(ci ? 1 : 0);
}
process.env.EDGE = edge;

// --open 0 leaves out the guide menu and the dialog readings (for runs against an older ui/ whose menu stood over the bar).
const SHOW_STATUS = `(() => { for (const id of ['session-status', 'parental-status', 'downloads-status', 'weather']) { const n = document.getElementById(id); if (!n) continue; n.hidden = false; if (!n.textContent.trim()) n.textContent = '99 min'; } return 1; })()`;
const NORMAL_STATUS = `(() => { const st = document.createElement('style'); st.textContent = '#session-status, #parental-status, #downloads-status { display: none !important; }'; document.head.append(st); return 1; })()`;
// The closed root, through the preview's test hook.
const ROOT = `const { root, host } = window.__couchhudTabBar; const rem = innerHeight / 54;`;
const CHECK_FN = `() => {
  ${ROOT}
  const list = root.querySelector('.list'); const lr = list.getBoundingClientRect();
  const lis = [...list.children]; const pins = lis.filter((l) => l.classList.contains('pinned'));
  const pinLeft = pins.length ? Math.min(...pins.map((p) => p.getBoundingClientRect().left)) : lr.right;
  const st = document.getElementById('status').getBoundingClientRect();
  const arrows = [...root.querySelectorAll('.more')].filter((n) => getComputedStyle(n).display !== 'none').map((n) => n.getBoundingClientRect());
  const scrolls = list.scrollWidth > list.clientWidth + 1;
  const probs = [];
  for (const l of lis) {
    const cs = getComputedStyle(l); const r = l.getBoundingClientRect(); const pinned = l.classList.contains('pinned');
    if (cs.display === 'none' || cs.visibility === 'hidden' || Number(cs.opacity) === 0 || r.width < 2) probs.push('invisible: ' + l.textContent);
    if (r.width < Math.min(l.scrollWidth, 6.5 * rem) - 1.5) probs.push('smaller than the minimum: ' + l.textContent + ' ' + Math.round(r.width));
    if (l.getAttribute('aria-label') !== l.textContent) probs.push('no full name: ' + l.textContent);
    if (scrolls && l.scrollWidth > l.clientWidth + 1.5 && r.width < 11.9 * rem) probs.push('cut while the bar scrolls: ' + l.textContent);
    if (!pinned) {
      let visL = r.left; let visR = r.right;
      const m = /inset\\(([^)]*)\\)/.exec(cs.clipPath || '');
      if (m) {
        const p = m[1].trim().split(/\\s+/);
        const right = p[1] === '100%' ? r.width : parseFloat(p[1]); const left = parseFloat(p[3] ?? p[1]);
        visL = r.left + (Number.isFinite(left) ? left : 0); visR = r.right - (Number.isFinite(right) ? right : 0);
      }
      const shown = Math.max(0, visR - visL);
      if (shown > 0.5 && shown < Math.min(2 * rem, r.width) - 1) probs.push('a stub of ' + Math.round(shown) + 'px of ' + l.textContent);
      if (shown > 0.5) for (const a of arrows) if (visL < a.right - 1 && visR > a.left + 1) probs.push('text under an arrow: ' + l.textContent);
    }
  }
  const act = list.querySelector('li.active'); let whole = true;
  if (act && ((act.style.clipPath && act.style.clipPath !== 'none') || (act.style.webkitMaskImage && act.style.webkitMaskImage !== 'none'))) probs.push('the focused tab is cut or faded: ' + act.textContent);
  if (!scrolls && arrows.length) probs.push('an arrow shows while the bar fits');
  if (act && !act.classList.contains('pinned')) { const r = act.getBoundingClientRect(); whole = r.left >= lr.left - 1 && r.right <= pinLeft + 1; }
  if (!whole) probs.push('the focused tab is not whole: ' + act.textContent);
  if (lr.right > st.left + 1) probs.push('the tab bar runs into the status area');
  const sh = lis.find((l) => l.textContent === 'Smart Home');
  return JSON.stringify({ skin: document.documentElement.dataset.skin, active: act && act.textContent, probs, hints: arrows.length, scrolls, smartHomeReadable: !!sh && sh.scrollWidth <= sh.clientWidth + 1, widths: lis.map((l) => l.textContent + ':' + Math.round(l.getBoundingClientRect().width)), lefts: lis.map((l) => l.getBoundingClientRect().left) });
}`.replace(/\s*\n\s*/g, ' ');
const CHECK = `(${CHECK_FN})()`;

const WIZARD = `(() => { ${ROOT} return JSON.stringify({ view: document.body.dataset.view, shown: [...root.querySelectorAll('li')].filter((x) => getComputedStyle(x).visibility !== 'hidden').length, arrows: [...root.querySelectorAll('.more')].filter((n) => getComputedStyle(n).visibility !== 'hidden' && getComputedStyle(n).display !== 'none').length }); })()`;

let failed = 0;
let n = 0;
const run = (extra, size, steps) => spawnSync(process.execPath, [join(here, 'ui-shot.mjs'), '--out', opt.out, '--port', String(extra.port), '--debug', String(extra.debug), '--size', size, '--catalog', extra.catalog, '--query', extra.query ?? 'hatab=1', ...steps], { encoding: 'utf8', timeout: 240000 });
const lines = (run, prefix, twice = true) => (run.stdout || '').split('\n').filter((l) => l.startsWith(prefix)).map((l) => { const v = JSON.parse(l.slice(prefix.length)); return twice ? (typeof v === 'string' ? JSON.parse(v) : null) : v; }).filter((v) => v !== null);
const ports = async () => ({ port: await freePort(), debug: await freePort() });

for (const mode of ['stress', 'normal']) {
  for (const size of ['1280x720', '1920x1080']) {
    for (const skin of skins) {
      const shot = `tabs4-${mode === 'normal' ? 'normal-' : ''}${skin}-${size}`;
      const steps = [`wait:2000`, ...(skin === 'default' ? [] : [`skin:user:${skin}`]), 'wait:1200', `eval:${mode === 'stress' ? SHOW_STATUS : NORMAL_STATUS}`, 'wait:900', `eval:${CHECK}`];
      for (let i = 0; i < 9; i++) steps.push('key:e', 'wait:150', `eval:${CHECK}`, ...(i === 5 ? ['wait:1400', `shot:${shot}`] : []));
      const results = lines(run({ ...(await ports()), catalog: opt.catalog }, size, steps), 'eval -> ');
      n++;
      const seen = new Set(results.map((r) => r.active));
      const problems = [...new Set(results.flatMap((r) => r.probs))];
      for (const name of ['Smart Home', 'Lumen']) if (!seen.has(name)) problems.push(`${name} was not reached with LB/RB`);
      if (results.length < 10) problems.push(`only ${results.length} states read`);
      const first = results[0];
      // A catalog skin must stay: a fall back to the default skin would pass every other check unseen.
      const wanted = skin === 'default' ? 'default' : `user:${skin}`;
      if (results.some((r) => r.skin !== wanted)) problems.push(`the skin was replaced by ${[...new Set(results.map((r) => r.skin))].join(', ')}`);
      if (mode === 'normal' && size === '1920x1080' && ['default', 'stardust'].includes(skin) && first && (first.scrolls || !first.smartHomeReadable)) problems.push('normal state: Smart Home is not fully readable without scrolling');
      // The mode (fits / shrinks / scrolls) must not depend on the focus, and with LB/RB the tabs that are not the two involved must not move
      // more than a few pixels while the bar does not scroll (the focused tab takes from the others only what the one before gives back).
      let jump = 0;
      let shifted = 0;
      const modes = new Set(results.map((r) => r.scrolls));
      if (modes.size > 1) problems.push('the bar switches between scrolling and not scrolling with the focus');
      for (let i = 1; i < results.length; i++) {
        const p = results[i - 1]; const q = results[i];
        if (p.scrolls || q.scrolls || p.lefts.length !== q.lefts.length) continue;
        p.lefts.forEach((x, k) => {
          const name = p.widths[k].split(':')[0];
          const d = Math.abs(x - q.lefts[k]);
          jump = Math.max(jump, d);
          if (name !== p.active && name !== q.active) shifted = Math.max(shifted, d);
        });
      }
      if (shifted > 8) problems.push(`tabs move ${Math.round(shifted)}px when the focus moves (at most 8 allowed)`);
      console.log(`${problems.length ? 'FAIL' : 'ok  '} ${mode} ${skin} ${size}: ${first ? `${first.scrolls ? 'scrolls' : 'fits'}, Smart Home ${first.smartHomeReadable ? 'readable' : 'cut'}; jump ${Math.round(jump)}px, others ${Math.round(shifted)}px; ${first.widths.join(' ')}` : '-'}`);
      for (const p of problems) console.log(`     ${p}`);
      if (problems.length) failed++;
    }
  }
}

// The fixtures: skins that try to take the bar apart, for scripts/tabs-check.mjs, never for the catalog. All of them must leave every tab visible,
// readable and on top with the skin still active (no fall back).
const FIXTURES = join(here, 'fixtures', 'skins');
const READ_FN = `async () => {
  ${ROOT}
  const kit = await import('/core/tab-skin.js');
  const probs = [];
  const hs = getComputedStyle(host);
  if (!host.matches(':popover-open')) probs.push('the bar is not in the top layer');
  const changed = ['display', 'visibility', 'opacity', 'filter', 'clipPath', 'zoom', 'transform', 'contentVisibility', 'mixBlendMode', 'backdropFilter', 'maskImage', 'contain', 'position', 'pointerEvents', 'direction', 'writingMode', 'rotate', 'scale', 'perspective']
    .filter((k) => ({ display: 'block', visibility: 'visible', opacity: '1', filter: 'none', clipPath: 'none', zoom: '1', transform: 'none', contentVisibility: 'visible', mixBlendMode: 'normal', backdropFilter: 'none', maskImage: 'none', contain: 'none', position: 'fixed', pointerEvents: 'auto', direction: 'ltr', writingMode: 'horizontal-tb', rotate: 'none', scale: 'none', perspective: 'none' })[k] !== hs[k]);
  if (changed.length) probs.push('the host is changed: ' + changed.join(', '));
  const hr = host.getBoundingClientRect();
  if (hr.top < -8 || hr.top > 4 || hr.left < -8 || hr.right > innerWidth + 8 || hr.height < 2 * rem || hr.bottom > innerHeight * 0.3 || hr.width < innerWidth - 2) probs.push('the bar is not at the top of the viewport or squashed: ' + Math.round(hr.left) + ',' + Math.round(hr.top) + ' ' + Math.round(hr.width) + 'x' + Math.round(hr.height));
  for (const p of ['::before', '::after']) { const c = getComputedStyle(host, p).content; if (!['none', 'normal'].includes(c)) probs.push('generated content on the host ' + p); }
  for (const id of ['html', 'body']) { const cs = getComputedStyle(id === 'html' ? document.documentElement : document.body); if (cs.zoom !== '1' || cs.contentVisibility !== 'visible' || cs.display === 'none') probs.push(id + ' is changed (zoom, content-visibility or display)'); }
  for (const n of document.querySelectorAll(':popover-open')) { if (n === host) continue; const q = n.getBoundingClientRect(); if (q.width * q.height > 0.3 * innerWidth * innerHeight) probs.push('a full-screen top-layer element while nothing is open: #' + n.id); }
  const navW = root.querySelector('.tabs').getBoundingClientRect().width;
  if (navW < 0.5 * innerWidth) probs.push('the tabs are squeezed to ' + Math.round(navW) + 'px of ' + innerWidth + ' by the status area');
  const lis = [...root.querySelector('.list').children];
  const rects = [];
  const sizes = [];
  const fontOk = (stack) => stack.split(',').every((s) => { const f = kit.familyKey(s); return kit.GENERIC_FONTS.includes(f) || kit.SYSTEM_FONTS.some((nm) => nm.toLowerCase() === f); });
  for (const li of lis) {
    const name = li.getAttribute('aria-label'); const r = li.getBoundingClientRect(); const cs = getComputedStyle(li);
    const label = li.querySelector('span') || li; const ls = getComputedStyle(label);
    if (li.style.clipPath && li.style.clipPath !== 'none') continue;
    sizes.push(Math.round(r.height) + 'px/' + cs.fontSize);
    if (r.width < 2 * rem || r.height < 1.2 * rem) probs.push('squashed: ' + name + ' ' + Math.round(r.width) + 'x' + Math.round(r.height));
    const x = r.left + r.width / 2; const y = r.top + r.height / 2;
    if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) { probs.push('off the screen: ' + name); continue; }
    const hit = document.elementFromPoint(x, y);
    if (hit !== host) probs.push('covered: ' + name + ' by ' + (hit ? hit.tagName + '#' + hit.id : 'nothing'));
    else if (root.elementFromPoint(x, y)?.closest('li') !== li) probs.push('not hit itself: ' + name);
    if (Number(cs.opacity) < 1 || cs.visibility !== 'visible' || cs.filter !== 'none' || cs.mixBlendMode !== 'normal' || cs.backdropFilter !== 'none') probs.push('faded: ' + name);
    if (!fontOk(cs.fontFamily)) probs.push('a font outside the allowlist: ' + name + ' ' + cs.fontFamily);
    if (parseFloat(cs.fontSize) < 0.85 * rem) probs.push('tiny letters: ' + name + ' ' + cs.fontSize);
    if (ls.webkitTextStrokeWidth !== '0px' || ls.fontSizeAdjust !== 'none') probs.push('stroke or size adjust on the letters: ' + name);
    const a = /^rgba\\([^,)]+,[^,)]+,[^,)]+,\\s*([\\d.]+)\\s*\\)$/.exec(ls.color); if (a && Number(a[1]) < 0.3) probs.push('faint letters: ' + name + ' ' + ls.color);
    const shadows = ls.textShadow === 'none' ? [] : ls.textShadow.split(/,(?![^(]*\\))/);
    if (shadows.length > 1 || shadows.some((s) => { const m = /\\)\\s+(-?[\\d.]+)px\\s+(-?[\\d.]+)px\\s+([\\d.]+)px/.exec(s) || /(-?[\\d.]+)px\\s+(-?[\\d.]+)px\\s+([\\d.]+)px/.exec(s); return m && (Math.abs(Number(m[1])) > 1 || Math.abs(Number(m[2])) > 1 || Number(m[3]) > 1.25 * rem); })) probs.push('more shadow than the capped glow: ' + name + ' ' + ls.textShadow);
    rects.push({ name, x: r.left, y: r.top, w: r.width, h: r.height });
  }
  window.__ink = rects;
  return JSON.stringify({ probs, skin: document.documentElement.dataset.skin, tabs: lis.length, sizes: [...new Set(sizes)], bar: Math.round(hr.left) + ',' + Math.round(hr.top) + ' ' + Math.round(hr.width) + 'x' + Math.round(hr.height), fonts: [...new Set(lis.map((l) => getComputedStyle(l).fontFamily))] });
}`.replace(/\s*\n\s*/g, ' ');
const READ = `(${READ_FN})()`;
const fixtureNames = existsSync(FIXTURES) ? readdirSync(FIXTURES, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name).filter((nm) => !opt.only || opt.only.split(',').includes(nm)) : [];
const fixtureRun = !opt.skins || opt.skins.split(',').includes('fixtures');
for (const name of fixtureRun ? fixtureNames : []) {
  const both = name === 'hostile' || name === 'hostile-host' || name === 'hostile-vars';
  for (const size of both ? ['1280x720', '1920x1080'] : ['1280x720']) {
    // The status area is only the normal one; the downloads chip is not what these skins are about.
    const steps = ['wait:2000', `skin:user:${name}`, 'wait:3500', `eval:${NORMAL_STATUS}`, 'wait:600'];
    // A skin that only changes with a media query is looked at after the query turned on while the page runs (the bar must read it again at once).
    if (name === 'font-media') steps.push('media:prefers-contrast=more', 'wait:700');
    // The pointer (in the top layer while the mouse is in use) and the quick menu (while it is open) are looked at where a skin could make them cover the bar.
    // Every skin is looked at three times: as it is (with the mouse pointer in use, so it is in the top layer), with the guide menu open, with a dialog open. The guide
    // menu and the dialog stand under the bar: the bar must be hit and its letters drawn exactly as without them (only dimmed).
    steps.push('mouse:640,400', 'wait:200');
    steps.push(`eval:${READ}`, 'ink:__ink', `shot:fixture-${name}-${size}`);
    if (opt.open !== '0') steps.push('key:Home', 'wait:900', `eval:${READ}`, 'ink:__ink', `shot:fixture-${name}-quick-${size}`, 'key:Escape', 'wait:900', 'key:m', 'wait:700', `eval:${READ}`, 'ink:__ink', 'key:Escape');
    if (name === 'hostile') {
      // Both checks are put on the page once and called by name: eighteen copies would not fit on a Windows command line.
      steps.splice(1, 0, `eval:(window.__read = ${READ_FN}, window.__check = ${CHECK_FN}, 1)`);
      for (let i = 0; i < 9; i++) steps.push('key:e', 'wait:150', 'eval:window.__read()', 'eval:window.__check()');
    }
    const result = run({ ...(await ports()), catalog: FIXTURES }, size, steps);
    const evals = lines(result, 'eval -> ');
    const reads = evals.filter((r) => 'tabs' in r);
    const checks = evals.filter((r) => 'smartHomeReadable' in r);
    const inks = lines(result, 'ink -> ', false);
    n++;
    const problems = [...new Set([...reads, ...checks].flatMap((r) => r.probs))];
    if (!reads.length) problems.push(`no reading (status ${result.status} ${(result.stderr || result.stdout || '').slice(0, 200)})`);
    if ([...reads, ...checks].some((r) => r.skin !== `user:${name}`)) problems.push('the skin was replaced, though nothing needs it to be');
    if (!inks.length) problems.push('no screenshot reading');
    inks.forEach((tab, k) => {
      for (const q of tab) {
        if (q.ink < 0.012) problems.push(`no letters drawn in the screenshot${k ? ` (${k === 1 ? 'quick menu' : 'dialog'} open)` : ''}: ${q.name} (ink ${q.ink})`);
        else if (q.contrast < (k ? 1.4 : 3)) problems.push(`letters against the pixels behind them: contrast ${q.contrast} under 3: ${q.name}`);
      }
    });
    if (reads[0] && reads[0].tabs < 5) problems.push(`only ${reads[0].tabs} tabs`);
    console.log(`${problems.length ? 'FAIL' : 'ok  '} fixture ${name} ${size}: ${reads.length} reading(s), ink min ${inks[0]?.length ? Math.min(...inks[0].map((q) => q.ink)) : '-'} contrast min ${inks[0]?.length ? Math.min(...inks[0].map((q) => q.contrast)) : '-'}${reads[0] ? `, tabs ${reads[0].sizes.join(' ')}, bar ${reads[0].bar}` : ''}`);
    for (const p of problems) console.log(`     ${p}`);
    if (problems.length) failed++;
  }
}

// The top layer: the overlays that may stand over the bar do so while they are open, and leave it when shut.
if (!opt.skins || opt.skins.split(',').includes('layers')) {
  const LAYERS = (what, expect) => `(async () => {
    const L = await import('/core/layers.js'); const { host } = window.__couchhudTabBar;
    const up = L.layersUp(); const at = (x, y) => document.elementFromPoint(x, y);
    const inside = (node, hit) => !!node && !!hit && (hit === node || node.contains(hit));
    const probs = [];
    const node = ${what === 'boot' ? "document.getElementById('boot')" : `document.getElementById('${what}')`};
    ${expect};
    if (!host.matches(':popover-open')) probs.push('the bar left the top layer');
    return JSON.stringify({ what: '${what}', up, probs });
  })()`.replace(/\s*\n\s*/g, ' ');
  const open = (id, x, y) => `if (!node || !node.matches(':popover-open') || !up.includes('${id}')) probs.push('${id} is not in the top layer while open'); else if (!inside(node, at(${x}, ${y}))) probs.push('${id} does not stand over the bar at ${x},${y}: ' + (at(${x}, ${y}) || {}).id);`;
  const shut = (id) => `if (node && node.matches(':popover-open')) probs.push('${id} is still in the top layer after it was shut');`;
  const steps = [
    'wait:2000',
    // Toasts and the notification cards stay below the bar on purpose: the mock shows a card at the start.
    `eval:${LAYERS('notify-stack', `if (!node || !node.childElementCount) probs.push('no notification card to look at'); else if (node.matches(':popover-open') || up.includes('notify-stack')) probs.push('the notification stack is in the top layer');`)}`,
    // Boot screen: made open again for a moment, then done.
    `eval:(() => { document.getElementById('boot').classList.remove('done'); return 1; })()`, 'wait:300', `eval:${LAYERS('boot', open('boot', 600, 40))}`,
    `eval:(() => { document.getElementById('boot').classList.add('done'); return 1; })()`, 'wait:1500', `eval:${LAYERS('boot', shut('boot'))}`,
    // Quick menu: over the right of the bar, the tabs beside it stay hit by the bar; gone when shut.
    // The guide menu and the dialog stand UNDER the bar (never in the top layer): the bar is hit everywhere on its line while they are open, the menu starts below it.
    'key:Home', 'wait:900', `eval:${LAYERS('quick', `if (!node || !node.classList.contains('open')) probs.push('the quick menu did not open'); if (node && node.matches(':popover-open')) probs.push('quick is in the top layer'); if (at(1850, 40) !== host || at(300, 40) !== host) probs.push('the bar is not hit at its line while the quick menu is open'); if (node && node.getBoundingClientRect().top < host.getBoundingClientRect().bottom - 2) probs.push('the quick menu starts above the bottom of the bar');`)}`,
    'key:Escape', 'wait:1600', `eval:${LAYERS('quick', `if (node && node.matches(':popover-open')) probs.push('quick is in the top layer')`)}`,
    // The power menu (the Start button): a dialog over the whole screen.
    'key:m', 'wait:700', `eval:${LAYERS('overlay', `if (!node || node.hidden) probs.push('the dialog did not open'); if (node && node.matches(':popover-open')) probs.push('overlay is in the top layer'); if (at(600, 40) !== host) probs.push('the bar is not hit at its line while the dialog is open');`)}`,
    'key:Escape', 'wait:1500', `eval:${LAYERS('overlay', `if (node && node.matches(':popover-open')) probs.push('overlay is in the top layer')`)}`,
    // Screensaver.
    `eval:import('/core/screensaver.js').then((m) => { m.startScreensaver('black'); return 1; })`, 'wait:700', `eval:${LAYERS('screensaver', open('screensaver', 600, 40))}`,
    `eval:import('/core/screensaver.js').then((m) => { m.stopScreensaver(); return 1; })`, 'wait:2500', `eval:${LAYERS('screensaver', `if (document.getElementById('screensaver')) probs.push('the screensaver is still there')`)}`,
    // Toasts and the notification cards stay below the bar on purpose (the mock shows a card): never in the top layer, the bar is hit at its tabs while they show.
    `eval:import('/core/feedback.js').then((m) => { m.toast('A toast over the bar', 3000); return 1; })`, 'wait:400', `eval:${LAYERS('toast', `if (!node || node.hidden || node.matches(':popover-open') || up.includes('toast') || at(600, 40) !== host) probs.push('the toast is over the bar or in the top layer');`)}`,
    // The pointer is the highest of the core's layers.
    'mouse:700,40', 'wait:300', `eval:${LAYERS('cursor', `if (!node || !node.matches(':popover-open')) probs.push('the pointer is not in the top layer'); if (up.at(-1) !== 'cursor' && !up.includes('screensaver')) probs.push('the pointer is not the last of ' + up.join(','));`)}`
  ];
  const result = run({ ...(await ports()), catalog: opt.catalog }, '1920x1080', steps);
  const reads = lines(result, 'eval -> ').filter((r) => r.what);
  n++;
  const problems = [...new Set(reads.flatMap((r) => r.probs))];
  if (reads.length < 11) problems.push(`only ${reads.length} readings (status ${result.status} ${(result.stderr || result.stdout || '').slice(0, 200)})`);
  // The order of the layers is the order of their ranks (the old z-index), always.
  for (const r of reads) {
    const rank = { cursor: 100, screensaver: 101, launch: 150, boot: 200 };
    for (let i = 1; i < r.up.length; i++) if (rank[r.up[i]] < rank[r.up[i - 1]]) problems.push(`layers out of order in ${r.what}: ${r.up.join(', ')}`);
  }
  console.log(`${problems.length ? 'FAIL' : 'ok  '} layers: ${reads.length} readings (${reads.map((r) => `${r.what}[${r.up.join(',')}]`).join(' ').slice(0, 200)})`);
  for (const p of problems) console.log(`     ${p}`);
  if (problems.length) failed++;
}

// The skin stylesheet sanitizer against a real parser: every output (the shared cases and a fixed-seed fuzz corpus of 2400 token soups) is parsed by Edge as a
// stylesheet, and no @layer or @import rule may be left. A raw @layer sheet is the control: the oracle has to see it, or it sees nothing.
if (!opt.skins || opt.skins.split(',').includes('sanitizer')) {
  const require = createRequire(import.meta.url);
  const { sanitize } = require('./skin-css.js');
  const { fuzz } = await import('./gen-skin-css-cases.mjs');
  const shared = JSON.parse(readFileSync(join(here, '..', 'tests', 'skin-css-cases.json'), 'utf8'));
  const corpus = { control: 'a{b:c}@layer ch{d{e:f}}@import "x.css";' };
  const outputs = { control: corpus.control };
  shared.forEach((c) => { outputs['shared-' + c.name] = sanitize(c.input).css; });
  fuzz(2400, 7777).forEach((input, i) => { outputs['fuzz-' + i] = sanitize(input).css; });
  const dir = mkdtempSync(join(tmpdir(), 'couchhud-sanitizer-'));
  mkdirSync(join(dir, 'oracle'));
  writeFileSync(join(dir, 'oracle', 'cases.json'), JSON.stringify(outputs));
  writeFileSync(join(dir, 'oracle', 'skin.css'), 'a{b:c}');
  // The attack on the charset: iso-2022-jp, where ESC ( B produces no character, hides "@layer" from a reading as UTF-8. The same bytes are served with the real
  // header (charset=utf-8) and, as the control, without a charset (the way the host used to send them).
  const iso = Buffer.concat([Buffer.from('@charset "iso-2022-jp";\n@'), Buffer.from([0x1b, 0x28, 0x42]), Buffer.from('layer ch{x{y:z}}')]);
  writeFileSync(join(dir, 'oracle', 'raw-iso.css'), iso);
  writeFileSync(join(dir, 'oracle', 'raw-iso.nocharset.css'), iso);
  writeFileSync(join(dir, 'oracle', 'skin.json'), '{"name":{"de":"o","en":"o"},"description":{"de":"o","en":"o"}}');
  const ORACLE = `(async () => { const d = await (await fetch('/__catalog-skins/oracle/cases.json')).json(); const bad = []; const walk = (rules) => { for (const r of rules) { if (/Layer|Import/.test(r.constructor.name)) return true; if (r.cssRules && walk(r.cssRules)) return true; } return false; }; for (const [k, v] of Object.entries(d)) { const s = document.createElement('style'); s.textContent = v; document.head.append(s); if (walk(s.sheet.cssRules)) bad.push(k); s.remove(); } return JSON.stringify({ n: Object.keys(d).length, bad }); })()`;
  const HEADER = `(async () => { const walk = (rules) => [...rules].some((r) => /Layer|Import/.test(r.constructor.name) || (r.cssRules && walk(r.cssRules))); const load = (href) => new Promise((res) => { const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = href; l.onload = () => res(l); l.onerror = () => res(l); document.head.append(l); }); const served = await load('/__catalog-skins/oracle/raw-iso.css'); const control = await load('/__catalog-skins/oracle/raw-iso.nocharset.css'); const hdr = (await fetch('/__catalog-skins/oracle/raw-iso.css')).headers.get('content-type'); return JSON.stringify({ hdr, served: walk(served.sheet.cssRules), control: walk(control.sheet.cssRules) }); })()`;
  const result = run({ ...(await ports()), catalog: dir }, '800x600', ['wait:1500', `eval:${ORACLE}`, `eval:${HEADER}`]);
  rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  const r = lines(result, 'eval -> ')[0] ?? null;
  const h = lines(result, 'eval -> ')[1] ?? null;
  n++;
  const problems = [];
  if (!h) problems.push('no header reading');
  else {
    if (!/charset=utf-8/i.test(h.hdr ?? '')) problems.push(`the preview does not send charset=utf-8 (${h.hdr})`);
    if (!h.control) problems.push('the control (iso-2022-jp without a charset in the header) shows no layer rule: the attack is not reproduced, the header check proves nothing');
    if (h.served) problems.push('with charset=utf-8 in the header an @charset still turned the sheet into a layer rule');
  }
  if (!r) problems.push(`no reading (${(result.stderr || result.stdout || '').slice(0, 200)})`);
  else {
    if (!r.bad.includes('control')) problems.push('the oracle did not see the control sheet (a raw @layer and @import): it sees nothing');
    for (const k of r.bad.filter((x) => x !== 'control').slice(0, 10)) problems.push(`a layer or import rule is left in the sanitized output of ${k}`);
    if (r.n < 2800) problems.push(`only ${r.n} sheets were checked`);
  }
  console.log(`${problems.length ? 'FAIL' : 'ok  '} sanitizer: ${r ? r.n : 0} sheets parsed by Edge, ${r ? r.bad.filter((x) => x !== 'control').length : '-'} with a layer or import rule left; header ${h ? `${h.hdr}: served ${h.served ? 'LAYER' : 'no rule'}, control without charset ${h.control ? 'layer rule (attack reproduced)' : 'no rule'}` : '-'}`);
  for (const p of problems) console.log(`     ${p}`);
  if (problems.length) failed++;
}

// The setup assistant hides the tabs itself: no tab may show on it, in any skin.
for (const skin of skins) {
  const steps = ['wait:2500', ...(skin === 'default' ? [] : [`skin:user:${skin}`]), 'wait:1500', `eval:${WIZARD}`];
  const result = run({ ...(await ports()), catalog: opt.catalog, query: 'terms&fresh&hatab=1' }, '1280x720', steps);
  const r = lines(result, 'eval -> ')[0] ?? null;
  n++;
  const bad = !r || r.view !== 'setup-wizard' || r.shown !== 0 || r.arrows !== 0;
  console.log(`${bad ? 'FAIL' : 'ok  '} wizard ${skin}: ${r ? `view ${r.view}, tabs shown ${r.shown}, arrows ${r.arrows}` : 'no reading'}`);
  if (bad) failed++;
}
console.log(failed ? `tabs: ${failed} of ${n} runs failed` : `tabs: ok (${n} runs)`);
process.exit(failed ? 1 : 0);
