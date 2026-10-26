// The Browser tab: its own page, like Home or Games. The page runs in a native web view (the host puts it exactly over
// the placeholder below the bar); the bar, the start page and the hints are HUD, so they follow every skin.
// Without a page open the tab is a start page: address or search, recent sites, bookmarks.
// The controller is either "in the page" (the host steers it: sticks are the mouse, or arrow keys on TV pages) or "in the
// bar" (the HUD steers: left/right pick a button, A presses, down or B go back to the page).

import { t } from '../core/i18n.js';
import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { hue } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render, renderHints, switchTab, tabViews } from '../core/nav.js';
import { state } from '../core/state.js';
import { openKeyboard } from '../components/keyboard.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { quick } from '../components/quick-menu.js';
import { sound } from '../sound.js';
import { ensureUnlocked } from '../components/passcode.js';

const ZOOMS = [100, 125, 150, 175, 200, 250];
const RECENT_KEY = 'couchhud.browser.recent';

let browserState = null;
let recent = loadRecent();
/** Where the user came from when the browser opened (tab and pages on top of it), for "back" on the first page. */
let origin = null;

function loadRecent() {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
  } catch {
    return [];
  }
}

function saveRecent() {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(recent));
  } catch { /* storage blocked: the list lives until the next start */ }
}

function remember(url, title) {
  if (!/^https?:/i.test(url ?? '')) return;
  recent = [{ url, title: title || host(url) }, ...recent.filter((r) => r.url !== url)].slice(0, 6);
  saveRecent();
}

/** X on the page: type into the text box that was clicked, with CouchHUD's own keyboard. */
async function typeIntoPage() {
  const field = await attempt(() => api.getBrowserField());
  if (!field) return api.browserFocus(true);
  const text = await openKeyboard({ title: field.label || t('browser.typeTitle'), value: field.value, secret: field.secret });
  if (text !== null && text !== undefined) {
    await attempt(() => api.setBrowserField(text, field.search && !field.multiline && !field.secret));
  } else {
    api.browserFocus(true);
  }
}

/** With the couch lock on, websites need the code: their upload and print dialogs lead into Windows. */
const open = async (text) => (await ensureUnlocked(t('browser.openLock')))
  && attempt(() => api.openBrowser(text), t('browser.openFailed'));

async function openAddress() {
  if (!(await ensureUnlocked(t('browser.openLock')))) return;
  const text = await openKeyboard({ title: t('browser.addressTitle'), hint: t('browser.addressHint') });
  if (!text) return;
  await open(text);
}

/** One letter for the badge: the site's name, not its country prefix (de.wikipedia.org gives W). */
function initial(url) {
  const parts = host(url).split('.');
  const name = parts.length > 2 ? parts.at(-2) : parts[0];
  return (name || '?').slice(0, 1).toUpperCase();
}

function host(url) {
  try {
    return new URL(url).host.replace(/^www\./, '');
  } catch {
    return url;
  }
}

async function bookmarkThis() {
  const s = browserState;
  if (!s?.open) return;
  const name = await openKeyboard({ title: t('browser.bookmarkName'), value: s.title || host(s.url) });
  if (name === undefined) return;
  browserState = (await attempt(() => api.addBookmark(name, s.url), t('browser.notSaved'))) ?? browserState;
  toast(t('browser.bookmarkSaved'));
  render();
}

