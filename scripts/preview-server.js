'use strict';

// Serves ui/ with the mock API, for UI work without Windows.
//
// CATALOG_SKINS=<folder>: also serves the skins in that folder (one sub folder per skin, with skin.json and skin.css) the way the host
// serves installed skins: as "user:<folder>" skins, the stylesheet cleaned of external addresses like SkinCss.Sanitize does.
// It is how a skin from the catalog is looked at before it is published (scripts/ui-shot.mjs, step skin:user:<folder>).
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'ui');
const port = Number(process.env.PORT) || 5173;
const skinRoot = process.env.CATALOG_SKINS ? path.resolve(process.env.CATALOG_SKINS) : null;
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json' };

// Text types are sent with an explicit charset, like the host does (a stylesheet's @charset must not be able to change how it is read).
const contentType = (ext) => { const t = types[ext] || 'application/octet-stream'; return /^(text\/|application\/json|image\/svg)/.test(t) || t === 'text/javascript' ? `${t}; charset=utf-8` : t; };

// The same as SkinCss.Sanitize in Core (scripts/skin-css.js): no @import, no @layer, no web addresses; a stylesheet that still mentions @layer or @import is refused.
const { sanitizeBytes } = require('./skin-css.js');

function serveSkins(urlPath, res) {
  const rest = urlPath.slice('/__catalog-skins/'.length);
  if (!skinRoot) return res.writeHead(404).end('not found');
  if (rest === 'index.json') {
    const skins = fs.readdirSync(skinRoot, { withFileTypes: true })
      .filter((d) => d.isDirectory() && fs.existsSync(path.join(skinRoot, d.name, 'skin.json')) && fs.existsSync(path.join(skinRoot, d.name, 'skin.css')))
      .map((d) => ({ folder: d.name, manifest: JSON.parse(fs.readFileSync(path.join(skinRoot, d.name, 'skin.json'), 'utf8')) }));
    return res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(skins));
  }
  const file = path.join(skinRoot, rest);
  if (!file.startsWith(skinRoot + path.sep) || !fs.existsSync(file)) return res.writeHead(404).end('not found');
  const data = fs.readFileSync(file);
  if (path.basename(file) === 'skin.css') {
    const text = data.toString('utf8');
    const { css: clean, refused } = sanitizeBytes(data);
    if (refused) console.log(`skin stylesheet refused (@layer, @import, a charset other than utf-8, a control character or bytes that are not UTF-8): ${rest}`);
    else if (clean !== text) console.log(`sanitizer changed ${rest}`);
    return res.writeHead(200, { 'Content-Type': 'text/css; charset=utf-8' }).end(clean);
  }
  // Only for the sanitizer check (scripts/tabs-check.mjs): a file named *.nocharset.css is sent the way the host used to send stylesheets, without a charset, to show
  // that an @charset in it is honoured then and is not with the header.
  if (file.endsWith('.nocharset.css')) return res.writeHead(200, { 'Content-Type': 'text/css' }).end(data);
  return res.writeHead(200, { 'Content-Type': contentType(path.extname(file)) }).end(data);
}

// The phone page (/remote/index.html) talks to /api/*: a pretend PC that is paired and plays a game. Nothing here does anything.
const PHONE_GAME = process.env.PHONE_GAME ?? 'Hollow Knight';
function servePhoneApi(req, urlPath, res) {
  const json = (status, body) => res.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(body));
  if (req.method === 'GET' && urlPath === '/api/state') return json(200, { keyboard: { open: false, title: '', secret: false, text: '' }, power: process.env.PHONE_POWER !== '0' });
  if (req.method !== 'POST') return json(405, {});
  if (urlPath === '/api/power/prepare') return json(200, { ticket: '0'.repeat(32), game: PHONE_GAME || null });
  if (['/api/power', '/api/button', '/api/text'].includes(urlPath)) return json(200, { ok: true });
  return json(404, {});
}

