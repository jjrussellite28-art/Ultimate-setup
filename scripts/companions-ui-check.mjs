// Companion apps and apps in the Guide menu, the HUD side in headless Edge (mock API, no window, no sound): the list page, the rule page with
// the controller's keys (A, B, arrows), unsaved changes asked about on B, only the rule's own fields sent, the Wand offer, and the Guide menu
// row that starts an app through the library's own start.
//
//   node scripts/companions-ui-check.mjs [--port 5199] [--debug 9335]
//
// Env: EDGE (path to msedge.exe). Runs only headless (--headless=new --mute-audio); exit code 1 on a failed check.
import { spawn } from 'node:child_process';
import { createProfile, guard } from './edge-profile.mjs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import http from 'node:http';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { port: '5199', debug: '9335' };
for (let i = 0; i < args.length; i++) if (args[i].startsWith('--')) opt[args[i].slice(2)] = args[++i];

const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const server = spawn(process.execPath, [join(here, 'preview-server.js')], { env: { ...process.env, PORT: opt.port }, stdio: 'ignore' });
const profile = createProfile();
const browser = spawn(edge, [`--remote-debugging-port=${opt.debug}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions',
  '--no-first-run', `--user-data-dir=${profile}`, '--window-size=1920,1080', 'about:blank'], { stdio: 'ignore' });
// Processes and profile folder are cleaned up on every way out (see edge-profile.mjs); removeProfile() waits for Edge to exit first.
const removeProfile = guard({ children: [server], browser, dirs: [profile] });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getJson = (url) => new Promise((res, rej) => http.get(url, (r) => { let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => { try { res(JSON.parse(d)); } catch (e) { rej(e); } }); }).on('error', rej));

async function connect() {
  for (let i = 0; i < 80; i++) {
    try {
      const page = (await getJson(`http://127.0.0.1:${opt.debug}/json`)).find((t) => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(250);
  }
  throw new Error('headless Edge not reachable');
}

const ws = new WebSocket(await connect());
await new Promise((r) => (ws.onopen = r));
let id = 0;
const waiting = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } };
const send = (method, params = {}) => new Promise((res) => { waiting.set(++id, res); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description ?? JSON.stringify(r.result.exceptionDetails));
  return r.result?.result?.value;
};

const VK = { Enter: 13, Escape: 27, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40 };
async function key(k) {
  const text = k === 'Enter' ? '\r' : undefined;
  const base = { key: k, code: k, windowsVirtualKeyCode: VK[k] };
  await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', ...base, text });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(180);
}

const calls = (name) => evaluate(`(window.__mockCalls ?? []).filter((c) => c[0] === ${JSON.stringify(name)})`);
const clearCalls = () => evaluate('window.__mockCalls = []');
// The labels of the rows of the page on top (the check lists), and which one has the focus.
const rows = () => evaluate(`(() => { const list = document.querySelector('.check-list'); return list ? { labels: [...list.children].map((li) => li.querySelector('strong')?.textContent), details: [...list.children].map((li) => li.querySelector('small')?.textContent ?? ''), focus: [...list.children].findIndex((li) => li.classList.contains('focused')), head: document.querySelector('.files-head h2')?.textContent } : null; })()`);
const overlay = () => evaluate(`(() => { const o = document.querySelector('#overlay'); return o && !o.hidden ? o.textContent : null; })()`);

let failed = 0;
function check(ok, what, detail) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}${ok || detail === undefined ? '' : ` -> ${JSON.stringify(detail)}`}`);
  if (!ok) failed++;
}

await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: `http://127.0.0.1:${opt.port}/?lang=en&fresh&quiet` });
await sleep(3500);
await evaluate(`import('/core/boot-gate.js').then((m) => { m.openInput(); document.querySelector("#boot")?.classList.add("done"); return true; })`);

