// Tabs, the page stack on top of them, and the render/update cycle.
//
// Views build their DOM once (build) and only adjust focus, texts and position
// while navigating (update). Rebuilding on every move made lists flash back to
// their start for a frame and killed all transitions.
//
// The tab bar itself is a closed shadow root in the top layer (core/tab-bar.js); this file fills it, fits the tabs into the room the status area
// leaves (core/tab-fit.js) and answers whether a skin may stay (barSane). A skin cannot hide, cover or reshape a tab any more, so none of the
// old runtime guards (inline holds, hit tests, text measurement) is left.

import { $, el } from './dom.js';
import { t } from './i18n.js';
import { state } from './state.js';
import { sound } from '../sound.js';
import { stopTrailer } from './media.js';
import { activeAfter, isPinnedTab } from './plugin-tabs.js';
import { TAB_EDGE_SLOP, TAB_GUTTER_REM, TAB_MAX_REM, TAB_STUB_REM, fitWidths, marqueeOffset, minTabWidth, needsMarquee, overflowHints, tabWindow } from './tab-fit.js';
import { applySkin, glyph } from './skins.js';
import { BUILTIN_SKINS } from '../skins/index.js';
import { cursorStep } from './paging.js';
import { clearStackOf } from './stack-rules.js';
import { markBusy } from './busy.js';
import { applyBarSkin, barRefs, barSane, coreRem, holdAncestors, setBarDim, setBarTabsHidden } from './tab-bar.js';

let tabs = [];

/** Sets the tab views; the active tab stays active even when tabs are added before it. */
export function setTabs(views) {
  // The active tab is gone (a plugin was switched off or failed, its area went off): back to the first tab.
  state.tab = activeAfter(tabs, state.tab, views);
  tabs = views;
}

export const tabViews = () => tabs;

export const currentView = () => state.stack.at(-1) ?? tabs[state.tab];

/** Rebuilds the current view; for tab switches and changed data, not for moving the cursor. */
let lastRendered = null;
let lastTab = 0;

export function render() {
  renderTabs();
  const root = $('#view');
  const view = currentView();
  // Views with media restart their trailer in build(); everything else stays calm.
  if (!view.keepsTrailer) stopTrailer();
  root.replaceChildren();
  root.dataset.view = view.id || view.name.toLowerCase();
  document.body.dataset.view = root.dataset.view;
  view.build(root);
  renderHints();

  // Slide in when the screen changes, not when the same view redraws.
  if (lastRendered !== view) {
    const direction = state.stack.length || lastRendered?.id === 'detail' ? 'up' : state.tab >= lastTab ? 'left' : 'right';
    root.classList.remove('enter-left', 'enter-right', 'enter-up');
    void root.offsetWidth;
    root.classList.add(`enter-${direction}`);
  }
  // Leaving a tab for another tab: it may finish what it still holds (a plugin tab sends a value that is settling).
  if (lastRendered && lastRendered !== view && tabs.includes(lastRendered) && tabs.includes(view)) lastRendered.onLeave?.();
  lastRendered = view;
  lastTab = state.tab;
}


/** One entry of the tab bar: text only, the same element and class for every tab (a plugin cannot add an icon or its own look). */
function tabItem(view, active, index) {
  const li = el('li', { className: active ? 'active' : '' }, el('span', { className: 'tab-label', textContent: view.name }));
  li.dataset.tab = String(index);
  // A label that is cut to fit still has its full name for assistive technology.
  li.setAttribute('aria-label', view.name);
  return li;
}

/** Which chip at the right of the bar has the focus ("bell", "downloads") or null: LB/RB walk from the last tab on to the chips. */
export const bar = { chip: null };

export function renderTabs() {
  const { list } = barRefs();
  // Assistant and Settings stand apart at the end and never scroll out of sight; the others scroll (with many plugin tabs). All of them
  // are list items of the one list in the bar's shadow root.
  const nodes = tabs.map((v, i) => {
    const li = tabItem(v, i === state.tab, i);
    if (isPinnedTab(v)) li.classList.add('pinned');
    return li;
  });
  list.replaceChildren(...nodes);
  fitTabs();
  showChipFocus();
}

/** The focus ring of the chip that has the focus (the same look as the active tab). */
export function showChipFocus() {
  for (const [id, sel] of [['bell', '#notify-bell'], ['downloads', '#downloads-status']]) $(sel)?.classList.toggle('chip-focus', bar.chip === id);
}

