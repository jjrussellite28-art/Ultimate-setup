// Video settings like on a console: resolution, refresh rate, HDR, and a
// "keep these settings?" countdown that switches back if nobody answers.

import { api } from '../core/api.js';
import { $ } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { state } from '../core/state.js';
import { chooseDialog, closeOverlay, confirmDialog, openOverlay } from '../components/overlay.js';
import { t } from '../core/i18n.js';
import { ensureUnlocked } from '../components/passcode.js';

let display = null;
/** The connected screens from the left: { number, width, height, primary, chosen } (empty until the host answered). */
let screens = [];

async function loadScreens() {
  screens = (await attempt(() => api.getScreens?.(), undefined, { quietLocked: true })) ?? screens;
  return screens;
}

export async function loadDisplay() {
  // A failure is remembered, otherwise the settings would ask again on every render.
  display = (await attempt(() => api.getDisplay(), t('display.unavailable'))) ?? display ?? { failed: true };
  await loadScreens();
  return display;
}

const screenName = (s) => `${t('display.screen.number', { n: s.number })} · ${s.width} × ${s.height}${s.primary ? ` · ${t('display.screen.main')}` : ''}`;

/**
 * Which screen Mega Hub covers (also offered next to the Couch or PC choice at start). Automatic, or one of the connected screens;
 * the change asks for the unlock first and moves the window right away. Resolves true when it changed.
 */
export async function chooseScreen() {
  await loadScreens();
  if (screens.length < 2) {
    toast(t('display.screen.single'), 4000);
    return false;
  }
  const current = screens.find((s) => s.chosen)?.number ?? 0;
  const pick = await chooseDialog(t('display.screen.label'), [
    { label: t('display.screen.auto'), detail: current === 0 ? t('display.current') : t('display.screen.autoDetail'), value: 0 },
    ...screens.map((s) => ({ label: screenName(s), detail: s.number === current ? t('display.current') : undefined, value: s.number }))
  ], current);
  if (pick === undefined || pick === current) return false;
  if (!(await ensureUnlocked(t('display.guard')))) return false;
  const next = await attempt(() => api.setScreen(pick), t('display.screen.failed'));
  if (!next) return false;
  screens = next;
  toast(t('display.screen.moved'));
  return true;
}

/** PC screen only, the same picture on both (mirror), use both (extend) or the second screen only: Windows' own switch. */
async function chooseLayout() {
  const pick = await chooseDialog(t('display.layout.label'), [
    { label: t('display.layout.internal'), detail: t('display.layout.internalDetail'), value: 'internal' },
    { label: t('display.layout.clone'), detail: t('display.layout.cloneDetail'), value: 'clone' },
    { label: t('display.layout.extend'), detail: t('display.layout.extendDetail'), value: 'extend' },
    { label: t('display.layout.external'), detail: t('display.layout.externalDetail'), value: 'external' }
  ]);
  if (!pick) return false;
  if (!(await confirmDialog(t('display.layout.confirm'), t('display.layout.apply')))) return false;
  if (!(await ensureUnlocked(t('display.guard')))) return false;
  const done = await attempt(async () => { await api.setScreenLayout(pick); return true; }, t('display.layout.failed'));
  if (!done) return false;
  // Windows needs a moment to switch; ask again afterwards so the list shows what is connected now.
  await new Promise((resolve) => setTimeout(resolve, 2500));
  await loadDisplay();
  return true;
}

const label = (m) => (m ? `${m.width} × ${m.height}` : '–');

/** Resolves true if the user keeps the new mode within 15 seconds. */
function keepDialog() {
  return new Promise((resolve) => {
    let seconds = 15;
    const title = () => t('display.keepTitle', { seconds });
    const done = (keep) => {
      clearInterval(timer);
      resolve(keep);
    };
    openOverlay({
      title: title(),
      index: 1,
      onCancel: () => done(false),
      entries: [
        { label: t('display.keep'), run: () => done(true) },
        { label: t('display.revert'), run: () => done(false) }
      ]
    });
    const timer = setInterval(() => {
      seconds--;
      if (seconds <= 0) {
        clearInterval(timer);
        if (state.overlay) closeOverlay();
        resolve(false);
        return;
      }
      const node = $('#overlay .dialog-title');
      if (node) node.textContent = title();
    }, 1000);
  });
}

