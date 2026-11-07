import { api } from '../core/api.js';
import { t } from '../core/i18n.js';
import { state } from '../core/state.js';
import { closeOverlay, openOverlay, renderOverlay } from './overlay.js';

/**
 * Update Mega Hub runs without a window of its own: this screen shows what it is doing (the script writes its steps for
 * api.getUpdateStatus). The script closes Mega Hub itself when it is ready; if it stops first, the reason is shown with OK.
 */
/** Bar from the script's "[n/5]" step; the long build step (about 10 minutes) fills with the time that has passed. */
function progressLine(text, startedAt, stepSince) {
  const step = Number((/^\[(\d)\/5\]/.exec(text) || [])[1] || 0);
  const building = /building/i.test(text);
  let pct = [0, 5, 15, 25, building ? 30 : 80, 92][step] ?? 0;
  if (building) pct += Math.min(60, ((Date.now() - stepSince) / 600000) * 60);
  if (/^Done/.test(text)) pct = 100;
  pct = Math.round(Math.min(99, pct));
  const filled = Math.round(pct / 5);
  const secs = Math.floor((Date.now() - startedAt) / 1000);
  const time = `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')}`;
  return `${'█'.repeat(filled)}${'░'.repeat(20 - filled)}  ${pct}%  ·  ${time}${building ? ' / about 10:00' : ''}`;
}

export function showUpdateProgress() {
  const menu = {
    title: t('settings.update.title'),
    message: t('settings.update.wait'),
    entries: [],
    refreshMs: 0
  };
  openOverlay(menu);
  let misses = 0;
  const startedAt = Date.now();
  let lastText = '';
  let stepSince = startedAt;
  const timer = setInterval(async () => {
    // Closed with B (or replaced): nothing left to update on screen.
    if (state.overlay !== menu) return clearInterval(timer);
    let status;
    try {
      status = await api.getUpdateStatus();
    } catch {
      return;
    }
    const stopped = !status.running || !!status.error;
    // The first seconds: the script may not have written anything yet.
    if (stopped && !status.text && ++misses < 3) return;
    if (stopped) {
      clearInterval(timer);
      menu.title = t('settings.update.stopped');
      menu.message = [status.text, status.error].filter(Boolean).join('\n');
      menu.entries = [{ label: 'OK', icon: '✓', run: () => closeOverlay({ cancelled: false }) }];
      menu.index = 0;
    } else if (status.text) {
      if (status.text !== lastText) {
        lastText = status.text;
        stepSince = Date.now();
      }
      menu.message = `${progressLine(status.text, startedAt, stepSince)}\n${status.text}\n${t('settings.update.wait')}`;
    }
    renderOverlay();
  }, 2000);
}