let fitObserver = null;
let geometry = { region: 0, content: 0, startX: 0, endX: 0, scroll: false };
const marquee = { li: null, text: '', raf: 0, start: 0 };

/** The two hints at the edges of the bar that say where tabs are hidden: shown only on the side where tabs are hidden, at that edge of the list. */
function showOverflowHints(list, nav, more, rootPx) {
  const width = TAB_GUTTER_REM * rootPx;
  // A bar that fits shows no arrow and cuts nothing, whatever rounding says about the last pixel.
  const due = geometry.scroll ? overflowHints(list.scrollLeft, geometry.region, geometry.content) : { start: false, end: false };
  // Tabs show only inside the room between the arrows (the gutters are only where tabs are hidden): nothing is drawn under an arrow or
  // behind the pinned tabs. A tab that is cut there fades out softly; one with less than about 2rem left is not shown at all. The
  // focused tab is never cut.
  const barLeft = nav.getBoundingClientRect().left;
  const from = barLeft + geometry.startX + (due.start ? width : 0);
  const to = barLeft + geometry.endX - (due.end ? width : 0);
  const stub = TAB_STUB_REM * rootPx;
  const fade = rootPx;
  const slop = TAB_EDGE_SLOP;
  // All the boxes are read first, then the cuts are written: no layout in between.
  const boxes = [...list.children].filter((li) => !li.classList.contains('pinned')).map((li) => [li, li.getBoundingClientRect()]);
  for (const [li, box] of boxes) {
    if (!(due.start || due.end) || li.classList.contains('active')) {
      setCut(li, 'none', 'none');
      continue;
    }
    const shownFrom = Math.max(box.left, from);
    const shownTo = Math.min(box.right, to);
    const shown = shownTo - shownFrom;
    const cutLeft = shownFrom - box.left;
    const cutRight = box.right - shownTo;
    if (cutLeft < slop && cutRight < slop) {
      setCut(li, 'none', 'none');
    } else if (shown <= 0.5 || shown < Math.min(stub, box.width)) {
      setCut(li, 'inset(0 100% 0 0)', 'none');
    } else {
      const f = Math.min(fade, shown / 2);
      const stops = [];
      if (cutLeft >= slop) stops.push(`transparent ${cutLeft}px`, `#000 ${cutLeft + f}px`);
      if (cutRight >= slop) stops.push(`#000 ${box.width - cutRight - f}px`, `transparent ${box.width - cutRight}px`);
      setCut(li, `inset(0 ${cutRight}px 0 ${cutLeft}px)`, `linear-gradient(to right, ${stops.join(', ')})`);
    }
  }
  for (const [side, on, left] of [['start', due.start, geometry.startX], ['end', due.end, geometry.endX - width]]) {
    const node = more[side];
    node.style.display = on ? 'flex' : 'none';
    if (on) {
      node.style.left = `${left}px`;
      node.style.width = `${width}px`;
    }
  }
}

/** Both the plain and the prefixed property: the tab is faded out toward a cut edge or shown as it is. */
function setCut(li, clip, mask) {
  li.style.clipPath = clip;
  li.style.webkitMaskImage = mask;
  li.style.maskImage = mask;
}

/** Ends the run-through: the label is plain text again (an ellipsis when it is cut). */
function stopMarquee() {
  if (marquee.raf) cancelAnimationFrame(marquee.raf);
  marquee.raf = 0;
  const li = marquee.li;
  marquee.li = null;
  if (li?.isConnected) setLabel(li, marquee.text);
}

/** The label of a tab as the one inner span. */
function setLabel(li, text) {
  li.replaceChildren(el('span', { className: 'tab-label', textContent: text }));
}

/**
 * The focused tab's label runs through when it does not fit: a pause of a second, to the end, a pause, back; two rounds, then it rests. The
 * text moves inside a clip of the tab's content box (an inner span, translated), so the tab's padding or pill never cuts a letter in half.
 * It stops when the focus leaves the bar, a page or dialog opens, or the screen is hidden.
 */
function startMarquee(li) {
  stopMarquee();
  const text = li.textContent;
  const clip = el('span', { className: 'clip' });
  const run = el('span', { className: 'run', textContent: text });
  clip.append(run);
  li.replaceChildren(clip);
  const max = run.offsetWidth - clip.clientWidth;
  if (max <= 1) {
    setLabel(li, text);
    return;
  }
  marquee.li = li;
  marquee.text = text;
  marquee.start = performance.now();
  const rounds = 2 * 2 * (1000 + max / 0.05);
  const step = (now) => {
    const away = state.overlay || state.stack.length || bar.chip || document.hidden || document.documentElement.dataset.motion !== 'on';
    if (marquee.li !== li || !li.isConnected || away || now - marquee.start >= rounds) {
      if (marquee.li === li) stopMarquee();
      return;
    }
    run.style.transform = `translateX(${-marqueeOffset(now - marquee.start, max)}px)`;
    marquee.raf = requestAnimationFrame(step);
  };
  marquee.raf = requestAnimationFrame(step);
}

