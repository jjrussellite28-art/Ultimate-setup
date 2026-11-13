// The Unlock button at the top right of the bar (before the bell). Locked: it asks for the Windows sign-in and the Mega Hub code, and
// then everything stays open (no timer; it locks again when the lid was closed for about 3 minutes, or on a shutdown or restart).
// Unlocked: the button is hidden (no Lock button). Hidden while there is no couch lock. LB/RB reach it like the bell ("unlock" in bar-chips.js).

import { $, el } from './dom.js';
import { api } from './api.js';
import { t } from './i18n.js';
import { toast } from './feedback.js';
import { showChipFocus } from './nav.js';

let node = null;
let timer = null;

function paint(security) {
  node ??= (() => {
    const n = el('span', { id: 'unlock-chip' });
    n.onclick = () => toggleLock();
    $('#status')?.prepend(n);
    return n;
  })();
  const lockOn = !!security && security.couchLock && security.couchLock !== 'off';
  const open = !!security?.unlocked;
  // Once unlocked there is no "Lock" button: it stays open until the lid was closed for a while, a restart or a shutdown.
  node.hidden = !lockOn || open;
  if (!lockOn || open) return;
  node.classList.toggle('open', open);
  node.textContent = open ? `🔓︎ ${t('unlockChip.lock')}` : `🔒︎ ${t('unlockChip.unlock')}`;
  node.title = open ? t('unlockChip.lockTitle') : t('unlockChip.unlockTitle');
  showChipFocus();
}

/** Reads the lock state and paints the button. Cheap; also called after every unlock. */
export async function refreshUnlockChip() {
  try {
    paint(await api.getSecurity());
  } catch {
    // The state is unknown: the button stays as it was.
  }
}

/** The button is on the bar now (for LB/RB). */
export const unlockChipShown = () => !!node && !node.hidden;

/** Locked: the unlock (Windows sign-in and code). Unlocked: lock again. */
export async function toggleLock() {
  const security = await api.getSecurity().catch(() => null);
  if (!security) return;
  if (security.unlocked) {
    await api.lockNow?.();
    toast(t('unlockChip.locked'));
  } else {
    const { ensureUnlocked } = await import('../components/passcode.js');
    if (await ensureUnlocked(t('unlockChip.unlock'))) toast(t('unlockChip.unlocked'), 4000);
  }
  await refreshUnlockChip();
}

/** Starts the button: painted now and kept in step with the host (the lock can end on its own, after sleep). */
export function startUnlockChip() {
  refreshUnlockChip();
  clearInterval(timer);
  timer = setInterval(refreshUnlockChip, 5000);
}
