// Optional mouse: a big pointer that shows while the mouse moves and hides again
// after a moment or as soon as the controller is used. Clicks act on what they
// hit; the controller focus stays where it is until something is clicked.

import { el } from '../core/dom.js';
import { state } from '../core/state.js';
import { currentView, switchTab } from '../core/nav.js';
import { setTabHover, tabAtPoint } from '../core/tab-bar.js';
import { quick } from './quick-menu.js';
import { isCodeEntry, tabClickAllowed } from '../core/input-help.js';

const HIDE_AFTER_MS = 2200;
const WHEEL_STEP = 60;

let cursor;
let pointerParts = null; // { pos, box, art } inside the closed root of #cursor
let pointerMode = 'skin';
let hideTimer;
let last = { x: -1, y: -1 };
let wheelAccumulator = 0;
let movingTimer;

// The easter egg pointer (unlocked on the info page): a cartoon mouse whose nose is the hotspot. The tail wags while
// it moves, it blinks now and then, and the scarf takes the skin's accent color.
const MOUSE_SVG = `
<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">
  <g class="cm-tail"><path d="M46 46 C54 55 62 50 58 42 C55 36 60 31 63 31" fill="none" stroke="#1b1e28" stroke-width="5" stroke-linecap="round"/>
    <path d="M46 46 C54 55 62 50 58 42 C55 36 60 31 63 31" fill="none" stroke="#f3a5b6" stroke-width="2.4" stroke-linecap="round"/></g>
  <ellipse class="cm-body" cx="36" cy="36" rx="15" ry="12.5" transform="rotate(45 36 36)" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="28.5" cy="14.5" r="7.5" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="28.5" cy="14.5" r="4" fill="#f3a5b6"/>
  <circle cx="14.5" cy="28.5" r="7.5" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <circle cx="14.5" cy="28.5" r="4" fill="#f3a5b6"/>
  <ellipse cx="17" cy="17" rx="12.5" ry="9.5" transform="rotate(45 17 17)" fill="#dfe3ec" stroke="#1b1e28" stroke-width="2.4"/>
  <path class="cm-scarf" d="M19 31 Q24 24 31 19" fill="none" stroke-width="4.5" stroke-linecap="round"/>
  <g class="cm-eyes"><circle cx="17.5" cy="11.8" r="2" fill="#1b1e28"/><circle cx="11.8" cy="17.5" r="2" fill="#1b1e28"/>
    <circle cx="18.1" cy="11.2" r="0.7" fill="#fff"/><circle cx="12.4" cy="16.9" r="0.7" fill="#fff"/></g>
  <circle cx="6.5" cy="6.5" r="3.2" fill="#ff6f91" stroke="#1b1e28" stroke-width="1.6"/>
  <path d="M9 3 L15 1 M10 5 L16 4 M3 9 L1 15 M5 10 L4 16" stroke="#1b1e28" stroke-width="1" stroke-linecap="round" opacity="0.7"/>
</svg>`;

/**
 * The pointer inside the HUD: "skin" (the skin's --pointer image, the default), "system" (the real
 * Windows pointer, no overlay) or "mouse" (the comic mouse). Falls back to "skin" without the unlock.
 */
const POINTER_CSS = `
:host { all: initial !important; display: block !important; position: fixed !important; top: 0 !important; left: 0 !important; width: 0 !important; height: 0 !important;
  overflow: visible !important; pointer-events: none !important; opacity: 1 !important; visibility: visible !important; z-index: 2147483645 !important; zoom: 1 !important;
  content-visibility: visible !important; contain: none !important; aspect-ratio: auto !important; }
:host(:not([data-live])), :host([data-off]), :host(:not(:popover-open):not([data-nopopover])) { display: none !important; }
:host::before, :host::after { content: none !important; display: none !important; }
:host::backdrop { display: none !important; background: none !important; }
.pos { position: absolute; left: 0; top: 0; }
.box { --u: calc(100vh / 54); width: var(--p-size); height: var(--p-size); margin: calc(var(--p-size) * var(--p-hy) * -1) 0 0 calc(var(--p-size) * var(--p-hx) * -1);
  contain: paint; overflow: hidden; filter: drop-shadow(0 0 calc(var(--u) * 0.5) var(--accent-glow)) drop-shadow(0 calc(var(--u) * 0.25) calc(var(--u) * 0.4) rgba(0, 0, 0, 0.55)); }
.cp { display: block; width: 100%; height: 100%; background: var(--pointer) 0 0 / 100% 100% no-repeat; transform-origin: calc(var(--p-hx) * 100%) calc(var(--p-hy) * 100%); }
.box.click .cp { animation: cm-squish 0.22s ease-out; }
svg { width: 100%; height: 100%; overflow: visible; transform-origin: 10% 10%; }
.box.click svg { animation: cm-squish 0.22s ease-out; }
.cm-scarf { stroke: var(--accent); }
.cm-tail { transform-origin: 46px 46px; transform-box: view-box; }
.box.moving .cm-tail { animation: cm-wag 0.35s ease-in-out infinite alternate; }
.cm-eyes { transform-origin: 15px 15px; transform-box: view-box; animation: cm-blink 4.5s infinite; }
.off .box.click .cp, .off .box.click svg, .off .cm-eyes, .off .cm-tail { animation: none; }
@keyframes cm-wag { from { transform: rotate(-14deg); } to { transform: rotate(14deg); } }
@keyframes cm-blink { 0%, 94%, 100% { transform: scale(1); } 97% { transform: scale(1, 0.1); } }
@keyframes cm-squish { 40% { transform: scale(0.82); } 100% { transform: scale(1); } }
`;