// The same pretend PC over the encrypted channel of 0.7 (ui/remote/seal.js, the page's own code): the QR secret of the screenshot
// (32 zero bytes) pairs, the phone gets the level PHONE_LEVEL (control, library, full; default full), a small library, two collections
// and two downloads.
// Screenshot: node scripts/ui-shot.mjs --mobile 1 --path "remote/index.html#k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
const sealModule = import('../ui/remote/seal.js');
const phoneDevices = new Map();
const phoneSessions = new Map();
const DAY = 86_400_000;
const phoneLibrary = [
  { id: 'g1', name: 'Hollow Knight', type: 'game', source: 'Steam', favorite: true, hidden: false, lastPlayed: Date.now() - DAY, minutes: 3120, cover: null },
  { id: 'g3', name: 'Forza Horizon 5', type: 'game', source: 'Xbox', favorite: true, hidden: false, lastPlayed: Date.now() - 2 * DAY, minutes: 2400, cover: null },
  { id: 'g2', name: 'Celeste', type: 'game', source: 'Steam', favorite: false, hidden: false, lastPlayed: Date.now() - 6 * DAY, minutes: 840, cover: null },
  { id: 'g4', name: 'Rocket League', type: 'game', source: 'Epic', favorite: false, hidden: false, lastPlayed: 0, minutes: 0, cover: null },
  { id: 'g5', name: '<img src=x onerror=alert(1)>', type: 'game', source: 'GOG', favorite: false, hidden: false, lastPlayed: 0, minutes: 0, cover: null },
  { id: 'a1', name: 'Netflix', type: 'app', source: 'App', favorite: false, hidden: false, lastPlayed: Date.now() - 3 * DAY, minutes: 0, cover: null },
  { id: 'g6', name: 'Old Demo', type: 'game', source: 'Steam', favorite: false, hidden: true, lastPlayed: 0, minutes: 0, cover: null }
];
const phoneCollections = [{ id: 'c1', name: 'Couch-Koop', items: ['g2', 'g4'], onHome: true }, { id: 'c2', name: 'Rennspiele', items: ['g3'], onHome: false }];
let phoneTimer = null;
// The settings page of a Full phone: the host's positive list (RemoteSettings.cs) with a few values, a Couch skin and one installed one.
const phoneSettings = { language: 'auto', favoritesMode: 'combined', favoritesRecentDays: 30, favoritesSites: true, screensaverMinutes: 5, screensaverStyle: 'art', audioMode: 'ui', soundVolume: 70, musicVolume: 35 };
const phoneSchema = [
  { key: 'language', kind: 'choice', options: ['auto', 'de', 'en'] },
  { key: 'favoritesMode', kind: 'choice', options: ['favorites', 'recent', 'combined'] },
  { key: 'favoritesRecentDays', kind: 'choice', options: [7, 30, 90, 365, 0] },
  { key: 'favoritesSites', kind: 'bool' },
  { key: 'screensaverMinutes', kind: 'choice', options: [0, 2, 5, 10, 15, 30] },
  { key: 'screensaverStyle', kind: 'choice', options: ['art', 'clock', 'dim', 'black'] },
  { key: 'audioMode', kind: 'choice', options: ['off', 'ui', 'music'] },
  { key: 'soundVolume', kind: 'range', min: 0, max: 100 },
  { key: 'musicVolume', kind: 'range', min: 0, max: 100 },
];
let phoneSkin = 'default';

function readBody(req) {
  return new Promise((resolve) => {
    let text = '';
    req.setEncoding('utf8');
    req.on('data', (c) => { text += c; });
    req.on('end', () => resolve(text));
  });
}

const refuse = (message, code) => Object.assign(new Error(message), { code });

function phoneCall(op, args, level) {
  const full = level === 'full';
  switch (op) {
    case 'state':
      return {
        level, name: 'Pixel 9', keyboard: { open: false, title: '', secret: false, text: '' }, power: process.env.PHONE_POWER !== '0', game: PHONE_GAME || null,
        sleepTimer: phoneTimer, parentOnPhone: true, locked: false, artwork: false, minutes: [30, 60, 90, 120]
      };
    case 'button': case 'text': case 'power': return {};
    case 'powerPrepare': return { ticket: '0'.repeat(32), game: PHONE_GAME || null, gameRunning: !!PHONE_GAME };
    case 'sleepTimer':
      phoneTimer = args.minutes ? { at: Date.now() + args.minutes * 60_000, action: args.action } : null;
      return {};
    case 'library': return phoneLibrary.filter((i) => full || !i.hidden);
    case 'collections': return phoneCollections;
    case 'downloads':
      return {
        entries: [
          { id: 'd1', kind: 'game', source: 'Steam', name: 'Baldurs Gate 3', state: 'downloading', progress: 0.42, bytesDone: 52e9, bytesTotal: 124e9, isUpdate: false },
          { id: 'd2', kind: 'game', source: 'Epic', name: 'Alan Wake 2', state: 'queued', progress: null, bytesDone: 0, bytesTotal: 0, isUpdate: true }
        ],
        summary: { active: 2 }
      };
    case 'launch': {
      const item = phoneLibrary.find((i) => i.id === args.id);
      if (!args.confirm && PHONE_GAME) throw refuse(`${PHONE_GAME} is still running`, 'running');
      return { name: item?.name };
    }
    case 'favorite': {
      const item = phoneLibrary.find((i) => i.id === args.id);
      if (item) item.favorite = !!args.favorite;
      return { favorite: !!args.favorite };
    }
    case 'settings':
      if (!full) throw refuse('This phone may not do that', 'level');
      return { values: phoneSettings, skin: phoneSkin, skins: [{ id: 'default', name: 'Couch' }, { id: 'user:neoncity', name: 'Neon City' }], schema: phoneSchema };
    case 'setting': {
      if (!full) throw refuse('This phone may not do that', 'level');
      if (!(args.key in phoneSettings)) throw refuse('Bad request', 'bad');
      phoneSettings[args.key] = args.value;
      return null;
    }
    case 'skin':
      if (!full) throw refuse('This phone may not do that', 'level');
      phoneSkin = args.id;
      return null;
    case 'reloadArtwork':
      if (!full) throw refuse('This phone may not do that', 'level');
      return 'nothing';
    default:
      if (!full) throw refuse('This phone may not do that', 'level');
      return phoneCollections;
  }
}