// What the check found out about a skin (id -> may stay), and who waits for it.
const verdicts = new Map();
const waiting = [];
const rejectListeners = [];
function setVerdict(id, ok) {
  verdicts.set(id, ok);
  for (const w of waiting.filter((x) => x.id === id)) w.done(ok);
}

/** Whether the skin was replaced by the default one because the bar was not there with it (see barSane). */
export const skinRejected = (id) => verdicts.get(id) === false;
/** `fn(id)` runs when a skin was replaced by the default one. */
export function onSkinRejected(fn) {
  rejectListeners.push(fn);
}
/**
 * Whether the skin that was just applied may stay: resolves when the bar has been looked at with it (false: it was replaced by the default
 * skin and must not be saved). Without an answer in time (the screen is hidden, the setup assistant is open) it is let through.
 */
export function skinVerdict(id, timeout = 1500) {
  if (id === BUILTIN_SKINS[0].id) return Promise.resolve(true);
  if (verdicts.has(id)) return Promise.resolve(verdicts.get(id));
  return new Promise((resolve) => {
    const entry = { id, done: (ok) => { clearTimeout(timer); waiting.splice(waiting.indexOf(entry), 1); resolve(ok); } };
    const timer = setTimeout(() => entry.done(true), timeout);
    waiting.push(entry);
  });
}

// The skin's stylesheet has to be in before its bar is judged (the load fits again): the answer is for the skin, not for the page before it.
let loadedSkin = '';
/** The sanity check after a fit: the bar is in the top layer and has a size. Structure does the rest. */
function checkBar() {
  const root = document.documentElement;
  if ($('#skin-css')?.getAttribute('href') && loadedSkin !== root.dataset.skin) return;
  if (document.hidden) return;
  const id = root.dataset.skin;
  const result = barSane();
  if (result.ok) {
    setVerdict(id, true);
    return;
  }
  if (id === BUILTIN_SKINS[0].id) return;
  console.warn(`tab bar: ${result.why} with the skin ${id}; back to the default skin`);
  setVerdict(id, false);
  applySkin(BUILTIN_SKINS[0]);
  for (const fn of rejectListeners) {
    try {
      fn(id);
    } catch (err) {
      console.error(err);
    }
  }
}

/** The room the bar takes at the top, kept free in the page flow (the bar itself is out of the flow, in the top layer). */
function keepRoom(host) {
  const gap = document.getElementById('top-gap');
  if (gap) gap.style.height = `${host.offsetHeight}px`;
  // The toasts and the notification cards sit below the bar, whatever its height (--bar-h in styles.css).
  document.documentElement.style.setProperty('--bar-h', `${host.offsetHeight}px`);
}

/**
 * Fits the tabs into what the bar really has: the width of the tab area (which takes what the status area leaves) minus the shoulder glyphs
 * and the pinned tabs (Assistant, Settings). Tabs that all fit keep their width. Otherwise the gap gives way a little, then the wide tabs
 * shrink (fitWidths: the short ones and the focused one stay, none gets smaller than the minimum width) and a label that is cut shows an
 * ellipsis. Only when even that is not enough does the list scroll with the tabs at their natural width (the focused tab is always kept
 * whole, with an arrow on the side where tabs are hidden). No tab is ever hidden. Runs again when the status, the bar, the skin, the view,
 * the motion setting or the fonts change.
 */