export function setPointer(mode, mouseUnlocked) {
  const next = mode === 'system' ? 'system' : mode === 'mouse' && mouseUnlocked ? 'mouse' : 'skin';
  document.documentElement.dataset.pointer = next;
  // The tab bar is a closed shadow root that the page's CSS cannot reach: the host carries the answer for it.
  document.getElementById('top')?.toggleAttribute('data-real-pointer', next === 'system');
  pointerMode = next;
  if (cursor) {
    // The real Windows pointer shows: no overlay (core/layers.js keeps the pointer out of the top layer then).
    cursor.toggleAttribute('data-off', next === 'system');
    pointerParts.art.innerHTML = next === 'mouse' ? MOUSE_SVG : '<i class="cp"></i>';
    sizePointer();
  }
}

/** The pointer's box from the skin's --pointer-size and hotspot, held to a few rem (the comic mouse keeps its own); set inside the closed root. */
function sizePointer() {
  const cs = getComputedStyle(document.documentElement);
  const rem = innerHeight / 54;
  const num = (name, [min, max, def]) => { const v = parseFloat(cs.getPropertyValue(name)); return Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : def; };
  const len = (name, [min, max, def]) => {
    const m = /^\s*(-?\d*\.?\d+)\s*(rem|px)?\s*$/i.exec(cs.getPropertyValue(name));
    return m ? Math.min(max, Math.max(min, (m[2]?.toLowerCase() === 'px' ? Number(m[1]) / rem : Number(m[1])))) : def;
  };
  const comic = pointerMode === 'mouse';
  const size = comic ? 4.6 : len('--pointer-size', [1, 8, 4.6]);
  const hx = comic ? 0.1 : num('--pointer-hx', [0, 1, 0.104]);
  const hy = comic ? 0.1 : num('--pointer-hy', [0, 1, 0.0625]);
  const style = pointerParts.box.style;
  style.setProperty('--p-size', `${size * rem}px`);
  style.setProperty('--p-hx', String(hx));
  style.setProperty('--p-hy', String(hy));
  pointerParts.pos.classList.toggle('off', document.documentElement.dataset.motion === 'off');
}

function show(x, y) {
  pointerParts.pos.style.transform = `translate(${x}px, ${y}px)`;
  pointerParts.box.classList.add('moving');
  clearTimeout(movingTimer);
  movingTimer = setTimeout(() => pointerParts.box.classList.remove('moving'), 180);
  sizePointer();
  document.body.classList.add('mouse-active');
  cursor.toggleAttribute('data-live', true);
  clearTimeout(hideTimer);
  hideTimer = setTimeout(hide, HIDE_AFTER_MS);
}

export function hide() {
  if (!cursor) return;
  clearTimeout(hideTimer);
  document.body.classList.remove('mouse-active');
  cursor.removeAttribute('data-live');
  for (const n of document.querySelectorAll('.hover')) n.classList.remove('hover');
}

/** Index of a clickable thing inside its list (tiles, rows, keys, buttons). */
function indexIn(node) {
  return node?.parentElement ? [...node.parentElement.children].indexOf(node) : -1;
}

function target(e) {
  return e.target instanceof Element ? e.target : null;
}

const CLICKABLE = '.passcode-key, .timeup-option, .quick-row, .ha-card, .news-card, .tile, .action, .settings li, .settings-cats li, .file-row, .check-row, .chip, .profile-tile, .wizard-option, .osk-key, .bbar-btn, .bbar-address, .menu li:not(.menu-group), .hint';

// The code prompt without a menu over it: only clicks on its buttons count there.
const codeEntry = () => !state.overlay && !quick.open && isCodeEntry(currentView());