// i18n-used: browser.bar.back browser.bar.forward browser.bar.reload browser.bar.zoomOut browser.bar.zoomIn browser.bar.fullscreen browser.bar.close browser.bar.lockMouse browser.bookmarkThis
// Bar buttons: Segoe Fluent Icons (Windows 11; Segoe MDL2 Assets before).
const BAR = [
  { id: 'back', icon: '', label: 'browser.bar.back', run: () => api.browserCommand('back'), enabled: (s) => s.canGoBack },
  { id: 'forward', icon: '', label: 'browser.bar.forward', run: () => api.browserCommand('forward'), enabled: (s) => s.canGoForward },
  { id: 'reload', icon: '', label: 'browser.bar.reload', run: () => api.browserCommand('reload') },
  { id: 'address', wide: true, run: () => openAddress() },
  { id: 'bookmark', icon: '', label: 'browser.bookmarkThis', run: () => bookmarkThis() },
  { id: 'zoomOut', icon: '', label: 'browser.bar.zoomOut', run: () => api.browserCommand('zoomOut') },
  { id: 'zoomIn', icon: '', label: 'browser.bar.zoomIn', run: () => api.browserCommand('zoomIn') },
  { id: 'fullscreen', icon: '', label: 'browser.bar.fullscreen', run: () => { focusPage(); return api.browserCommand('fullscreen'); } },
  { id: 'lock', icon: '', label: 'browser.bar.lockMouse', run: () => api.browserCommand('lockMouse') },
  { id: 'lock', icon: '', label: 'browser.bar.lockMouse', run: () => api.browserCommand('lockMouse') },
  { id: 'close', icon: '', label: 'browser.bar.close', run: () => api.browserCommand('close') }
];

