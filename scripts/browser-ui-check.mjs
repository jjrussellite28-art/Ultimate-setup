// The couch browser's HUD side in headless Edge (mock API, no window, no sound): the controller's place between the HUD
// and the page stays in step with the host, the page's questions and downloads are asked in the HUD, typing into the page
// works, and the page never stays "focused" while the host steers the HUD.
//
//   node scripts/browser-ui-check.mjs [--port N] [--debug N]   (free ports by default, so runs in parallel never meet)
//
// Env: EDGE (path to msedge.exe). Runs only headless (--headless=new --mute-audio); exit code 1 on a failed check. Without an Edge
// (none at EDGE or the usual places) or Node 22 it says "skipped" and exits 0, so scripts/check.sh stays usable on machines without
// Edge; in CI (CI=true) the same is a failure (exit 1), so a runner without them never passes unseen. A watchdog ends the run
// after 180 s (cleanup, exit 1).
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import http from 'node:http';
import net from 'node:net';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = { port: '', debug: '' };
for (let i = 0; i < args.length; i++) if (args[i].startsWith('--')) opt[args[i].slice(2)] = args[++i];
// Ports: a fixed default made two runs at the same time (other worktrees, CI next to a local run) share one DevTools port and
// one preview server, so a run steered the other's browser and its page ("Failed to resolve module specifier '/views/browser.js'"
// from a document that was not the app, then a wall of FAILs). Without --port/--debug a free port is asked from the system.
const freePort = () => new Promise((res, rej) => {
  const s = net.createServer();
  s.once('error', rej);
  s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => res(String(port))); });
});
if (!opt.port) opt.port = await freePort();
if (!opt.debug) opt.debug = await freePort();

// EDGE when set, else the usual install places (Windows, Linux, macOS). Only a file that exists is started.
const edgeCandidates = process.env.EDGE ? [process.env.EDGE] : [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  '/usr/bin/microsoft-edge',
  '/usr/bin/microsoft-edge-stable',
  '/opt/microsoft/msedge/msedge',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge'
];
const edge = edgeCandidates.find((p) => existsSync(p));
// Locally a missing Edge or Node only skips; in CI it fails.
const skip = (why) => {
  const ci = /^(1|true)$/i.test(process.env.CI ?? '');
  console.log(`browser ui: ${ci ? 'FAIL, in CI not skipped' : 'skipped'} (${why})`);
  process.exit(ci ? 1 : 0);
};
if (!edge) {
  skip(`no Microsoft Edge found${process.env.EDGE ? ` at EDGE=${process.env.EDGE}` : '; set EDGE to msedge.exe'}`);
}
// The DevTools connection uses Node's own WebSocket (Node 22 and later).
if (typeof WebSocket === 'undefined') {
  skip(`Node ${process.version} has no built-in WebSocket, Node 22 or later needed`);
}
const server = spawn(process.execPath, [join(here, 'preview-server.js')], { env: { ...process.env, PORT: opt.port }, stdio: 'ignore' });
let serverGone = null;
server.on('exit', (code) => { serverGone = code; });
const profile = mkdtempSync(join(tmpdir(), 'couchhud-edge-'));
const browser = spawn(edge, [`--remote-debugging-port=${opt.debug}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions',
  '--no-first-run', `--user-data-dir=${profile}`, '--window-size=1920,1080', 'about:blank'], { stdio: 'ignore' });
const browserGone = new Promise((r) => browser.once('exit', r));
// Edge runs as a process tree; on Windows only taskkill /T ends the children that hold the profile folder (only while Edge runs: a finished PID may be reused).
const killBrowser = () => { if (process.platform === 'win32' && browser.pid && browser.exitCode === null && browser.signalCode === null) spawnSync('taskkill', ['/pid', String(browser.pid), '/T', '/F'], { stdio: 'ignore' }); browser.kill(); };
const cleanup = () => { server.kill(); killBrowser(); };
process.on('exit', cleanup);
// Remove the Edge profile only after Edge has let go of it; an 'exit' handler cannot wait, so this runs before process.exit.
async function removeProfile() {
  cleanup();
  await Promise.race([browserGone, new Promise((r) => setTimeout(r, 5000))]);
  try { rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch { /* still held: left in %TEMP% */ }
}
for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, () => removeProfile().finally(() => process.exit(130)));
// A hung Edge or DevTools connection never holds the check up: after 180 s it ends as a failure.
setTimeout(() => {
  console.log('browser ui: FAIL, watchdog: no result after 180 s');
  removeProfile().finally(() => process.exit(1));
}, 180_000).unref();

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

// The preview server must answer on the port before the page is opened (it is a child that needs a moment to listen; a
// navigation to a port nobody listens on leaves an error page where no module path resolves) and must still be our own child.
async function serverUp() {
  for (let i = 0; i < 80; i++) {
    if (serverGone !== null) throw new Error(`preview server ended with ${serverGone} (port ${opt.port} taken?)`);
    try {
      await new Promise((res, rej) => http.get(`http://127.0.0.1:${opt.port}/`, (r) => { r.resume(); r.statusCode === 200 ? res() : rej(new Error(`status ${r.statusCode}`)); }).on('error', rej));
      return;
    } catch { /* not listening yet */ }
    await sleep(100);
  }
  throw new Error('preview server not reachable');
}
await serverUp();
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