async function switchTo(mode) {
  // A locked couch refuses the change; ask for the code (or the Windows sign-in) first instead of failing with "unlock first".
  if (!(await ensureUnlocked(t('display.guard')))) return;
  const before = display.current;
  const next = await attempt(() => api.setDisplayMode(mode.width, mode.height, mode.hz), t('display.switchFailed'));
  if (!next) return;
  display = next;
  if (await keepDialog()) return toast(t('display.saved'));
  display = (await attempt(() => api.setDisplayMode(before.width, before.height, before.hz), t('display.revertFailed'))) ?? display;
  toast(t('display.restored'));
}

/** Rows for the settings category; `refresh` reloads the settings view afterwards. */
export function displayItems(refresh) {
  if (!display) {
    loadDisplay().then(refresh);
    return [{ kind: 'info', label: t('display.label'), value: t('display.loading') }];
  }
  if (display.failed) return [{ kind: 'info', label: t('display.label'), value: t('display.failed') }];
  const d = display;
  // Biggest first, like on a console.
  const resolutions = [...new Map(d.modes.map((m) => [`${m.width}x${m.height}`, m])).values()].sort((a, b) => b.width * b.height - a.width * a.height || b.width - a.width);
  const rates = d.modes.filter((m) => m.width === d.current.width && m.height === d.current.height).map((m) => m.hz);
  const items = [
    {
      kind: 'choice',
      label: t('display.resolution.label'),
      desc: t('display.resolution.desc'),
      value: () => label(d.current),
      run: async () => {
        const chosen = await chooseDialog(t('display.resolution.label'), resolutions.map((m) => ({ label: label(m), detail: m.width === d.current.width && m.height === d.current.height ? t('display.current') : undefined, value: m })), resolutions.find((m) => m.width === d.current.width && m.height === d.current.height));
        if (!chosen || (chosen.width === d.current.width && chosen.height === d.current.height)) return;
        // Keep the refresh rate if the new resolution has it, otherwise its fastest one.
        const best = d.modes.filter((m) => m.width === chosen.width && m.height === chosen.height).sort((a, b) => Math.abs(a.hz - d.current.hz) - Math.abs(b.hz - d.current.hz))[0];
        await switchTo(best);
        refresh();
      }
    },
    {
      kind: 'choice',
      label: t('display.rate.label'),
      desc: t('display.rate.desc'),
      value: () => t('display.hz', { hz: d.current.hz }),
      run: async () => {
        const hz = await chooseDialog(t('display.rate.label'), [...new Set(rates)].sort((a, b) => b - a).map((r) => ({ label: t('display.hz', { hz: r }), value: r })), d.current.hz);
        if (!hz || hz === d.current.hz) return;
        await switchTo({ width: d.current.width, height: d.current.height, hz });
        refresh();
      }
    }
  ];
  // Which screen Mega Hub is on (two or more screens) and how the screens work together. The layout row stays also with one screen
  // seen: with a mirrored picture Windows reports only one, and this is the way back to "use both".
  if (screens.length > 1) {
    const chosen = screens.find((s) => s.chosen);
    items.push({
      kind: 'choice',
      label: t('display.screen.label'),
      desc: t('display.screen.desc'),
      value: () => (chosen ? screenName(chosen) : t('display.screen.auto')),
      run: async () => {
        if (await chooseScreen()) refresh();
      }
    });
  }
  if (api.setScreenLayout) {
    items.push({
      kind: 'choice',
      label: t('display.layout.label'),
      desc: t('display.layout.desc'),
      value: () => t('display.layout.change'),
      run: async () => {
        if (await chooseLayout()) refresh();
      }
    });
  }
  if (d.hdrSupported) {
    items.push({
      kind: 'toggle',
      label: 'HDR',
      desc: t('display.hdr.desc'),
      value: () => d.hdrOn,
      run: async () => {
        if (!(await ensureUnlocked(t('display.guard')))) return;
        const next = await attempt(() => api.setHdr(!d.hdrOn), t('display.hdr.failed'));
        if (next) display = next;
        refresh();
      }
    });
  } else {
    items.push({ kind: 'info', label: 'HDR', value: t('display.hdr.unsupported') });
  }
  return items;
}
