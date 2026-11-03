// The tab bar as a structure a skin cannot take apart. It is the page's <header id="top">, made the host of a CLOSED shadow root and shown as a
// popover in the top layer:
//   - Document selectors do not reach into a shadow root, so no skin rule can hide, shrink, move, recolour or cover a tab or a label. The
//     host's own box is pinned from inside (:host with !important beats a document rule, important or not), its ::before/::after and
//     ::backdrop are switched off, and the status area (the host's light children, slotted in) is clipped to its own box.
//   - The tabs' wrapper starts from `all: initial`, so no inherited font, colour, text-shadow, stroke, size-adjust or spacing of body or html
//     reaches the letters; the core's own rules style them, with values from the skin's small set of custom properties (core/tab-skin.js),
//     cleaned and clamped in JS and set inline in the shadow root.
//   - Fonts only come from a system allowlist, and a name the skin declares with @font-face itself is dropped (document.fonts tells), so a
//     skin's size-adjust, ascent-override or descent-override cannot get at the letters.
//   - In the top layer nothing in the page, a fixed layer with z-index 9999 or a pseudo element of body included, can paint over the bar.
//     The core's own overlays that may stand over it are top-layer popovers shown after it, only while open (core/layers.js).
// What is left of the page around the bar: zoom, content-visibility and display of html and body are held inline (a zoom on an ancestor
// multiplies into the shadow tree, content-visibility and display remove the whole subtree), and a minimal sanity check (see barSane).
import { el } from './dom.js';
import { onGlyphsChanged, glyph } from './skins.js';
import { BAR_SKIN_VARS, familyKey, readBarSkin } from './tab-skin.js';
import { parseColor, rgbaText } from './tab-fit.js';
import { hasPopover, raiseOverlays, watchLayers } from './layers.js';