function onClick(e, dispatch) {
  // A click next to an open menu closes it, like on any desktop, and does nothing else (not a tile behind it). The hint bar
  // belongs to the menu: its buttons act on it.
  if (state.overlay && !target(e)?.closest('.dialog, #hints')) return dispatch('b');
  // The tabs live in the bar's closed shadow root: the page only sees the bar as one element, the bar says which tab is at the point.
  const tab = tabAtPoint(e.clientX, e.clientY);
  if (tab) {
    // A page that holds the screen (code prompt, time-up page, required profile picker) is not left by a click on a tab, as LB/RB do not leave it.
    // (an open guide menu still closes on the click, as everywhere else)
    if (!tabClickAllowed(currentView(), state)) return quick.open ? dispatch('b') : undefined;
    return quick.open ? dispatch('b') : switchTab(Number(tab.dataset.tab));
  }
  const node = target(e)?.closest(CLICKABLE);
  if (!node) {
    if (quick.open && !target(e)?.closest('#quick')) dispatch('b');
    return;
  }

  if (node.matches('.quick-row')) {
    quick.index = indexIn(node);
    return dispatch('a');
  }
  // Clicking next to the open guide menu closes it.
  if (quick.open) return dispatch('b');
  if (node.matches('.hint')) return dispatch(node.dataset.btn === '≡' ? 'start' : node.dataset.btn.toLowerCase());
  if (node.matches('.menu li')) {
    // Group headings sit between the entries: the entry's own index, not its place among the list's children.
    state.overlay.index = node.dataset.index ? Number(node.dataset.index) : indexIn(node);
    return dispatch('a');
  }

  const view = currentView();
  if (view.pointer) return view.pointer(node, indexIn(node), dispatch);

  view.index = indexIn(node);
  view.update?.();
  dispatch('a');
}

function onWheel(e, dispatch) {
  // On the code prompt the arrows are code buttons: a wheel must not type them.
  if (codeEntry()) {
    wheelAccumulator = 0;
    return;
  }
  wheelAccumulator += e.deltaY;
  if (Math.abs(wheelAccumulator) < WHEEL_STEP) return;
  const forward = wheelAccumulator > 0;
  wheelAccumulator = 0;
  const view = currentView();
  // A two-pane page (settings) scrolls the pane the mouse is over, not the one the controller was last in.
  view.wheelAt?.(target(e));
  const horizontal = view.wheelAxis === 'x';
  dispatch(horizontal ? (forward ? 'right' : 'left') : (forward ? 'down' : 'up'));
}

/** dispatch(action): the same entry point the controller uses. */
export function attachMouse(dispatch, { pointer, mouseUnlocked } = {}) {
  // The pointer stands in the top layer, above the tab bar (core/layers.js), only while the mouse is active. It is the host of a closed shadow root with
  // its own box (all: initial, held from inside), so no rule of a skin, no ::backdrop or outline, can make it cover the tabs; the skin's --pointer image,
  // --pointer-size (held to 8 rem) and hotspot are read here and set inside.
  cursor = el('div', { id: 'cursor', ariaHidden: 'true' });
  const root = cursor.attachShadow({ mode: 'closed' });
  const art = el('div', { className: 'art' });
  const box = el('div', { className: 'box' }, art);
  const pos = el('div', { className: 'pos' }, box);
  art.style.display = 'contents';
  root.append(el('style', { textContent: POINTER_CSS }), pos);
  pointerParts = { pos, box, art };
  // The host rules inside the root are !important and beat the UA rule that keeps a closed popover display:none, so the host says itself when it shows:
  // only while the mouse is in use (data-live), never with the real Windows pointer (data-off), and in the top layer when popovers exist.
  if (typeof HTMLElement.prototype.showPopover !== 'function') cursor.setAttribute('data-nopopover', '');
  document.body.append(cursor);
  setPointer(pointer, mouseUnlocked);

  window.addEventListener('mousemove', (e) => {
    // Ignore the synthetic moves browsers send when the layout shifts under a still mouse.
    if (Math.abs(e.clientX - last.x) + Math.abs(e.clientY - last.y) < 3) return;
    last = { x: e.clientX, y: e.clientY };
    show(e.clientX, e.clientY);
    for (const n of document.querySelectorAll('.hover')) n.classList.remove('hover');
    target(e)?.closest(CLICKABLE)?.classList.add('hover');
    setTabHover(tabAtPoint(e.clientX, e.clientY));
  });
  window.addEventListener('mousedown', (e) => {
    e.preventDefault();
    show(e.clientX, e.clientY);
    pointerParts.box.classList.remove('click');
    void pointerParts.box.offsetWidth; // restart the squish animation
    pointerParts.box.classList.add('click');
    if (e.button === 0) onClick(e, dispatch);
    // Right button and the side button are B; on the code prompt B is a code button, so they type nothing there.
    if ((e.button === 2 || e.button === 3) && !codeEntry()) dispatch('b');
  });
  window.addEventListener('contextmenu', (e) => e.preventDefault());
  window.addEventListener('wheel', (e) => {
    e.preventDefault();
    show(e.clientX, e.clientY);
    onWheel(e, dispatch);
  }, { passive: false });
  document.documentElement.addEventListener('mouseleave', hide);
}