export function fitTabs() {
  const { host, list, nav, more } = barRefs();
  stopMarquee();
  holdAncestors();
  setBarTabsHidden(document.body.dataset.view);
  setBarDim(document.body.classList.contains('quick-open') || document.body.classList.contains('overlay-open'));
  const skin = applyBarSkin();
  const all = [...list.children];
  const items = all.filter((li) => !li.classList.contains('pinned'));
  const pins = all.filter((li) => li.classList.contains('pinned'));
  const rootPx = coreRem();
  // Back to the natural look first: what the skin asks for is the starting point, measured, never assumed.
  list.style.columnGap = '';
  for (const li of all) {
    li.style.removeProperty('width');
    li.style.removeProperty('right');
  }
  const minWidth = minTabWidth(skin.minWidth, rootPx);
  const natural = parseFloat(getComputedStyle(list).columnGap) || 0;
  const barGap = parseFloat(getComputedStyle(nav).columnGap) || 0;
  const others = [...nav.children].filter((c) => c !== list && !c.classList.contains('more') && c.getClientRects().length > 0);
  // The list may have a padding or a border (a pill around the tabs): the tabs only get what is inside.
  const box = getComputedStyle(list);
  const inset = (side) => (parseFloat(box[`padding${side}`]) || 0) + (parseFloat(box[`border${side}Width`]) || 0);
  const avail = nav.clientWidth - others.reduce((sum, c) => sum + c.offsetWidth, 0) - barGap * others.length - inset('Left') - inset('Right');
  const pinW = pins.reduce((sum, li) => sum + Math.ceil(li.getBoundingClientRect().width), 0);
  // Naturals are rounded up and every tab keeps a pixel of slack: rounding must never push the last tab past the room.
  const naturals = items.map((li) => Math.ceil(li.getBoundingClientRect().width));
  const active = all.find((li) => Number(li.dataset.tab) === state.tab);
  const keep = items.indexOf(active);
  const roomFor = (g) => avail - pinW - g * pins.length - items.length;
  const maxWidth = TAB_MAX_REM * rootPx;
  let fit = fitWidths(naturals, roomFor(natural), natural, 0.8 * rootPx, minWidth, maxWidth, keep);
  // The gap that was chosen also stands between the last tab and the pinned ones: count it there as well.
  if (fit.gap < natural) fit = fitWidths(naturals, roomFor(fit.gap), fit.gap, fit.gap, minWidth, maxWidth, keep);
  const gap = fit.gap;
  if (gap < natural) list.style.columnGap = `${gap}px`;
  items.forEach((li, i) => { if (fit.widths[i] < naturals[i]) li.style.width = `${fit.widths[i]}px`; });
  // The pinned ones stick to the right edge, each at its own offset from it (the widths of those after it).
  let right = 0;
  for (const li of [...pins].reverse()) {
    li.style.right = `${right}px`;
    right += li.offsetWidth + gap;
  }
  list.style.setProperty('--pinned-w', `${right}px`);
  // Where the tabs may stand, measured (the pinned ones are where the browser really put them): from the inside of the list's left edge to
  // the gap before the first pinned tab. Positions inside the scrolled content count from that left edge.
  const barBox = nav.getBoundingClientRect();
  const listBox = list.getBoundingClientRect();
  const startX = listBox.left + inset('Left') - barBox.left;
  const endX = (pins.length ? Math.min(...pins.map((li) => li.getBoundingClientRect().left)) - gap : listBox.right - inset('Right')) - barBox.left;
  const region = Math.max(0, endX - startX);
  // Where a tab stands in the scrolled content (read from the screen position, so the scroll it has now is taken out).
  const posOf = (li) => li.getBoundingClientRect().left - listBox.left - inset('Left') + list.scrollLeft;
  const last = items.at(-1);
  const content = last ? posOf(last) + last.offsetWidth : 0;
  geometry = { region, content, startX, endX, scroll: fit.scroll };
  if (fit.scroll) {
    if (active && !pins.includes(active)) {
      list.scrollLeft = tabWindow(items.map((li) => ({ left: posOf(li), width: li.offsetWidth })), region, TAB_GUTTER_REM * rootPx, keep, list.scrollLeft);
    }
  } else {
    list.scrollLeft = 0;
  }
  showOverflowHints(list, nav, more, rootPx);
  keepRoom(host);
  checkBar();
  if (active && needsMarquee(active.scrollWidth, active.clientWidth, document.documentElement.dataset.motion === 'on')) startMarquee(active);
  if (!fitObserver && typeof ResizeObserver === 'function') {
    fitObserver = new ResizeObserver(() => fitTabs());
    for (const node of [$('#status'), nav, host]) if (node) fitObserver.observe(node);
    window.addEventListener('resize', () => fitTabs());
    // A skin's @font-face or values inside a media query apply when the query changes: the fonts and values are read again then.
    for (const q of ['(prefers-color-scheme: dark)', '(prefers-reduced-motion: reduce)', '(prefers-contrast: more)', '(forced-colors: active)', '(orientation: portrait)', '(hover: hover)', '(dynamic-range: high)']) {
      matchMedia(q).addEventListener?.('change', () => fitTabs());
    }
    // Fonts that arrive late and a skin's stylesheet change the widths of the tabs: fit again.
    document.fonts?.addEventListener?.('loadingdone', () => fitTabs());
    document.fonts?.ready?.then(() => fitTabs());
    $('#skin-css')?.addEventListener('load', () => { loadedSkin = document.documentElement.dataset.skin; verdicts.delete(loadedSkin); fitTabs(); });
    // A skin, the motion setting, HDR or the screen (the setup assistant hides the tabs) changed: measure again (the skin's stylesheet may
    // still be on its way, its load fits once more).
    new MutationObserver(() => fitTabs()).observe(document.documentElement, { attributes: true, attributeFilter: ['data-skin', 'data-motion', 'data-hdr'] });
    new MutationObserver(() => setBarDim(document.body.classList.contains('quick-open') || document.body.classList.contains('overlay-open'))).observe(document.body, { attributes: true, attributeFilter: ['class'] });
    new MutationObserver(() => fitTabs()).observe(document.body, { attributes: true, attributeFilter: ['data-view'] });
    // A skin's values can change without an event (a media query, a state): every 30 seconds the bar reads them again.
    setInterval(() => { if (!document.hidden) fitTabs(); }, 30_000);
    // Mouse, touch and the wheel scroll the bar when it scrolls at all; the hints follow. The wheel over a scrolling bar does not also
    // scroll the page behind it.
    list.addEventListener('scroll', () => showOverflowHints(list, nav, more, coreRem()), { passive: true });
    list.addEventListener('wheel', (e) => {
      if (!geometry.scroll) return;
      list.scrollLeft += e.deltaX || e.deltaY;
      e.preventDefault();
      e.stopPropagation();
    }, { passive: false });
  }
}

