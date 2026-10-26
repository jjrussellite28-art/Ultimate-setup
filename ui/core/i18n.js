// Translations for the UI. Language: Config "Language" (auto | de | en); the host sends the resolved one in
// systemInfo().language. Text lookup is lazy: call t() when you build a view, never in a top-level constant.
//
//   t('area.key')                      plain text
//   t('area.key', { name: 'Zelda' })   "{name}" placeholders
//   t('area.key', { count: 3 })        value { one, other } picks by count; {count} is filled in too
//
// Fallback: active language, then English, then the key itself (so a missing text is visible, not blank).
import { api } from './api.js';
import { dictionaries } from '../i18n/index.js';

const SUPPORTED = ['de', 'en'];

/** English is the default: "auto" means English. German is used only when it is chosen in the settings. */
export function resolveLanguage(setting) {
  return SUPPORTED.includes(setting) ? setting : 'en';
}

let active = 'en';
let activeLocale = 'en-GB';

export function setLang(setting) {
  active = resolveLanguage(setting);
  // English: keep the Windows region (24 h clock in Europe, 12 h in the US); German is always de-DE.
  const nav = globalThis.navigator?.language || '';
  activeLocale = active === 'de' ? 'de-DE' : /^en-/i.test(nav) ? nav : 'en-GB';
  document.documentElement.lang = active;
}

export const lang = () => active;
export const locale = () => activeLocale;

function lookup(key, language) {
  return dictionaries[language]?.[key];
}

export function t(key, params = {}) {
  let text = lookup(key, active) ?? lookup(key, 'en');
  if (text === undefined) {
    console.warn(`[i18n] missing key ${key}`);
    return key;
  }
  if (typeof text === 'object') {
    const n = Number(params.count);
    text = n === 1 ? text.one : text.other;
  }
  return String(text).replace(/\{(\w+)\}/g, (m, name) => (name in params ? params[name] : m));
}

// The preview backend (mock-api.js) is loaded before this module finishes and cannot import it (cycle): it calls this.
globalThis.__couchhudT = t;

export const fmtNumber = (n, options) => Number(n).toLocaleString(activeLocale, options);
/** 35 -> "35 %" (de) or "35%" (en). */
export const fmtPercent = (n) => (Number(n) / 100).toLocaleString(activeLocale, { style: 'percent', maximumFractionDigits: 0 });
export const fmtTime = (date = new Date()) => new Date(date).toLocaleTimeString(activeLocale, { hour: '2-digit', minute: '2-digit' });
export const fmtDate = (date, options = { dateStyle: 'medium' }) => new Date(date).toLocaleDateString(activeLocale, options);
export const fmtDateTime = (date, options = { dateStyle: 'medium', timeStyle: 'short' }) => new Date(date).toLocaleString(activeLocale, options);
/** Case-insensitive search that follows the language. */
export const lower = (s) => String(s).toLocaleLowerCase(activeLocale);

/** Fills the static texts in index.html: data-i18n (text), data-i18n-title, data-i18n-aria. */
export function applyStatic(root = document) {
  for (const node of root.querySelectorAll('[data-i18n]')) node.textContent = t(node.dataset.i18n);
  for (const node of root.querySelectorAll('[data-i18n-title]')) node.title = t(node.dataset.i18nTitle);
  for (const node of root.querySelectorAll('[data-i18n-aria]')) node.setAttribute('aria-label', t(node.dataset.i18nAria));
}

// The language is known before any view is built: modules that import this one wait for it.
{
  let setting = 'auto';
  try {
    const info = await api.systemInfo();
    setting = info?.language ?? info?.languageSetting ?? 'auto';
  } catch {
    // Without host info: Windows/browser language.
  }
  // Previews in a plain browser can force a language: ?lang=en
  if (!window.chrome?.webview) setting = new URLSearchParams(location.search).get('lang') || setting;
  setLang(setting);
  applyStatic();
}