try {
  // 1. The list page: "Add" first, the Wand offer (Wand.exe found, not a companion yet), nothing else yet.
  await evaluate(`import('/components/companions.js').then((m) => m.openCompanionApps())`);
  await sleep(500);
  let page = await rows();
  check(page?.head === 'Companion apps' && page.labels[0] === 'Add a companion app' && page.labels[1] === 'Set up Wand', 'list page: add row and the Wand offer', page);
  check(page?.focus === 0, 'the first row has the focus', page);

  // 2. The Wand offer asks how (every game or Guide menu only); "every game" sets it up through the host.
  await key('ArrowDown');
  await key('Enter');
  await sleep(300);
  // First the full path of the file the host found, with the signature statement; only then how Wand should start.
  check(/valid signature by Wand or WeMod/.test((await overlay()) ?? '') && (await overlay()).includes('Wand.exe'), 'the offer shows the full path first', await overlay());
  await key('Enter');
  await sleep(300);
  check((await overlay())?.includes('Start with every game'), 'then it asks how Wand should start', await overlay());
  await clearCalls();
  await key('Enter');
  await sleep(500);
  const setup = await calls('setupWand');
  check(setup.length === 1 && setup[0][1] === true, 'set up with every game', setup);
  page = await rows();
  check(page?.labels.includes('Wand') && !page.labels.includes('Set up Wand'), 'Wand is a rule now and the offer is gone', page);
  const wandRow = page?.labels.indexOf('Wand') ?? -1;
  check(page?.details[wandRow]?.startsWith('With every game'), 'the row says "With every game"', page?.details[wandRow]);

  // 3. The rule page: switch "End with the game" on, B asks before throwing the change away, Save sends exactly that one field.
  for (let i = page.focus; i < wandRow; i++) await key('ArrowDown');
  await key('Enter');
  await sleep(300);
  page = await rows();
  check(page?.head === 'Wand' && page.labels.includes('End with the game') && page.labels.includes('Save') && page.labels.includes('Remove'), 'rule page rows', page);
  const closeRow = page.labels.indexOf('End with the game');
  for (let i = 0; i < closeRow; i++) await key('ArrowDown');
  await key('Enter');
  await sleep(200);
  check((await evaluate(`document.querySelectorAll('.check-list li')[${closeRow}].classList.contains('checked') || document.querySelectorAll('.check-list li')[${closeRow}].querySelector('.check-box').textContent === '✓'`)) === true, 'the switch shows as on');
  await key('Escape');
  await sleep(300);
  check((await overlay())?.includes('Discard the changes?'), 'B with unsaved changes asks first', await overlay());
  await key('Escape');
  await sleep(300);
  page = await rows();
  check(page?.head === 'Wand', 'cancelling the question stays on the page', page);
  await clearCalls();
  const saveRow = page.labels.indexOf('Save');
  for (let i = page.focus; i < saveRow; i++) await key('ArrowDown');
  await key('Enter');
  await sleep(500);
  const saved = await calls('setCompanion');
  check(saved.length === 1 && saved[0][1] === 'custom:wand' && JSON.stringify(saved[0][2]) === JSON.stringify({ closeWithGame: true }), 'save sends only the changed field and the app id', saved);
  page = await rows();
  check(page?.head === 'Companion apps' && /Ends with the game/.test(page.details[page.labels.indexOf('Wand')]), 'back on the list with the new state', page);

  // 4. A rule for chosen games: the game picker is a list with checks and B leaves it.
  await evaluate(`import('/core/api.js').then(({ api }) => api.setCompanion('custom:kodi', { mode: 'games', gameIds: ['steam:1245620'] }))`);
  await evaluate(`import('/core/nav.js').then(({ pop }) => pop())`);
  await evaluate(`import('/components/companions.js').then((m) => m.openCompanionApps())`);
  await sleep(500);
  page = await rows();
  check(page?.labels.includes('Kodi') && /With 1 game/.test(page.details[page.labels.indexOf('Kodi')]), 'a rule for one game says so', page);

  // 5. The Guide menu: an app in the list shows as a row and A starts it through the library's own start (launch).
  await evaluate(`import('/core/nav.js').then(({ clearStack }) => clearStack())`);
  await evaluate(`import('/core/api.js').then(({ api }) => api.setGuideApp('custom:wand', true))`);
  await evaluate(`import('/components/quick-menu.js').then((m) => m.openQuickMenu())`);
  await sleep(700);
  const quick = await evaluate(`[...document.querySelectorAll('#quick .quick-row')].map((li) => li.textContent)`);
  const at = quick.findIndex((t) => t.startsWith('Wand'));
  check(at >= 0 && /Start app/.test(quick[at]), 'the Guide menu has a row for the app', quick);
  await clearCalls();
  await evaluate(`import('/components/quick-menu.js').then(({ quick }) => { quick.index = ${at}; import('/components/quick-menu.js').then((m) => m.renderQuick()); })`);
  await key('Enter');
  await sleep(500);
  const launched = await calls('launch');
  check(launched.length === 1 && launched[0][1] === 'custom:wand', 'A starts the app through the library start', launched);
} catch (e) {
  console.log('FAIL', e.message);
  failed++;
} finally {
  console.log(failed ? `companions ui: ${failed} failed` : 'companions ui: ok');
  await removeProfile();
  process.exit(failed ? 1 : 0);
}