const CODES = { F6: 'F6', Enter: 'Enter', Escape: 'Escape', Backspace: 'Backspace' };
const VK = { F6: 117, Enter: 13, Escape: 27, Backspace: 8, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40 };
async function key(k) {
  const text = k.length === 1 ? k : k === 'Enter' ? '\r' : undefined;
  const base = { key: k, code: CODES[k] ?? (k.length === 1 ? `Key${k.toUpperCase()}` : k), windowsVirtualKeyCode: VK[k] ?? k.toUpperCase().charCodeAt(0) };
  await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', ...base, text });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(160);
}

const host = (name, payload) => evaluate(`window.couchhudMockBrowser(${JSON.stringify(name)}, ${JSON.stringify(payload)})`);
const calls = (name) => evaluate(`(window.__mockCalls ?? []).filter((c) => c[0] === ${JSON.stringify(name)})`);
const clearCalls = () => evaluate('window.__mockCalls = []');
const view = () => evaluate(`import('/views/browser.js').then(({ browserView: v }) => ({ focus: v.focus, open: v.isOpen }))`);
const overlay = () => evaluate(`(() => { const o = document.querySelector('#overlay'); return o && !o.hidden ? o.textContent : null; })()`);
const lastLayout = async () => (await calls('browserLayout')).at(-1)?.[1];

let failed = 0;
function check(ok, what, detail) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}${ok || detail === undefined ? '' : ` -> ${JSON.stringify(detail)}`}`);
  if (!ok) failed++;
}

await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: `http://127.0.0.1:${opt.port}/?lang=en` });
// Ready when the document is the app (not about:blank or an error page), complete, the mock API is installed and the modules the
// checks import resolve; waited for instead of a fixed pause.
let ready = false;
for (let i = 0; i < 120 && !ready; i++) {
  ready = await evaluate(`location.href.startsWith('http://127.0.0.1:${opt.port}/') && document.readyState === 'complete' && typeof window.couchhudMockBrowser === 'function' && !!document.querySelector('#boot')`).catch(() => false);
  if (!ready) await sleep(250);
}
if (!ready) {
  console.log(`FAIL the app page did not load (${await evaluate('location.href').catch(() => '?')})`);
  process.exit(1);
}
const mods = await evaluate(`import('/views/browser.js').then(() => true, (e) => String(e))`).catch((e) => String(e));
if (mods !== true) {
  console.log(`FAIL the app modules did not load: ${mods}`);
  process.exit(1);
}
await evaluate('document.querySelector("#boot")?.classList.add("done")');

