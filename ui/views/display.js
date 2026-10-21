// Video settings like on a console: resolution, refresh rate, HDR, and a
// "keep these settings?" countdown that switches back if nobody answers.

import { api } from '../core/api.js';
import { $ } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { state } from '../core/state.js';
import { chooseDialog, closeOverlay, openOverlay } from '../components/overlay.js';
import { t } from '../core/i18n.js';

let display = null;

export async function loadDisplay() {
  // A failure is remembered, otherwise the settings would ask again on every render.
  display = (await attempt(() => api.getDisplay(), t('display.unavailable'))) ?? display ?? { failed: true };
  return display;
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
  if (d.hdrSupported) {
    items.push({
      kind: 'toggle',
      label: 'HDR',
      desc: t('display.hdr.desc'),
      value: () => d.hdrOn,
      run: async () => {
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
