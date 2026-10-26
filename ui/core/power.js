// Power actions in one place: Start menu, Guide menu and settings use the same
// entries, texts and confirmations.
//
//   requestPower(id)            asks first (focus on "Abbrechen"), then runs; resolves to true if it ran
//   powerMenu({ sleepTimer })   overlay menu definition for openOverlay()
//   powerEntries(ids?)          [{ id, icon, label, detail, run }] for custom menus (settings, ...)
//   powerRows()                 Guide menu rows
//   chooseSleepTimer()          sleep timer picker; sleepTimerDetail() is its status text
//
// Ids: standby, pc-mode, restart, restart-pc, shutdown.

import { api } from './api.js';
import { attempt, toast } from './feedback.js';
import { t } from './i18n.js';
import { chooseDialog, confirmDialog, openOverlay } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';

const dataLoss = () => t('power.dataLoss');

export const POWER_ACTIONS = [
  {
    id: 'standby',
    icon: '☾',
    get label() { return t('power.standby.label'); },
    get detail() { return t('power.standby.detail'); },
    get confirm() { return { title: t('power.standby.confirmTitle'), label: t('power.standby.label'), message: t('power.standby.confirmMessage') }; },
    run: () => api.power('sleep')
  },
  {
    id: 'pc-mode',
    icon: '▭',
    get label() { return t('power.pcMode.label'); },
    get detail() { return t('power.pcMode.detail'); },
    get confirm() { return { title: t('power.pcMode.confirmTitle'), label: t('power.pcMode.label'), message: t('power.pcMode.confirmMessage') }; },
    get unlock() { return t('power.pcMode.unlock'); },
    run: () => api.enterPcMode()
  },
  {
    id: 'quit',
    icon: '✕',
    get label() { return t('power.quit.label'); },
    get detail() { return t('power.quit.detail'); },
    get confirm() { return { title: t('power.quit.confirmTitle'), label: t('power.quit.label'), message: t('power.quit.confirmMessage') }; },
    get unlock() { return t('power.quit.unlock'); },
    run: () => api.quit()
  },
  {
    id: 'restart',
    icon: '↻',
    get label() { return t('power.restart.label'); },
    get detail() { return t('power.restart.detail'); },
    get confirm() { return { title: t('power.restart.confirmTitle'), label: t('power.restart.label'), message: dataLoss() }; },
    run: () => api.power('restart')
  },
  {
    id: 'restart-pc',
    icon: '⇄',
    get label() { return t('power.restartPc.label'); },
    get detail() { return t('power.restartPc.detail'); },
    get confirm() { return { title: t('power.restartPc.confirmTitle'), label: t('power.restart.label'), message: t('power.restartPc.confirmMessage', { loss: dataLoss() }) }; },
    get unlock() { return t('power.restartPc.unlock'); },
    run: () => api.power('restart-pc')
  },
  {
    id: 'shutdown',
    icon: '⏻',
    get label() { return t('power.shutdown.label'); },
    get detail() { return t('power.shutdown.detail'); },
    get confirm() { return { title: t('power.shutdown.confirmTitle'), label: t('power.shutdown.label'), message: dataLoss() }; },
    run: () => api.power('shutdown')
  }
];

const byId = (id) => POWER_ACTIONS.find((a) => a.id === id);

/** Asks for confirmation (and the couch lock where needed), then runs the action. */
export async function requestPower(id) {
  const action = byId(id);
  if (!action) return false;
  const ok = await confirmDialog(action.confirm.title, action.confirm.label, { danger: true, message: action.confirm.message, icon: action.icon });
  if (!ok) return false;
  if (action.unlock && !(await ensureUnlocked(action.unlock))) return false;
  return (await attempt(async () => (await action.run(), true), action.label)) === true;
}

/** Entries for a custom menu. */
export function powerEntries(ids = POWER_ACTIONS.map((a) => a.id), { onCancel } = {}) {
  return ids.map(byId).filter(Boolean).map((a) => ({
    id: a.id,
    icon: a.icon,
    label: a.label,
    detail: a.detail,
    danger: a.id !== 'standby' && a.id !== 'pc-mode' && a.id !== 'quit',
    run: async () => {
      // Cancelling the question goes back to the menu it came from, not out of everything.
      if (!(await requestPower(a.id))) onCancel?.(a.id);
    }
  }));
}

/** The Start menu: standby first (reversible), "Abbrechen" last. */
export function powerMenu({ sleepTimer = true, index = 0 } = {}) {
  // After cancelling a question the menu comes back with the entry the question was about.
  const entries = powerEntries(['standby', 'pc-mode', 'quit', 'restart', 'restart-pc', 'shutdown'], {
    onCancel: (id) => openOverlay(powerMenu({ sleepTimer, index: Math.max(0, entries.findIndex((e) => e.id === id)) }))
  });
  if (sleepTimer) entries.push({ icon: '⏲', label: t('power.sleepTimer'), detail: sleepTimerDetail(), run: () => chooseSleepTimer() });
  entries.push({ icon: '✕', label: t('power.cancel'), run: () => {} });
  return { title: t('power.title'), entries, index };
}

/** One row for the Guide menu that opens the shared menu, plus the sleep timer. */
export function powerRows() {
  return [
    { kind: 'action', icon: '⏻', label: t('power.title'), detail: t('power.rowDetail'), run: () => openOverlay(powerMenu({ sleepTimer: false })) },
    { kind: 'action', icon: '⏲', label: t('power.sleepTimer'), detail: sleepTimerDetail(), keepOpen: true, run: chooseSleepTimer }
  ];
}

// Sleep timer: "in 60 minutes go to standby", like on a TV.
const sleepTimer = { at: 0, handle: 0 };

export function sleepTimerDetail() {
  return sleepTimer.at ? t('power.timerIn', { n: Math.max(1, Math.round((sleepTimer.at - Date.now()) / 60_000)) }) : t('power.off');
}

export async function chooseSleepTimer() {
  const options = [30, 60, 90, 120].map((m) => ({ label: t('power.inMinutes', { n: m }), value: m }));
  if (sleepTimer.at) options.push({ label: t('power.timerOff'), value: 0 });
  const minutes = await chooseDialog(t('power.timerTitle'), options);
  if (minutes === undefined) return;
  clearTimeout(sleepTimer.handle);
  sleepTimer.at = 0;
  if (!minutes) return toast(t('power.timerOff'));
  sleepTimer.at = Date.now() + minutes * 60_000;
  sleepTimer.handle = setTimeout(() => {
    sleepTimer.at = 0;
    attempt(() => api.power('sleep'));
  }, minutes * 60_000);
  toast(t('power.timerSet', { n: minutes }));
}