try {
  // 0. Menus: an ungrouped menu renders as before (no headings, a detail on every entry, plain list); the grouped power menu in
  // tiles has its headings outside the entry indices and no label wider than its tile.
  const plain = await evaluate(`(async () => {
    const { openOverlay, closeOverlay } = await import('/components/overlay.js');
    openOverlay({ title: 'T', entries: [{ label: 'a', detail: 'da', run: () => {} }, { label: 'b', detail: 'db', run: () => {} }, { label: 'c', detail: 'dc', run: () => {} }], index: 1 });
    const ul = document.querySelector('#overlay .menu');
    const r = { cls: [...ul.classList].sort(), items: ul.children.length, details: ul.querySelectorAll('li small').length, headings: ul.querySelectorAll('.menu-group').length,
      below: !!document.querySelector('#overlay .menu-detail'), indexes: [...ul.children].map((li) => li.dataset.index).join(','), focused: ul.querySelector('li.focused')?.dataset.index };
    closeOverlay();
    return r;
  })()`);
  check(plain.cls.join() === 'menu' && plain.items === 3 && plain.details === 3 && plain.headings === 0 && !plain.below && plain.indexes === '0,1,2' && plain.focused === '1',
    'ungrouped menu renders unchanged', plain);
  const tiles = await evaluate(`(async () => {
    const { state } = await import('/core/state.js');
    state.info.isShell = true;
    const { powerMenu } = await import('/core/power.js');
    const { openOverlay, closeOverlay } = await import('/components/overlay.js');
    openOverlay(powerMenu());
    const ul = document.querySelector('#overlay .menu');
    const fits = [...ul.querySelectorAll('.menu-label')].every((l) => l.scrollWidth <= l.parentElement.clientWidth + 1);
    const r = { tiles: ul.classList.contains('tiles'), headings: [...ul.querySelectorAll('.menu-group')].map((h) => h.dataset.index ?? '-').join(','),
      entries: ul.querySelectorAll('li[data-index]').length, focused: state.overlay.entries[state.overlay.index].id, details: ul.querySelectorAll('li small').length,
      below: document.querySelector('#overlay .menu-detail')?.textContent ?? null, fits };
    closeOverlay();
    state.info.isShell = false;
    return r;
  })()`);
  check(tiles.tiles && tiles.headings === '' && tiles.entries === 5 && tiles.focused === 'standby' && tiles.details === 0 && !!tiles.below && tiles.fits,
    'power menu: three tiles, Mehr and timer rows, no headings, detail under the tiles, labels fit', tiles);

  // 1. Opening a site: the browser tab comes up with the page in front and the controller on it.
  await clearCalls();
  await evaluate(`import('/core/api.js').then(({ api }) => api.openBrowser('example.org'))`);
  await sleep(500);
  let v = await view();
  check(v.open && v.focus === 'page', 'opened site: page has the controller', v);
  check(/^\d+,\d+,\d+,\d+$/.test(await lastLayout() ?? ''), 'page is placed over the placeholder', await lastLayout());
  check((await calls('browserFocus')).at(-1)?.[1] === true, 'host is asked for the page focus', await calls('browserFocus'));

  // 2. The host moves the controller to the bar (View on the page): the HUD follows without asking back.
  await clearCalls();
  await host('browserui', 'bar');
  await sleep(300);
  v = await view();
  check(v.focus === 'bar', 'host "bar": HUD shows the bar', v);
  check((await calls('browserFocus')).length === 0, 'no focus ping-pong after a host report', await calls('browserFocus'));

  // 3. Down in the bar gives the page the controller again.
  await key('ArrowDown');
  await sleep(300);
  v = await view();
  check(v.focus === 'page' && (await calls('browserFocus')).at(-1)?.[1] === true, 'down in the bar: back to the page', v);

  // 4. The host refuses the page (not on screen yet): the HUD must not stay "page" while the host steers the HUD.
  await host('browserui', 'bar');
  await host('refuseFocus', true);
  await sleep(200);
  await key('ArrowDown');
  await sleep(400);
  v = await view();
  check(v.focus === 'bar', 'refused page focus: HUD back on the bar', v);
  await host('refuseFocus', false);
  await key('ArrowDown');
  await sleep(400);
  check((await view()).focus === 'page', 'asking again after a refusal works', await view());

  // 5. The host gives the page the controller by itself (full screen): the HUD follows.
  await host('browserui', 'bar');
  await sleep(200);
  await host('browserui', 'page');
  await sleep(200);
  check((await view()).focus === 'page', 'host "page": HUD follows', await view());

  // 6. A page's confirm(): asked in the HUD with the site's name, the page steps aside, the answer goes back.
  await clearCalls();
  await host('browserdialog', { id: 7, kind: 'confirm', message: 'Leave this page?', host: 'example.org' });
  await sleep(400);
  const text = await overlay();
  check(!!text && text.includes('example.org') && text.includes('Leave this page?'), 'confirm shown in the HUD with the site', text);
  check(await lastLayout() === 'hidden', 'page steps aside while the HUD asks', await lastLayout());
  check(await evaluate(`document.querySelector('#overlay').classList.contains('web')`), 'a page question is marked as coming from the web');
  await key('Enter');
  await sleep(300);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 7, true, null]), 'confirm answered yes', await calls('browserDialogAnswer'));
  check(await overlay() === null, 'dialog gone after the answer', await overlay());

  // 7. An alert the host closes by itself (time-out, page gone): the HUD closes it too.
  await host('browserdialog', { id: 8, kind: 'alert', message: 'Hi', host: 'example.org', embedded: true });
  await sleep(300);
  check((await overlay())?.includes('example.org (embedded)'), 'alert of an embedded frame names the page, marked as embedded', await overlay());
  await host('browserdialog', { id: 8, closed: true });
  await sleep(300);
  check(await overlay() === null, 'alert closed by the host', await overlay());

  // 8. A download: the question defaults to "no".
  await clearCalls();
  await host('browserdialog', { id: 9, kind: 'download', message: 'holiday-photos.zip', extension: 'zip', host: 'example.org', bytes: 1048576 });
  await sleep(400);
  const dl = await overlay();
  check(!!dl && dl.includes('holiday-photos.zip') && dl.includes('1 MB') && dl.includes('File type: ZIP'), 'download asked with name, size and type', dl);
  await key('Enter');
  await sleep(300);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 9, false, null]), 'download: Enter on the default is "no"', await calls('browserDialogAnswer'));

  // 9. A prompt(): the on-screen keyboard with the page's default text.
  await clearCalls();
  await host('browserdialog', { id: 10, kind: 'prompt', message: 'Name?', defaultText: 'ab', host: 'example.org' });
  await sleep(500);
  check(await evaluate(`!!document.querySelector('.osk.web')`), 'a page prompt has the web frame on the keyboard');
  await key('c');
  await key('Enter');
  await sleep(400);
  check(JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 10, true, 'abc']), 'prompt answered with the typed text', await calls('browserDialogAnswer'));

  // 10. X on the page: the keyboard types into the page; the controller is back on the page afterwards.
  await host('browserui', 'page');
  await clearCalls();
  await host('browserui', { type: 'typing', secret: false });
  await sleep(500);
  for (const ch of 'hello') await key(ch);
  await key('Enter');
  await sleep(700);
  check(JSON.stringify((await calls('browserType')).at(-1)) === JSON.stringify(['browserType', 'hello', false]), 'typed text goes into the page', await calls('browserType'));
  check((await view()).focus === 'page', 'after typing the page has the controller again', await view());

  // 11. Typing cancelled with B: still back on the page, nothing typed.
  await clearCalls();
  await host('browserui', { type: 'typing', secret: true });
  await sleep(500);
  await key('Escape');
  await sleep(500);
  check((await calls('browserType')).length === 0, 'cancelled typing sends nothing');
  check((await view()).focus === 'page', 'after cancelled typing the page has the controller again', await view());

  // 12. Unknown or malformed host messages change nothing.
  await host('browserdialog', { id: 11, kind: 'file' });
  await host('browserdialog', null);
  await host('browserui', { type: 'other' });
  await sleep(300);
  check(await overlay() === null && (await view()).focus === 'page', 'unknown messages are ignored', await view());

  // 12b. A mouse click beside the page: the keyboard is in the HUD now, the bar shows it; Enter asks for the page again; F6 toggles.
  await host('browserui', 'page');
  await sleep(200);
  await clearCalls();
  await evaluate(`document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))`);
  await sleep(300);
  check((await view()).focus === 'bar' && (await calls('browserFocus')).at(-1)?.[1] === false, 'click beside the page: visible focus on the bar', await view());
  await host('browserui', 'page');
  await sleep(200);
  await clearCalls();
  await key('Enter');
  await sleep(300);
  check((await calls('browserFocus')).at(-1)?.[1] === true && (await view()).focus === 'page', 'Enter while the HUD has the keys: the page is asked for again', await calls('browserFocus'));
  await clearCalls();
  await key('F6');
  await sleep(300);
  check((await view()).focus === 'bar', 'F6 in the HUD goes to the bar', await view());
  await key('F6');
  await sleep(300);
  check((await view()).focus === 'page', 'F6 in the bar goes back to the page', await view());

  // 12c. A page in full screen: the HUD's strip above it says it is a website.
  await host('browser', { open: true, url: 'https://example.org/', title: 'Example', canGoBack: true, fullScreen: true });
  await sleep(300);
  const badge = await evaluate(`window.__couchhudBadge?.root.querySelector('.strip')?.textContent ?? null`);
  check(!!badge && badge.includes('example.org'), 'full screen: the website badge is shown', badge);
  await host('browser', { open: true, url: 'https://other.example/', title: 'Other', canGoBack: true, fullScreen: true });
  await sleep(300);
  const moved = await evaluate(`window.__couchhudBadge?.root.querySelector('.strip')?.textContent ?? null`);
  check(!!moved && moved.includes('other.example') && !moved.includes('example.org'), 'full screen: the badge follows a redirect to another site', moved);
  await host('browser', { open: true, url: 'https://example.org/', title: 'Example', canGoBack: true, fullScreen: false });
  await sleep(300);
  check(await evaluate(`!document.querySelector('#web-fullscreen-badge')`), 'out of full screen: the badge is gone');

  // 12d. The user went home (guide): a question of the page is not shown over the HUD, it is answered "no".
  await evaluate(`import('/core/nav.js').then(({ switchTab }) => switchTab(0))`);
  await sleep(400);
  await clearCalls();
  await host('browserdialog', { id: 21, kind: 'confirm', message: 'Enter your code', host: 'example.org' });
  await sleep(500);
  check(await overlay() === null && JSON.stringify((await calls('browserDialogAnswer')).at(-1)) === JSON.stringify(['browserDialogAnswer', 21, false, null]),
    'a page question after leaving is refused unseen', await calls('browserDialogAnswer'));
  // A question the host ended while it waited for the screen is never shown.
  await host('browserui', 'show');
  await sleep(400);
  await clearCalls();
  await evaluate(`import('/components/overlay.js').then(({ openOverlay }) => { openOverlay({ title: 'busy', tag: 'busy', entries: [{ label: 'x', run: () => {} }] }); })`);
  await sleep(200);
  await host('browserdialog', { id: 22, kind: 'alert', message: 'late', host: 'example.org' });
  await host('browserdialog', { id: 22, closed: true });
  await evaluate(`import('/components/overlay.js').then(({ closeTagged }) => closeTagged('busy'))`);
  await sleep(600);
  check(await overlay() === null && (await calls('browserDialogAnswer')).length === 0, 'a question ended while waiting is not shown', await overlay());
  await host('browserui', 'page');
  await sleep(200);

  // 13. Closing the browser: the HUD leaves the page.
  await evaluate(`import('/core/api.js').then(({ api }) => api.browserCommand('close'))`);
  await sleep(400);
  v = await view();
  check(!v.open && v.focus === 'bar' && await lastLayout() === 'hidden', 'closed browser: no page, no page focus', v);

  // 14. The guard script of every page (from BrowserGuard.cs) in a blank document: no picture-in-picture of either kind.
  const source = readFileSync(join(here, '..', 'src', 'CouchHud.Core', 'Guard', 'BrowserGuard.cs'), 'utf8');
  const guard = /public const string Script = "{3}\r?\n([\s\S]*?)\r?\n\s*"{3};/.exec(source)?.[1];
  check(!!guard, 'guard script found in BrowserGuard.cs');
  await send('Page.navigate', { url: 'about:blank' });
  await sleep(500);
  await evaluate(guard);
  const pip = await evaluate(`(async () => {
    const v = document.createElement('video');
    let refused = false;
    try { await v.requestPictureInPicture(); } catch (e) { refused = e.name === 'NotAllowedError'; }
    v.disablePictureInPicture = false;
    return { refused, disabled: v.disablePictureInPicture === true, docPip: window.documentPictureInPicture === undefined, enabled: document.pictureInPictureEnabled };
  })()`);
  check(pip?.refused && pip.disabled && pip.docPip && pip.enabled === false, 'guard: picture-in-picture and Document PiP refused', pip);

  // A second run in the same window (the navigation script and a frame's ContentLoading run) wraps nothing twice.
  await evaluate(`window.__click = HTMLInputElement.prototype.click; 1`);
  await evaluate(guard);
  check(await evaluate(`window.__click === HTMLInputElement.prototype.click`) === true, 'guard: a second run in the same window changes nothing');

  // 15. File and color fields in shadow roots (also closed ones), and print and the pickers in a frame a script makes (about:blank, srcdoc).
  const dialogs = await evaluate(`(async () => {
    const swallowed = (root, type) => {
      const input = document.createElement('input');
      input.type = type;
      root.append(input);
      const event = new MouseEvent('click', { bubbles: true, composed: true, cancelable: true });
      input.dispatchEvent(event);
      return event.defaultPrevented;
    };
    const closed = document.body.appendChild(document.createElement('div')).attachShadow({ mode: 'closed' });
    const open = document.body.appendChild(document.createElement('div')).attachShadow({ mode: 'open' });
    const label = open.appendChild(document.createElement('label'));
    const frame = document.body.appendChild(document.createElement('iframe'));
    const fw = frame.contentWindow;
    const inFrame = fw.document.createElement('input');
    inFrame.type = 'file';
    fw.document.body.append(inFrame);
    const frameEvent = new fw.MouseEvent('click', { bubbles: true, cancelable: true });
    inFrame.dispatchEvent(frameEvent);
    const doc = document.body.appendChild(document.createElement('iframe'));
    doc.srcdoc = '<p>x</p>';
    await new Promise((r) => setTimeout(r, 300));
    const own = (w) => /=>\\s*\\{\\s*\\}/.test(String(w.print));
    return {
      closedFile: swallowed(closed, 'file'), closedColor: swallowed(closed, 'color'), openFile: swallowed(open, 'file'),
      frameFile: frameEvent.defaultPrevented, framePrint: own(fw), srcdocPrint: own(doc.contentWindow), top: own(window),
      picker: String(fw.showOpenFilePicker).includes('Promise.reject'), label: !!label
    };
  })()`);
  check(dialogs?.closedFile && dialogs.closedColor && dialogs.openFile && dialogs.frameFile && dialogs.framePrint && dialogs.srcdocPrint && dialogs.top && dialogs.picker,
    'guard: file and color fields in shadow roots and script-made frames, print and pickers in frames', dialogs);
  // 16. The TV page (ui/views/tv.js) on the mock host (ui/mock-tv.js; the mock reads ?notv, ?tvnoplugin from the address): no provider,
  // one provider with a found device, the controller through the rows, and the German and English texts.
  const openApp = async (query) => {
    await send('Page.navigate', { url: `http://127.0.0.1:${opt.port}/?${query}` });
    let up = false;
    for (let i = 0; i < 120 && !up; i++) {
      up = await evaluate(`location.href.startsWith('http://127.0.0.1:${opt.port}/') && document.readyState === 'complete' && typeof window.couchhudMockBrowser === 'function' && !!document.querySelector('#boot')`).catch(() => false);
      if (!up) await sleep(250);
    }
    if (!up) throw new Error(`the app page did not load for ?${query}`);
    await evaluate('document.querySelector("#boot")?.classList.add("done")');
  };
  const openTv = async () => {
    await evaluate(`import('/views/tv.js').then(async (m) => { await m.refreshTv(false); (await import('/core/nav.js')).push(m.tvPage()); })`);
    await sleep(500);
  };
  const tvRows = () => evaluate(`(() => { const list = document.querySelector('.check-list'); return list ? { labels: [...list.children].map((li) => li.querySelector('strong')?.textContent), details: [...list.children].map((li) => li.querySelector('small')?.textContent ?? ''), focus: [...list.children].findIndex((li) => li.classList.contains('focused')), head: document.querySelector('.files-head h2')?.textContent, meta: [...document.querySelectorAll('.files-head .meta')].map((m) => m.textContent).join(' | ') } : null; })()`);

  // 16a. No TV plugin and no pairing: the page offers the search and the way to the catalog, in English and in German.
  await openApp('lang=en&notv&tvnoplugin');
  await openTv();
  let tv = await tvRows();
  check(tv?.head === 'TV' && tv.labels.join() === 'Find TV,Get a TV plugin' && /No TV paired/.test(tv.meta) && tv.focus === 0, 'TV page without provider (en): search and plugin rows', tv);
  await openApp('lang=de&notv&tvnoplugin');
  await openTv();
  tv = await tvRows();
  check(tv?.head === 'Fernseher' && tv.labels.join() === 'Fernseher suchen,TV-Plugin holen' && /Kein Fernseher gekoppelt/.test(tv.meta), 'TV page without provider (de): same rows in German', tv);

  // 16b. One provider (LG webOS) and a device in the network: A on "Find TV" lists the devices found.
  await openApp('lang=en&notv&fresh&quiet');
  await openTv();
  tv = await tvRows();
  check(tv?.labels.join() === 'Find TV' && tv.focus === 0, 'TV page with one provider, not paired: only the search row', tv);
  await key('Enter');
  await sleep(2200);
  const found = await overlay();
  check(!!found && found.includes('[LG] webOS TV OLED55C3') && found.includes('192.168.1.20') && found.includes('LG webOS'), 'device search lists the found TV with address and kind', found);
  await key('Escape');
  await sleep(300);

  // 16c. Paired and on: the rows, the controller through them, and the host is asked.
  await openApp('lang=en&fresh&quiet');
  await openTv();
  tv = await tvRows();
  check(tv?.head === 'OLED55C3' && tv.labels.join() === 'Turn off,Volume,Choose input,Refresh,Remove TV' && tv.details[1] === '18 %' && /On · 18 %/.test(tv.meta), 'paired TV page: head, rows, volume and state', tv);
  await key('ArrowDown');
  tv = await tvRows();
  check(tv?.focus === 1, 'controller down moves the focus to Volume', tv);
  await key('ArrowRight');
  await sleep(300);
  tv = await tvRows();
  check(tv?.details[1] === '19 %', 'controller right steps the volume on the host (the mock host answers 19)', tv);
  await key('ArrowLeft');
  await sleep(300);
  tv = await tvRows();
  check(tv?.details[1] === '18 %', 'controller left steps it back', tv);
  await key('ArrowUp');
  await key('ArrowUp');
  tv = await tvRows();
  check(tv?.focus === 0, 'controller up stops at the first row', tv);
  await key('ArrowDown');
  await key('ArrowDown');
  await key('Enter');
  await sleep(500);
  const inputs = await overlay();
  check(!!inputs && inputs.includes('PlayStation 5') && inputs.includes('HDMI 3'), 'A on Choose input opens the input list from the TV', inputs);
  await key('Escape');
  await sleep(300);
  check(await evaluate(`import('/core/nav.js').then((n) => n.currentView().id)`) === 'tv', 'B closes the list and stays on the TV page');
  await key('Escape');
  await sleep(300);
  check(await evaluate(`import('/core/nav.js').then((n) => n.currentView().id !== 'tv')`), 'B on the TV page goes back');

  // 16d. The same page in German, and a TV that is off.
  await openApp('lang=de&tvoff&fresh&quiet');
  await openTv();
  tv = await tvRows();
  check(tv?.labels.join() === 'Einschalten,Aktualisieren,Fernseher entfernen', 'TV page (de), TV off: German rows', tv);
  await openApp('lang=de');
  await openTv();
  tv = await tvRows();
  check(tv?.labels.join() === 'Ausschalten,Lautstärke,Eingang wählen,Aktualisieren,Fernseher entfernen' && tv.details[1] === '18 %', 'TV page (de), TV on: German rows', tv);
} catch (e) {
  console.log('FAIL', e.message);
  failed++;
} finally {
  console.log(failed ? `browser ui: ${failed} failed` : 'browser ui: ok');
  await removeProfile();
  process.exit(failed ? 1 : 0);
}