const CSS = `
*, *::before, *::after { box-sizing: border-box; }
:host {
  /* Everything starts from the initial value, important, so a property nobody thought of (anchor positioning, aspect-ratio, container queries, a
     future one) is covered too; then the core's own values. */
  all: initial !important;
  display: block !important; position: fixed !important; top: 0 !important; left: 0 !important; right: 0 !important; bottom: auto !important;
  width: auto !important; height: auto !important; z-index: 2147483646 !important; overflow: visible !important;
  opacity: 1 !important; visibility: visible !important; pointer-events: auto !important; aspect-ratio: auto !important;
  content-visibility: visible !important; zoom: 1 !important; direction: ltr !important; writing-mode: horizontal-tb !important;
  position-anchor: none !important; position-area: none !important; position-try-fallbacks: none !important; position-visibility: always !important; anchor-name: none !important;
  container-type: normal !important; translate: clamp(-4px, var(--shift-x, 0px), 4px) clamp(-3px, var(--shift-y, 0px), 3px) !important; transition: translate 2s ease-in-out !important;
}
:host::before, :host::after { content: none !important; display: none !important; }
:host::backdrop { display: none !important; background: none !important; }
.wrap { --u: calc(100vh / 54); position: relative; isolation: isolate; display: flex; align-items: center; justify-content: space-between; padding: var(--s-pad-top) calc(var(--u) * 4) 0; min-width: 0; }
/* The core's own backing: the theme's --bg at 0.8, fading out under the bar, whatever the page paints there (core/tab-skin.js checks the letters against it). */
.wrap::before { content: ""; position: absolute; z-index: -1; left: 0; right: 0; top: 0; bottom: calc(var(--u) * -2); pointer-events: none;
  background: linear-gradient(to bottom, var(--s-scrim) 0, var(--s-scrim) calc(100% - var(--u) * 2.5), transparent 100%); }
.wrap.dim { filter: brightness(0.55) blur(calc(var(--u) * 0.15)); transition: filter 0.3s; }
.tabs { all: initial; display: flex; align-items: center; flex: 1 1 0; position: relative; min-width: 0; gap: calc(var(--u) * 1.2);
  font-family: var(--s-font); font-size: calc(var(--u) * 1.35); line-height: normal; color: var(--s-glyph-color); direction: ltr; cursor: none; user-select: none; box-sizing: border-box; }
.tabs[data-hidden] { visibility: hidden; }
.tabs[data-noshoulder] .shoulder { visibility: hidden; }
.glyph { display: inline-grid; place-items: center; min-width: calc(var(--u) * 1.7); height: calc(var(--u) * 1.7); padding: 0 calc(var(--u) * 0.4);
  border-radius: var(--s-glyph-radius); font-size: var(--s-glyph-size); font-weight: 700; background: var(--s-glyph-bg); border: var(--s-glyph-border-w) solid var(--s-glyph-border); color: var(--s-glyph-color); opacity: 0.8; white-space: nowrap; }
.list { display: flex; gap: var(--s-gap); list-style: none; margin: 0; padding: var(--s-bar-pad); min-width: 0; overflow-x: auto; overflow-y: hidden; scrollbar-width: none;
  background: var(--s-bar-bg); border: var(--s-bar-border-w) solid var(--s-bar-border); border-radius: var(--s-bar-radius); }
.list::-webkit-scrollbar { display: none; }
.glass .list { backdrop-filter: blur(calc(var(--u) * 1.4)) saturate(1.5);
  box-shadow: inset 0 calc(var(--u) * 0.06) 0 rgba(255, 255, 255, 0.12), 0 var(--u) calc(var(--u) * 3) rgba(0, 0, 0, 0.35); }
li { position: relative; isolation: isolate; flex: none; display: block; max-width: calc(var(--u) * 16); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  font-size: var(--s-size); font-weight: var(--s-weight); letter-spacing: var(--s-letter); text-transform: var(--s-case); line-height: normal; color: var(--s-color);
  padding: var(--s-pad-y) var(--s-pad-x); border-bottom: var(--s-underline) solid transparent; }
li::before { content: ""; position: absolute; inset: 0; z-index: -1; background: var(--s-bg); border-radius: var(--s-radius);
  clip-path: polygon(0 0, calc(100% - var(--s-cut)) 0, 100% var(--s-cut), 100% 100%, 0 100%); }
li.pinned { position: sticky; }
li.hover:not(.active) { color: var(--s-color-active); }
li.active { color: var(--s-color-active); border-bottom-color: var(--s-accent); font-weight: var(--s-weight-active); }
li.active::before { background: var(--s-bg-active); }
li.active .tab-label, li.active .run { text-shadow: var(--s-glow); }
li .tab-label { display: inline; }
li .clip { display: block; overflow: hidden; }
li .run { display: inline-block; white-space: nowrap; }
.more { position: absolute; top: 0; bottom: 0; display: none; align-items: center; justify-content: center; z-index: 2; font-size: calc(var(--u) * 1.5); font-weight: 700;
  line-height: 1; color: var(--s-glyph-color); text-shadow: 0 0 calc(var(--u) * 0.5) rgba(0, 0, 0, 0.95); pointer-events: none; }
.slot { flex: 0 1 auto; display: flex; align-items: center; min-width: 0; max-width: 40%; height: calc(var(--u) * 4); contain: paint;
  color: var(--fg); font-family: var(--font); font-size: var(--u); font-weight: 400; line-height: normal; direction: ltr;
  padding: calc(var(--u) * 0.5); margin: calc(var(--u) * -0.5) calc(var(--u) * -0.5) calc(var(--u) * -0.5) var(--u); }
`;

let refs = null;

/** The pieces of the bar (made on first use). */
export function barRefs() {
  return refs ?? mountBar();
}

const html = document.documentElement;

function mountBar() {
  const host = document.getElementById('top');
  const root = host.attachShadow({ mode: 'closed' });
  const style = el('style', { textContent: CSS });
  const lb = el('span', { className: 'glyph shoulder', dataset: { glyph: 'LB' } });
  const rb = el('span', { className: 'glyph shoulder', dataset: { glyph: 'RB' } });
  const list = el('ol', { className: 'list' });
  const more = { start: el('span', { className: 'more more-start', textContent: '‹' }), end: el('span', { className: 'more more-end', textContent: '›' }) };
  for (const n of Object.values(more)) n.setAttribute('aria-hidden', 'true');
  const nav = el('nav', { className: 'tabs' }, lb, list, rb, more.start, more.end);
  const slot = el('div', { className: 'slot' }, document.createElement('slot'));
  const wrap = el('div', { className: 'wrap' }, nav, slot);
  root.append(style, wrap);
  refs = { host, root, wrap, nav, list, more, lb, rb };
  refreshBarGlyphs();
  onGlyphsChanged(refreshBarGlyphs);
  holdAncestors();
  applyBarSkin();
  showBar();
  watchLayers();
  // The preview in a plain browser (the mock API) and the checks reach into the closed root through this; the app (WebView2) never sets it.
  if (!window.chrome?.webview) window.__couchhudTabBar = { host, root };
  return refs;
}