export const browserView = {
  id: 'browser',
  get name() { return t('browser.title'); },
  index: 0,
  /** 'page': the host steers the page; 'bar': the HUD steers the bar. */
  focus: 'bar',
  barIndex: 0,
  onEnter() {
    // Coming by LB/RB: the controller stays in the HUD, so LB/RB keep switching tabs.
    this.focus = 'bar';
  },
  async load() {
    browserState = (await attempt(() => api.getBrowser(), t('browser.unavailable'))) ?? browserState;
    if (currentView() === this) render();
  },
  get isOpen() {
    return !!browserState?.open;
  },
  rows() {
    const s = browserState;
    const known = new Set((s?.bookmarks ?? []).map((b) => b.url));
    const rows = [{ icon: '⌕', label: t('browser.enterAddress'), detail: t('browser.enterAddressDetail'), run: openAddress }];
    for (const r of recent.filter((r) => !known.has(r.url)).slice(0, 4)) {
      rows.push({ recent: r, label: r.title, detail: host(r.url), run: () => open(r.url) });
    }
    for (const b of s?.bookmarks ?? []) {
      rows.push({ bookmark: b, label: b.name, detail: host(b.url), run: () => open(b.url) });
    }
    rows.push({
      icon: '🔍',
      label: t('browser.zoom'),
      detail: t('browser.zoomDetail', { value: s?.zoom ?? 150 }),
      run: async () => {
        const z = await chooseDialog(t('browser.zoomTitle'), ZOOMS.map((v) => ({ label: t('browser.percent', { value: v }), value: v })), s?.zoom ?? 150);
        if (z === undefined) return;
        browserState = (await attempt(() => api.setBrowserZoom(z))) ?? browserState;
        render();
      }
    });
    return rows;
  },
  build(root) {
    if (!browserState) this.load();
    this.slot = null;
    if (this.isOpen) return this.buildPage(root);
    this.list = this.rows();
    this.index = Math.min(this.index, this.list.length - 1);
    this.items = this.list.map((r) => {
      const site = r.bookmark ?? r.recent;
      const badge = site
        ? el('span', { className: 'browser-badge', textContent: initial(site.url) })
        : el('span', { className: 'check-box' }, iconEl(r.icon));
      if (site) badge.style.setProperty('--h', hue(site.url));
      return el('li', { className: 'check-row' }, badge,
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), el('small', { textContent: r.detail })));
    });
    this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
    root.append(el('div', { className: 'files' },
      el('div', { className: 'files-head' },
        el('h2', { textContent: t('browser.title') }),
        el('p', { className: 'meta', textContent: t('browser.startHelp') })),
      this.scroller));
    this.update({ instant: true });
  },
  buildPage(root) {
    this.list = null;
    this.items = null;
    this.buttons = BAR.map((b) => b.id === 'address'
      ? el('div', { className: 'bbar-address', dataset: { bar: b.id } },
        el('span', { className: 'bbar-lock', textContent: '' }),
        el('span', { className: 'bbar-url' }))
      : el('button', { className: 'bbar-btn', type: 'button', tabIndex: -1, ariaLabel: t(b.label), title: t(b.label), dataset: { bar: b.id }, textContent: b.icon }));
    this.slot = el('div', { className: 'browser-slot' });
    root.append(el('div', { className: 'browser-page' },
      el('div', { className: 'bbar' }, this.buttons),
      this.slot));
    this.barIndex = Math.min(this.barIndex, BAR.length - 1);
    this.updateBar();
  },
  /** Texts and states of the bar without a rebuild (the placeholder must stay where it is). */
  updateBar() {
    if (!this.buttons) return;
    const s = browserState ?? {};
    BAR.forEach((b, i) => {
      const node = this.buttons[i];
      node.classList.toggle('disabled', b.enabled ? !b.enabled(s) : false);
      node.classList.toggle('focused', this.focus === 'bar' && i === this.barIndex);
      if (b.id === 'address') {
        node.querySelector('.bbar-url').textContent = s.title ? `${s.title} · ${host(s.url)}` : host(s.url ?? '');
        node.querySelector('.bbar-lock').hidden = !/^https:/i.test(s.url ?? '') || !!s.failed;
      }
    });
    this.slot?.classList.toggle('page-focus', this.focus === 'page');
  },
  update({ instant = false } = {}) {
    if (this.buttons) return this.updateBar();
    if (!this.items?.length) return;
    setFocused(this.items, this.index);
    keepInView(this.scroller, this.items[this.index], { instant });
  },
  captures(action) {
    // In the bar B returns to the page; everywhere else B keeps its global meaning (the tab bar, the page it came from).
    return this.isOpen && this.focus === 'bar' && action === 'b';
  },
  setFocus(focus) {
    this.focus = focus;
    this.updateBar();
    renderHints();
  },
  async handle(action) {
    if (this.isOpen) return this.handlePage(action);
    const row = this.list?.[this.index];
    switch (action) {
      case 'up': return move(this, -1, this.list.length, true);
      case 'down': return move(this, 1, this.list.length, true);
      case 'a':
        sound.select();
        return row?.run();
      case 'y': {
        if (row?.recent) {
          // A history entry: forget it (no question, it is not saved anywhere else).
          recent = recent.filter((r) => r.url !== row.recent.url);
          saveRecent();
          sound.back();
          return render();
        }
        const site = row?.bookmark;
        if (!site) return sound.bump();
        if (!(await confirmDialog(t('browser.removeConfirm', { name: site.name }), t('browser.remove')))) return;
        browserState = (await attempt(() => api.removeBookmark(site.url))) ?? browserState;
        return render();
      }
    }
  },
  handlePage(action) {
    // The page is the host's while it has the controller; these only arrive from the keyboard or when the host is not asked.
    if (this.focus === 'page') {
      if (action === 'up' || action === 'back' || action === 'view') return this.setFocus('bar');
      return;
    }
    const enabled = (i) => !BAR[i].enabled || BAR[i].enabled(browserState ?? {});
    switch (action) {
      case 'left':
      case 'right': {
        const step = action === 'left' ? -1 : 1;
        let i = this.barIndex + step;
        while (i >= 0 && i < BAR.length && !enabled(i)) i += step;
        if (i < 0 || i >= BAR.length) return sound.bump();
        this.barIndex = i;
        sound.move();
        return this.updateBar();
      }
      case 'a':
        if (!enabled(this.barIndex)) return sound.bump();
        sound.select();
        return BAR[this.barIndex].run();
      case 'y':
        return openAddress();
      case 'down':
      case 'b':
      case 'back':
      case 'view':
        return focusPage();
      default:
        return sound.bump();
    }
  },
  pointer(node, index, dispatch) {
    if (this.buttons) {
      const i = this.buttons.indexOf(node.closest('[data-bar]'));
      if (i < 0) return;
      this.focus = 'bar';
      this.barIndex = i;
      this.updateBar();
      return dispatch('a');
    }
    this.index = index;
    this.update();
    dispatch('a');
  },
  hints() {
    if (this.isOpen) {
      return this.focus === 'page'
        ? (state.inputSource === 'keyboard'
          ? [['F6', t('browser.hintBar')], ['F11', t('browser.hintFullscreen')], ['Y', t('browser.hintAddress')]]
          : [['A', t('browser.hintClick')], ['X', t('browser.hintType')], ['Start', t('browser.hintMouseLock')], ['B', t('browser.hintBack')], ['Y', t('browser.hintAddress')], ['LB', t('browser.hintZoomOut')], ['RB', t('browser.hintZoomIn')], ['View', t('browser.hintBar')]])
        : [['←→', t('browser.hintPick')], ['A', t('browser.hintPress')], ['Y', t('browser.hintAddress')], ['B', t('browser.hintToPage')], ['LB', t('browser.hintTabs')]];
    }
    const removable = this.list?.[this.index]?.bookmark || this.list?.[this.index]?.recent;
    return removable ? [['A', t('browser.hintOpen')], ['Y', t('browser.hintRemove')], ['B', t('browser.hintBack')]] : [['A', t('browser.hintOpen')], ['B', t('browser.hintBack')]];
  }
};