async function servePhoneChannel(req, urlPath, res) {
  const S = await sealModule;
  const text = (status, body) => res.writeHead(status, { 'Content-Type': 'text/plain' }).end(body);
  const level = ['control', 'library', 'full'].includes(process.env.PHONE_LEVEL) ? process.env.PHONE_LEVEL : 'full';
  if (urlPath === '/api/events') {
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' });
    res.write(': ok\n\n');
    const ping = setInterval(() => res.write(': ping\n\n'), 20000);
    req.on('close', () => clearInterval(ping));
    return;
  }
  if (urlPath.startsWith('/art/')) return text(404, '');
  const body = await readBody(req);
  if (urlPath === '/api/pair') {
    const pk = S.pairKey(new Uint8Array(32));
    const opened = S.open(pk, S.DIR.request, 'pair', body);
    if (!opened) return text(400, '');
    const id = S.hex(S.randomBytes(8));
    const key = S.randomBytes(32);
    phoneDevices.set(id, key);
    return text(200, S.seal(pk, S.DIR.response, 0, 'pair', JSON.stringify({ id, key: S.b64url(key), name: JSON.parse(opened.text).name })));
  }
  if (urlPath === '/api/session') {
    const id = req.headers['x-megahub-device'];
    const key = phoneDevices.get(id);
    const opened = key && S.open(key, S.DIR.request, `session|${id}`, body);
    if (!opened) return text(401, '');
    const c = JSON.parse(opened.text).c;
    const n = S.randomBytes(16);
    const sid = S.hex(S.randomBytes(16));
    phoneSessions.set(sid, S.sessionKey(key, S.fromB64url(c), n));
    return text(200, S.seal(key, S.DIR.response, 0, `session|${id}`, JSON.stringify({ sid, n: S.b64url(n), c, level, name: 'Pixel 9' })));
  }
  if (urlPath === '/api/rpc') {
    const sid = req.headers['x-megahub-session'];
    const key = phoneSessions.get(sid);
    const opened = key && S.open(key, S.DIR.request, `rpc|${sid}`, body);
    if (!opened) return text(401, '');
    const { op, args } = JSON.parse(opened.text);
    let answer;
    try {
      answer = { ok: true, result: phoneCall(op, args ?? {}, level) };
    } catch (e) {
      answer = { ok: false, error: e.message, code: e.code };
    }
    return text(200, S.seal(key, S.DIR.response, opened.seq, `rpc|${sid}`, JSON.stringify(answer)));
  }
  return text(404, '');
}

http.createServer((req, res) => {
  const urlPath = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (urlPath.startsWith('/__catalog-skins/')) return serveSkins(urlPath, res);
  if (['/api/pair', '/api/session', '/api/rpc', '/api/events'].includes(urlPath) || urlPath.startsWith('/art/')) return void servePhoneChannel(req, urlPath, res);
  if (urlPath.startsWith('/api/')) return servePhoneApi(req, urlPath, res);
  const file = path.join(root, urlPath === '/' ? 'index.html' : urlPath);
  if (!file.startsWith(root)) {
    res.writeHead(403).end();
    return;
  }
  fs.readFile(file, (err, data) => {
    if (err) {
      res.writeHead(404).end('not found');
      return;
    }
    res.writeHead(200, { 'Content-Type': contentType(path.extname(file)) }).end(data);
  });
}).listen(port, () => console.log(`MegaHub preview: http://localhost:${port}`));