/** The bar is shown (again): in the top layer, then every open overlay goes on top of it. */
export function showBar() {
  if (!refs) return;
  if (hasPopover()) {
    if (!refs.host.matches(':popover-open')) {
      try { refs.host.showPopover(); } catch { /* not connected */ }
    }
    raiseOverlays();
  }
}

/** LB and RB on the bar (the symbols of the pad in use; the skin's own labels). */
export function refreshBarGlyphs() {
  if (!refs) return;
  for (const n of [refs.lb, refs.rb]) n.textContent = glyph(n.dataset.glyph);
}

// What an ancestor of the bar can still do that reaches into the top layer: a zoom multiplies into the shadow tree, content-visibility and
// display take the whole subtree away; the root font size is the core's (the status area and the rest of the UI are measured in rem; the bar itself
// does not use rem at all, see applyBarSkin). (Opacity, filter, clip, mask and transform of html or body do not touch a top layer element.)
const ANCESTORS = [[html, [['zoom', '1'], ['content-visibility', 'visible'], ['display', 'block'], ['font-size', 'calc(100vh / 54)']]], [document.body, [['zoom', '1'], ['content-visibility', 'visible'], ['display', 'grid']]]];
export function holdAncestors() {
  for (const [node, pairs] of ANCESTORS) for (const [k, v] of pairs) node.style.setProperty(k, v, 'important');
  holdPanels();
}

// The guide menu and the dialog stand under the bar (core/layers.js), so nothing a skin says about their box can paint over a tab. One thing still
// reaches across: a 3D context (perspective, preserve-3d, translateZ) can be composited out of order by the browser. They are held flat and
// clipped inline (inline important beats every rule of a skin): paint containment makes the panel the containing block of fixed descendants
// and clips everything, 3D included, to its own box.
const PANELS = ['quick', 'overlay'];
const PANEL_HOLD = [['contain', 'paint'], ['overflow', 'clip'], ['perspective', 'none'], ['transform-style', 'flat'], ['backface-visibility', 'visible'], ['mix-blend-mode', 'normal'], ['rotate', 'none'], ['scale', 'none'], ['translate', 'none']];
function holdPanels() {
  for (const id of PANELS) {
    const node = document.getElementById(id);
    if (node) for (const [k, v] of PANEL_HOLD) node.style.setProperty(k, v, 'important');
  }
}

// Colours in any syntax the browser knows (oklch(), color(), color-mix() ...), painted on a 1x1 canvas and read back as rgba; null when it
// cannot paint them. Remembered: the bar asks for the same few colours again and again.
let paint = null;
const painted = new Map();
function toRgba(text) {
  const s = String(text ?? '').trim();
  if (parseColor(s)) return s;
  if (painted.has(s)) return painted.get(s);
  if (!paint) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 1;
    paint = canvas.getContext('2d', { willReadFrequently: true });
  }
  let out = null;
  if (paint && s) {
    // A colour the canvas does not take leaves the one before: two different ones before tell a valid colour from an invalid one.
    paint.fillStyle = '#000';
    paint.fillStyle = s;
    const first = paint.fillStyle;
    paint.fillStyle = '#fff';
    paint.fillStyle = s;
    if (paint.fillStyle === first) {
      paint.clearRect(0, 0, 1, 1);
      paint.fillRect(0, 0, 1, 1);
      const [r, g, b, a] = paint.getImageData(0, 0, 1, 1).data;
      out = rgbaText([r, g, b, a / 255]);
    }
  }
  if (painted.size > 256) painted.clear();
  painted.set(s, out);
  return out;
}

/** The families a skin declared itself with @font-face (document.fonts lists them all), as lower-case names. */
function declaredFonts() {
  const taken = new Set();
  try { document.fonts?.forEach((f) => taken.add(familyKey(f.family))); } catch { /* none */ }
  return taken;
}