export function renderHints() {
  const hints = state.overlay
    ? state.overlay.hints?.() ?? [['A', t('nav.select')], ['B', t('nav.back')]]
    : state.panel?.hints() ?? currentView().hints();
  // A third entry { busy: true } draws the hint greyed with a small spinner (ui/core/busy.js): the action runs, a press does nothing.
  $('#hints').replaceChildren(...hints.map(([btn, label, opts]) =>
    markBusy(el('span', { className: 'hint', dataset: { btn } },
      el('span', { className: `glyph btn-${btn.toLowerCase()}`, textContent: glyph(btn) }), label), !!opts?.busy)));
}

/**
 * Moves a view's cursor; bumps at the ends (sound and rumble only, nothing on screen moves). wrap: a horizontal tile row goes
 * round instead (see cursorStep). `clamp` is kept for the callers; every step stops at the ends unless it wraps.
 */
export function move(view, delta, length, clamp = false, wrap = false) {
  const next = cursorStep(view.index, delta, length, { wrap: wrap && !clamp });
  if (next === null) {
    sound.bump();
    return false;
  }
  view.index = next;
  sound.move();
  view.update();
  // The hints depend on what is focused now.
  renderHints();
  return true;
}

/**
 * Closes every page on the stack and tells each one (an open code prompt must
 * resolve, not hang). Sticky pages, like the first-start notice, stay.
 */
export function clearStack() {
  clearStackOf(state);
}

/** Removes one page if it is still open; never anything else. */
export function removePage(page) {
  const i = state.stack.indexOf(page);
  if (i >= 0) state.stack.splice(i, 1);
}

export function switchTab(tab) {
  const next = (tab + tabs.length) % tabs.length;
  clearStack();
  // A later exit of the browser must never restore pages from before this switch.
  state.browserOrigin = null;
  bar.chip = null;
  if (next === state.tab) return render();
  state.tab = next;
  tabs[next].onEnter?.();
  sound.tab();
  render();
}

/** Opens a page on top of the current one. */
export function push(page) {
  // A protected profile waits at the profile picker: no other page opens over it (its code prompts come through their own way).
  if (state.pickerHold && page.id !== 'profile-picker') return;
  state.stack.push(page);
  sound.select();
  render();
}

/** Closes the topmost page; false if there was none. */
export function pop() {
  if (!state.stack.length) return false;
  const page = state.stack.pop();
  page.onClose?.();
  sound.back();
  render();
  return true;
}

/** Sorting changes after favorites/launches; keep the tab's cursor on the same item. */
export function keepFocusOn(item) {
  const view = tabs[state.tab];
  const index = view.items ? view.items().findIndex((i) => i.id === item.id) : -1;
  if (index >= 0) view.index = index;
}
