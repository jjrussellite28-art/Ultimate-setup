import { api } from '../core/api.js';
import { t } from '../core/i18n.js';
import { state } from '../core/state.js';
import { closeOverlay, openOverlay, renderOverlay } from './overlay.js';

/**
 * Update Mega Hub runs without a window of its own: this screen shows what it is doing (the script writes its steps for
 * api.getUpdateStatus). The script closes Mega Hub itself when it is ready; if it stops first, the reason is shown with OK.
 */
export function showUpdateProgress() {
  const menu = {
    title: t('settings.update.title'),
    message: t('settings.update.wait'),
    entries: [],
    refreshMs: 0
  };
  openOverlay(menu);
  let misses = 0;
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
      menu.message = `${status.text}\n${t('settings.update.wait')}`;
    }
    renderOverlay();
  }, 2000);
}
