// Renders the branding from its SVG sources with headless Edge (no extra packages):
//   docs/logo.png (512), docs/banner.png, docs/social-preview.png (needs the docs/screenshot-*.png), assets/branding/png/icon-*.png
//   and src/CouchHud.Shell/couchhud.ico (PNG frames; 16-32 px from the simplified icon-small.svg).
//
//   node scripts/brand-render.mjs            Env: EDGE (path to msedge.exe)
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import http from 'node:http';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const at = (...p) => join(root, ...p);
const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const port = 9341;

const ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const SMALL = 32; // up to this size the simplified icon stays sharp
const jobs = [
  { src: 'docs/logo.svg', out: 'docs/logo.png', w: 512, h: 512 },
  { src: 'docs/banner.svg', out: 'docs/banner.png', w: 1520, h: 400 },
  // Opened directly (not as <img>) so it may load the screenshots it shows.
  { src: 'docs/social-preview.svg', out: 'docs/social-preview.png', w: 1280, h: 640, opaque: true, direct: true },
  { src: 'docs/logo.svg', out: 'assets/branding/png/icon-512.png', w: 512, h: 512 },
  ...ICON_SIZES.map((s) => ({ src: s <= SMALL ? 'assets/branding/icon-small.svg' : 'docs/logo.svg', out: `assets/branding/png/icon-${s}.png`, w: s, h: s }))
];

const profile = mkdtempSync(join(tmpdir(), 'couchhud-brand-'));
const browser = spawn(edge, [`--remote-debugging-port=${port}`, '--headless=new', '--mute-audio', '--disable-gpu', '--disable-extensions', '--force-color-profile=srgb',
  `--user-data-dir=${profile}`, '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });
process.on('exit', () => browser.kill());

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getJson = (url) => new Promise((res, rej) => http.get(url, (r) => { let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => { try { res(JSON.parse(d)); } catch (e) { rej(e); } }); }).on('error', rej));
let wsUrl;
for (let i = 0; i < 60 && !wsUrl; i++) {
  try { wsUrl = (await getJson(`http://127.0.0.1:${port}/json`)).find((t) => t.type === 'page')?.webSocketDebuggerUrl; } catch {}
  if (!wsUrl) await sleep(250);
}
if (!wsUrl) throw new Error('Edge not reachable');
const ws = new WebSocket(wsUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0;
const waiting = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } };
const send = (method, params = {}) => new Promise((res) => { waiting.set(++id, res); ws.send(JSON.stringify({ id, method, params })); });

await send('Page.enable');
const tmp = mkdtempSync(join(tmpdir(), 'couchhud-brand-page-'));
for (const job of jobs) {
  // The SVG is drawn as an <img> at the target size, so the browser scales it (crisp, no CSS from outside).
  const page = job.direct ? at(job.src) : join(tmp, 'page.html');
  if (!job.direct) writeFileSync(page, `<!doctype html><html><body style="margin:0;background:transparent"><img src="${pathToFileURL(at(job.src)).href}" style="display:block;width:${job.w}px;height:${job.h}px"></body></html>`);
  await send('Emulation.setDeviceMetricsOverride', { width: job.w, height: job.h, deviceScaleFactor: 1, mobile: false });
  await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: job.opaque ? 1 : 0 } });
  await send('Page.navigate', { url: pathToFileURL(page).href });
  await sleep(700);
  const r = await send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: job.w, height: job.h, scale: 1 } });
  mkdirSync(dirname(at(job.out)), { recursive: true });
  writeFileSync(at(job.out), Buffer.from(r.result.data, 'base64'));
  console.log('png', job.out);
}

// ICO with PNG frames (Windows Vista and later), smallest first.
const frames = ICON_SIZES.map((s) => ({ s, data: readFileSync(at(`assets/branding/png/icon-${s}.png`)) }));
const header = Buffer.alloc(6 + frames.length * 16);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
let offset = header.length;
frames.forEach((f, i) => {
  const o = 6 + i * 16;
  header[o] = f.s >= 256 ? 0 : f.s; header[o + 1] = f.s >= 256 ? 0 : f.s;
  header.writeUInt16LE(1, o + 4); header.writeUInt16LE(32, o + 6);
  header.writeUInt32LE(f.data.length, o + 8); header.writeUInt32LE(offset, o + 12);
  offset += f.data.length;
});
writeFileSync(at('src/CouchHud.Shell/couchhud.ico'), Buffer.concat([header, ...frames.map((f) => f.data)]));
console.log('ico', 'src/CouchHud.Shell/couchhud.ico', ICON_SIZES.join(', '));
browser.kill();
process.exit(0);