/** The core's rem in px: a 54th of the screen height, whatever a skin does to the root font size. */
export const coreRem = () => (innerHeight > 0 ? innerHeight / 54 : 20);

/** The cleaned skin values for the bar (see core/tab-skin.js): the custom properties the skin sets, read from the host, clamped, set inline in the shadow root. */
export function applyBarSkin() {
  if (!refs) return null;
  const cs = getComputedStyle(refs.host);
  // The skin's @font-face rules are in document.fonts only once the styles are computed: ask for a value first, then for the fonts.
  void cs.getPropertyValue('--font');
  const values = readBarSkin((name) => cs.getPropertyValue(name).trim(), { remPx: coreRem(), toRgba, taken: declaredFonts() });
  const { glass, ...vars } = values;
  // The bar is measured in px derived from the core's own rem, never in the document's rem: every rem of a cleaned value becomes a multiple of --u.
  refs.wrap.style.setProperty('--u', `${Math.round(coreRem() * 1000) / 1000}px`);
  for (const [k, v] of Object.entries(vars)) refs.wrap.style.setProperty(k, v.replace(/(-?\d*\.?\d+)rem(?![a-z])/g, 'calc(var(--u) * $1)'));
  refs.wrap.classList.toggle('glass', glass);
  return { cs, minWidth: cs.getPropertyValue('--tab-min-width') };
}
export { BAR_SKIN_VARS };

/** The setup assistant hides the tabs (not the status area); on the keyboard and code pages LB/RB belong to the page, so their symbols go. Only the core's state decides that. */
export function setBarTabsHidden(view) {
  if (!refs) return;
  refs.nav.toggleAttribute('data-hidden', view === 'setup-wizard');
  refs.nav.toggleAttribute('data-noshoulder', view === 'keyboard' || view === 'passcode');
}

/** The dim behind the quick menu (the core's look). */
export function setBarDim(on) {
  if (refs) refs.wrap.classList.toggle('dim', !!on);
}

/** The tab (its `li`) under a point of the screen, or null; the closed root is only reachable from here. */
export function tabAtPoint(x, y) {
  if (!refs || document.elementFromPoint(x, y) !== refs.host) return null;
  return refs.root.elementFromPoint(x, y)?.closest?.('li[data-tab]') ?? null;
}

/** The mouse is over this tab (or none). */
export function setTabHover(li) {
  if (!refs) return;
  for (const n of refs.list.querySelectorAll('.hover')) if (n !== li) n.classList.remove('hover');
  li?.classList.add('hover');
}

/**
 * The only check that is left of the old runtime guard: the bar is in the top layer, has a size, and every tab has room. Anything else is
 * structure now. When the popover was closed by something, it is shown again once; false when the bar still is not there (then the skin is
 * replaced by the default one, as before, and the reason goes to the log).
 */
export function barSane() {
  if (!refs) return { ok: true };
  const { host, list } = refs;
  if (hasPopover() && !host.matches(':popover-open')) showBar();
  const popoverOpen = !hasPopover() || host.matches(':popover-open');
  const r = host.getBoundingClientRect();
  const tabs = [...list.children];
  const u = coreRem();
  if (!popoverOpen) return { ok: false, why: 'the bar is not in the top layer' };
  if (r.width < innerWidth * 0.5 || r.height < 0.75 * coreRem()) return { ok: false, why: `the bar has no size (${Math.round(r.width)}x${Math.round(r.height)})` };
  // At the top of the screen, in the viewport, not somewhere a skin put it.
  if (r.top < -8 || r.left < -8 || r.right > innerWidth + 8 || r.top > innerHeight * 0.25) return { ok: false, why: `the bar is not at the top of the screen (${Math.round(r.left)},${Math.round(r.top)})` };
  for (const li of tabs) {
    const b = li.getBoundingClientRect();
    if (b.width < 1) return { ok: false, why: 'a tab has no room' };
    // Letters of a readable size in a tab of a readable height (px, from the core's rem).
    if (b.height < 1.2 * u || parseFloat(getComputedStyle(li).fontSize) < 0.85 * u) return { ok: false, why: `a tab is squashed (${Math.round(b.height)}px, font ${getComputedStyle(li).fontSize})` };
  }
  return { ok: true };
}