function focusPage() {
  browserView.setFocus('page');
  sync();
}

function showTab() {
  const tabs = tabViews();
  const at = tabs.indexOf(browserView);
  if (at < 0) return;
  if (currentView() !== browserView) {
    origin = { tab: state.tab, stack: [...state.stack] };
    state.stack = [];
    state.tab = at;
    sound.tab();
  }
  browserView.focus = 'page';
  render();
}

function leaveToOrigin() {
  if (!origin) return switchTab(0);
  const { tab, stack } = origin;
  origin = null;
  state.stack = stack;
  state.tab = tab;
  sound.back();
  render();
}

function onBrowserState(s) {
  const wasOpen = browserState?.open;
  browserState = s;
  if (s?.open) remember(s.url, s.title);
  if (currentView() !== browserView) return;
  if (!!wasOpen !== !!s?.open) return render();
  browserView.updateBar();
}

api.onBrowser?.(onBrowserState);

// The host tells what the controller did on the page: up to the bar, Y for the address, B on the first page.
api.onBrowserUi?.((what) => {
  switch (what) {
    case 'show':
      return showTab();
    case 'closed':
      browserView.focus = 'bar';
      if (currentView() === browserView) render();
      return;
    case 'bar':
      return browserView.setFocus('bar');
    case 'address':
      browserView.setFocus('bar');
      return openAddress();
    case 'type':
      return typeIntoPage();
    case 'exit':
      return leaveToOrigin();
  }
});

// ---- The page lies over the placeholder: report where it is (and whether it may be seen)

let sent = { visible: false, key: '', focus: false };

/** True while a menu, dialog or other page would be painted below the native page: it must step aside. */
const covered = () => !!state.overlay || !!quick.open || !!document.querySelector('#screensaver');

/** Where the box is in the window, without the transforms of an enter animation (offsets ignore them). */
function layoutRect(node) {
  let left = 0;
  let top = 0;
  for (let n = node; n; n = n.offsetParent) {
    left += n.offsetLeft;
    top += n.offsetTop;
  }
  return { left, top, width: node.offsetWidth, height: node.offsetHeight };
}

function sync() {
  const slot = browserView.slot;
  const visible = currentView() === browserView && browserView.isOpen && !!slot?.isConnected && !covered();
  let key = '';
  let r = null;
  if (visible) {
    r = layoutRect(slot);
    key = [r.left, r.top, r.width, r.height].join(',');
  }
  const focus = visible && browserView.focus === 'page';
  if (visible && (!sent.visible || key !== sent.key)) {
    api.browserLayout(r.left, r.top, r.width, r.height, true).catch(() => {});
    sent = { ...sent, visible: true, key };
  } else if (!visible && sent.visible) {
    api.browserLayout(0, 0, 0, 0, false).catch(() => {});
    sent = { ...sent, visible: false, key: '' };
  }
  if (focus !== sent.focus) {
    api.browserFocus(focus).catch(() => {});
    sent = { ...sent, focus };
  }
}

setInterval(sync, 100);
window.addEventListener('resize', () => sync());
