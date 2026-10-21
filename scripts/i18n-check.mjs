// Checks the UI texts: node scripts/i18n-check.mjs [--verbose]
//  - de and en have the same keys (per area file and overall), the same {placeholders} and the same plural shape
//  - every t('key') / data-i18n="key" in ui/ exists; every key in the dictionaries is used somewhere (no orphans)
//  - t('key', { ... }) passes every placeholder the text has
//  - no German text left in code (umlauts in string literals outside ui/i18n; mark a line with "i18n-ignore" to allow it)
// Dynamic keys: list them in a comment, e.g.  // i18n-used: settings.mode.light settings.mode.full
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'ui');
const verbose = process.argv.includes('--verbose');
const errors = [];
const fail = (msg) => errors.push(msg);

const { dictionaries, FILES } = await import(pathToFileURL(join(root, 'i18n', 'index.js')).href);
const { de, en } = dictionaries;

// --- 1. de / en parity -------------------------------------------------------------------------------------
const placeholders = (v) => {
  const texts = typeof v === 'object' ? Object.values(v) : [v];
  return [...new Set(texts.flatMap((s) => [...String(s).matchAll(/\{(\w+)\}/g)].map((m) => m[1])))].sort();
};
const shape = (v) => (typeof v === 'object' ? Object.keys(v).sort().join('/') : 'text');

for (const file of FILES) {
  const a = (await import(pathToFileURL(join(root, 'i18n', 'de', `${file}.js`)).href)).default;
  const b = (await import(pathToFileURL(join(root, 'i18n', 'en', `${file}.js`)).href)).default;
  for (const k of Object.keys(a)) if (!(k in b)) fail(`en/${file}.js: missing key ${k}`);
  for (const k of Object.keys(b)) if (!(k in a)) fail(`de/${file}.js: missing key ${k}`);
}
for (const k of Object.keys(de)) {
  if (!(k in en)) continue;
  if (shape(de[k]) !== shape(en[k])) fail(`${k}: plural shape differs (de ${shape(de[k])}, en ${shape(en[k])})`);
  if (typeof de[k] === 'object' && !(de[k].one !== undefined && de[k].other !== undefined)) fail(`${k}: plural needs one and other`);
  if (placeholders(de[k]).join() !== placeholders(en[k]).join()) fail(`${k}: placeholders differ (de {${placeholders(de[k])}}, en {${placeholders(en[k])}})`);
  for (const lang of [de, en]) {
    const texts = typeof lang[k] === 'object' ? Object.values(lang[k]) : [lang[k]];
    if (texts.some((s) => typeof s !== 'string' || s === '')) fail(`${k}: empty or non-string text`);
  }
}
const seen = new Map();
for (const file of FILES) {
  const a = (await import(pathToFileURL(join(root, 'i18n', 'de', `${file}.js`)).href)).default;
  for (const k of Object.keys(a)) {
    if (seen.has(k)) fail(`${k}: defined in ${seen.get(k)} and ${file}`);
    seen.set(k, file);
  }
}

// --- 2. usage in the code -----------------------------------------------------------------------------------
function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      if (name === 'assets' || name === 'i18n') continue;
      walk(full, out);
    } else if (/\.(js|html)$/.test(name)) out.push(full);
  }
  return out;
}

/** The top-level keys of the object literal that starts at text[start] === '{'. */
function objectKeys(text, start) {
  const keys = [];
  let depth = 0;
  let i = start;
  let token = '';
  let expectKey = true;
  for (; i < text.length; i++) {
    const c = text[i];
    if (c === "'" || c === '"' || c === '`') {
      const q = c;
      for (i++; i < text.length && text[i] !== q; i++) if (text[i] === '\\') i++;
      continue;
    }
    if (c === '{' || c === '(' || c === '[') {
      depth++;
      if (depth > 1) expectKey = false;
      continue;
    }
    if (c === '}' || c === ')' || c === ']') {
      depth--;
      if (depth === 0) {
        if (token.trim()) keys.push(token.trim().match(/^\w+/)?.[0]);
        break;
      }
      continue;
    }
    if (depth === 1) {
      if (c === ',') {
        if (expectKey && token.trim()) keys.push(token.trim().match(/^\w+/)?.[0]);
        token = '';
        expectKey = true;
      } else if (c === ':' && expectKey) {
        keys.push(token.trim().replace(/^['"]|['"]$/g, ''));
        token = '';
        expectKey = false;
      } else if (expectKey) token += c;
    }
  }
  return keys.filter(Boolean);
}

const used = new Set();
const umlaut = /[äöüßÄÖÜ]/;
for (const file of walk(root)) {
  const rel = relative(root, file).split(sep).join('/');
  const src = readFileSync(file, 'utf8');
  const isHtml = file.endsWith('.html');
  // Strip comments for the code scans (keep the i18n-used markers).
  for (const m of src.matchAll(/i18n-used:\s*([^\n*]+)/g)) for (const k of m[1].trim().split(/\s+/)) used.add(k);
  const code = isHtml ? src : src.replace(/\/\*[\s\S]*?\*\//g, (c) => c.replace(/[^\n]/g, ' ')).replace(/(^|[^:'"`\\])\/\/[^\n]*/g, '$1');

  for (const m of code.matchAll(/(?<![\w.$])t\(\s*(['"`])([^'"`$]*?)\1\s*(,\s*\{)?/g)) {
    const key = m[2];
    used.add(key);
    const line = code.slice(0, m.index).split('\n').length;
    if (!(key in de)) fail(`${rel}:${line}: unknown key ${key}`);
    else if (!(key in en)) fail(`${rel}:${line}: key ${key} missing in en`);
    else {
      const need = placeholders(de[key]);
      if (m[3]) {
        const given = objectKeys(code, m.index + m[0].length - 1);
        const missing = need.filter((p) => !given.includes(p));
        if (missing.length) fail(`${rel}:${line}: ${key} needs {${missing}}`);
      } else if (need.length && !/^\s*,/.test(code.slice(m.index + m[0].length))) {
        fail(`${rel}:${line}: ${key} needs {${need}} but gets no parameters`);
      }
    }
  }
  for (const m of code.matchAll(/(?<![\w.$])t\(\s*`[^`]*\$\{/g)) fail(`${rel}:${code.slice(0, m.index).split('\n').length}: dynamic t(\`…\${}\`) key; build the key in a variable and list the possible keys in an "i18n-used:" comment`);
  if (isHtml) for (const m of code.matchAll(/data-i18n(?:-title|-aria)?="([^"]+)"/g)) {
    used.add(m[1]);
    if (!(m[1] in de)) fail(`${rel}: unknown key ${m[1]}`);
  }

  // German left in code: umlauts inside string literals / template literals / html text
  code.split('\n').forEach((line, i) => {
    if (/i18n-ignore/.test(src.split('\n')[i] ?? '')) return;
    if (!umlaut.test(line)) return;
    if (isHtml && !/>[^<]*[äöüßÄÖÜ]|=\s*"[^"]*[äöüßÄÖÜ]/.test(line)) return;
    if (!isHtml && !/(['"`])(?:(?!\1).)*[äöüßÄÖÜ]/.test(line)) return;
    fail(`${rel}:${i + 1}: German text in code: ${line.trim().slice(0, 90)}`);
  });
}

// --- 3. orphans ---------------------------------------------------------------------------------------------------
for (const k of Object.keys(de)) if (!used.has(k)) fail(`${k}: not used anywhere (remove it, or list it in an "i18n-used:" comment if built dynamically)`);

if (errors.length) {
  console.error(`i18n: ${errors.length} problem(s)`);
  for (const e of errors.slice(0, 200)) console.error('  ' + e);
  if (errors.length > 200) console.error(`  … and ${errors.length - 200} more`);
  process.exit(1);
}
console.log(`i18n: ok (${Object.keys(de).length} keys, de + en)`);
if (verbose) for (const f of FILES) console.log(`  ${f}`);
