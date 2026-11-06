// Power actions in one place: Start menu, Guide menu and settings use the same
// entries, texts and confirmations.
//
//   requestPower(id)            asks first (focus on "Abbrechen"), then runs; resolves to true if it ran
//   powerMenu({ sleepTimer })   overlay menu definition for openOverlay(): three tiles, a "Mehr" row and the timer row, see power-rules.js
//   powerEntries(ids?)          [{ id, icon, label, detail, run }] for custom menus (settings, ...)
//   powerRows()                 Guide menu rows
//   chooseSleepTimer()          sleep timer picker (minutes, then shut down or standby); sleepTimerDetail() is its status text
//   listenToPhoneSleepTimer()   the phone may start or stop the timer (the host checked the gates); the TV reports the timer back
//
// Ids: standby, pc-mode, restart-app, restart, restart-pc, shutdown, quit. The mode decides which are offered (see forMode).

import { api } from './api.js';
import { state } from './state.js';
import { attempt, toast } from './feedback.js';
import { t } from './i18n.js';
import { chooseDialog, closeOverlay, closeTagged, confirmDialog, openOverlay } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';
import { MORE, POWER_START, SLEEP_TIMER, forMode as forModeRule, isDangerous, isSecondary, powerEntryIds, powerLayout } from './power-rules.js';
import { TIMER_ACTIONS, TIMER_ACTION_KEY, TIMER_MINUTES, TIMER_WARN_MS, phoneTimer, timerAction, timerTexts, timerUnlock, warnDelay } from './sleep-timer-rules.js';

/** How the Start menu lays out its groups: 'list' (rows with headings) or 'tiles' (one row of large tiles per group). */
export const POWER_MENU_LAYOUT = 'tiles';

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
    // Mega Hub itself starts again (plugin changes take effect, the couch lock and the profile come back as they were): behind the same
    // couch lock as quitting; the host refuses while a game runs.
    id: 'restart-app',
    icon: '⟲',
    get label() { return t('power.restartApp.label'); },
    get detail() { return t('power.restartApp.detail'); },
    get confirm() { return { title: t('power.restartApp.confirmTitle'), label: t('power.restartApp.label'), message: t('power.restartApp.confirmMessage') }; },
    get unlock() { return t('power.restartApp.label'); },
    run: () => api.restartCouchHud()
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
    // Closes Mega Hub completely (as the shell the host starts the desktop first); the host asks for the couch lock too.
    id: 'quit',
    icon: '✕',
    get label() { return t('power.quit.label'); },
    get detail() { return t('power.quit.detail'); },
    get confirm() { return { title: t('power.quit.confirmTitle'), label: t('power.quit.label'), message: t('power.quit.confirmMessage') }; },
    get unlock() { return t('power.quit.label'); },
    run: () => api.quit()
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

/** The action an id stands for in the current mode (rules in power-rules.js). */
export function forMode(id, isShell = !!state.info?.isShell) {
  return forModeRule(id, isShell);
}

/** Asks for confirmation (and the couch lock where needed), then runs the action. */
export async function requestPower(id) {
  const action = byId(forMode(id));
  if (!action) return false;
  const ok = await confirmDialog(action.confirm.title, action.confirm.label, { danger: true, message: action.confirm.message, icon: action.icon });
  if (!ok) return false;
  if (action.unlock && !(await ensureUnlocked(action.unlock))) return false;
  return (await attempt(async () => (await action.run(), true), action.label)) === true;
}

/** Entries for a custom menu. */
export function powerEntries(ids = POWER_ACTIONS.map((a) => a.id), { onCancel } = {}) {
  const shown = [...new Set(ids.map((id) => forMode(id)).filter(Boolean))];
  return shown.map(byId).filter(Boolean).map((a) => ({
    id: a.id,
    icon: a.icon,
    label: a.label,
    detail: a.detail,
    danger: isDangerous(a.id),
    run: async () => {
      // Cancelling the question goes back to the menu it came from, not out of everything.
      if (!(await requestPower(a.id))) onCancel?.(a.id);
    }
  }));
}

/**
 * The Start menu: three large tiles (standby, shut down, restart), a collapsed "Mehr" row with the rare entries (PC mode, restart into
 * it, restart Mega Hub, quit) and one row for the sleep timer (power-rules.js). Focus starts on standby. A expands "Mehr" in place and
 * moves to its first entry; B folds it back before the menu closes; X opens the timer picker from anywhere. After a cancelled question
 * the menu comes back on the entry the question was about (focus), expanded if that entry lives behind "Mehr". No "Abbrechen" entry:
 * B, a click next to the menu or the B hint close it. Whether "Mehr" is open is remembered for the open menu only.
 */
export function powerMenu({ sleepTimer = true, focus = POWER_START, expanded = false, layout = POWER_MENU_LAYOUT } = {}) {
  const parts = powerLayout(!!state.info?.isShell, { sleepTimer });
  const reopen = (id, open = isSecondary(parts, id)) => openOverlay(powerMenu({ sleepTimer, layout, expanded: open, focus: id }));
  const actions = new Map(powerEntries([...parts.primary, ...parts.more], { onCancel: (id) => reopen(id) }).map((e) => [e.id, e]));
  const timer = () => pickSleepTimer(() => reopen(SLEEP_TIMER, menu.expanded));
  // The menu object stays the same when "Mehr" folds in or out (the refresh timer and the overlay hold it); only its entries change.
  const menu = { title: t('power.title'), layout, details: layout === 'tiles' ? 'below' : 'focused' };
  const fill = (open, focusId, reveal) => {
    const ids = powerEntryIds(parts, open);
    const entries = ids.map((id) => {
      if (id === SLEEP_TIMER) return Object.assign(sleepTimerEntry(timer), { wide: true, row: id });
      if (id === MORE) return moreEntry(open, () => fill(!open, open ? MORE : parts.more[0], !open));
      return Object.assign(actions.get(id), isSecondary(parts, id) ? { wide: true, row: id, reveal } : { row: 'primary' });
    });
    Object.assign(menu, {
      entries,
      expanded: open,
      // A running timer counts down while the menu is open (the option above shadows the timer's state, hence timerRunning()).
      refreshMs: sleepTimer && timerRunning() ? 15_000 : 0,
      index: Math.max(0, ids.indexOf(focusId)),
      hints: () => [['A', t('nav.select')], ...(sleepTimer ? [['X', t('power.timerHint')]] : []), ['B', t('nav.back')]],
      onBack: () => {
        if (!menu.expanded) return false;
        fill(false, MORE, false);
        return true;
      },
      keys: sleepTimer ? { x: timer } : {}
    });
  };
  fill(expanded, focus, false);
  return menu;
}

/** The "Mehr" row: folds the rare entries in and out (A on it never closes the menu). */
function moreEntry(open, toggle) {
  return { id: MORE, icon: '⋯', label: t('power.more'), detail: t('power.moreDetail'), value: open ? '▴' : '▾', wide: true, row: MORE, expanded: open, keepOpen: true, run: toggle };
}

/**
 * The timer row: always says its state on the right ("Aus", or the remaining time and what happens). Cancelling the picker comes back
 * to the menu on the timer.
 */
function sleepTimerEntry(pick) {
  return {
    id: SLEEP_TIMER,
    icon: '⏲',
    label: t('power.sleepTimer'),
    detail: '',
    get value() { return sleepTimerDetail(); },
    run: pick
  };
}

/** One row for the Guide menu that opens the shared menu, plus the sleep timer. */
export function powerRows() {
  return [
    // i18n-used: power.rowDetail power.rowDetailApp
    { kind: 'action', icon: '⏻', label: t('power.title'), detail: t(state.info?.isShell ? 'power.rowDetail' : 'power.rowDetailApp'), run: () => openOverlay(powerMenu({ sleepTimer: false })) },
    { kind: 'action', icon: '⏲', label: t('power.sleepTimer'), detail: sleepTimerDetail(), keepOpen: true, run: chooseSleepTimer }
  ];
}

// Sleep timer: "in 60 minutes shut down" (or standby), like on a TV. The action is a power entry's id (sleep-timer-rules.js); when
// the timer runs out, that entry's own host call runs, without asking again: it was authorised when the timer was set.
// gen tells an old warning (its timer was cancelled, replaced or fired) from the current one.
const sleepTimer = { at: 0, action: '', handle: 0, warnHandle: 0, gen: 0, token: '' };
const WARN_TAG = 'sleep-timer-warning';

function timerRunning() {
  return sleepTimer.at > 0;
}

export function sleepTimerDetail() {
  return sleepTimer.at ? t(timerTexts(sleepTimer.action).status, { n: Math.max(1, Math.round((sleepTimer.at - Date.now()) / 60_000)) }) : t('power.off');
}

export function chooseSleepTimer() {
  return pickSleepTimer();
}

/** The last used action (a fresh install, blocked storage or an unknown stored value: shut down). */
function lastAction() {
  try { return timerAction(localStorage.getItem(TIMER_ACTION_KEY)); } catch { return timerAction(''); }
}

function rememberAction(action) {
  try { localStorage.setItem(TIMER_ACTION_KEY, action); } catch { /* storage blocked: the default comes back next time */ }
}

/**
 * The picker: the minutes, then what happens (preselected: the last used action). B in the second step goes back to the minutes,
 * B in the minutes runs onCancel (the power menu comes back). "Timer aus" is one step.
 */
async function pickSleepTimer(onCancel) {
  const options = TIMER_MINUTES.map((m) => ({ label: t('power.inMinutes', { n: m }), value: m }));
  if (sleepTimer.at) options.push({ label: t('power.timerOff'), value: 0 });
  let minutes;
  let action;
  do {
    minutes = await chooseDialog(t('power.timerTitle'), options, minutes);
    if (minutes === undefined) return onCancel?.();
    if (!minutes) {
      stopSleepTimer();
      return toast(t('power.timerOff'));
    }
    action = await chooseDialog(t('power.timerActionTitle', { n: minutes }), TIMER_ACTIONS.map((id) => {
      const entry = byId(id);
      return { label: entry.label, icon: entry.icon, value: id };
    }), lastAction());
  } while (action === undefined);
  action = timerAction(action);
  // The same couch lock as the entry itself: setting a timer is never a way around it.
  const unlock = timerUnlock(action, (id) => byId(id)?.unlock);
  if (unlock && !(await ensureUnlocked(unlock))) return;
  rememberAction(action);
  startSleepTimer(minutes * 60_000, action);
  toast(t(timerTexts(action).set, { n: minutes }));
}

// The host shows its notice over a game only for the timer armed here, close to its end (Hud.SleepTimerWarning).
function stopSleepTimer(tellHost = true) {
  clearTimeout(sleepTimer.handle);
  clearTimeout(sleepTimer.warnHandle);
  const wasRunning = !!sleepTimer.at;
  sleepTimer.at = 0;
  sleepTimer.action = '';
  sleepTimer.token = '';
  sleepTimer.gen++;
  closeTagged(WARN_TAG);
  if (tellHost && wasRunning) api.sleepTimerArm?.(null, 0).catch(() => {});
  if (wasRunning) reportSleepTimer();
}

/** The phone page shows the TV's timer: the host keeps only end and action. */
function reportSleepTimer() {
  api.sleepTimerState?.({ at: sleepTimer.at, action: sleepTimer.action, token: sleepTimer.token })?.catch?.(() => {});
}

let phoneTimerListening = false;
// True while Mega Hub is in PC mode (the host says so): no sleep timer starts or fires then, whoever set it.
let pcModeActive = false;

/**
 * The phone's sleep timer: the host checked the same gates as for the power action it ends in (couch lock, waiting picker, areas, the
 * owner's setting, PC mode) and told the TV; here it starts like one set on the couch (with the warning a minute before), or stops.
 */
export function listenToPhoneSleepTimer() {
  if (phoneTimerListening) return;
  phoneTimerListening = true;
  api.onRemote?.((event) => {
    // PC mode: the desktop is in use, no timer may shut it down. The host sends this when PC mode begins or ends.
    if (event?.type === 'pcMode') {
      pcModeActive = event.on === true;
      if (pcModeActive) stopSleepTimer();
      return;
    }
    const timer = phoneTimer(event);
    if (!timer) return;
    if (!timer.minutes) return stopSleepTimer();
    // The host's token for this very timer comes back with the report: that is how it knows the timer is the phone's.
    startSleepTimer(timer.minutes * 60_000, timer.action, typeof event.token === 'string' ? event.token : '');
  });
}

function startSleepTimer(ms, action, token = '') {
  // The host hears the cancel of a replaced timer too (its arm gap is cleared by it).
  stopSleepTimer();
  if (pcModeActive) return;
  sleepTimer.at = Date.now() + ms;
  sleepTimer.action = action;
  sleepTimer.token = token;
  api.sleepTimerArm?.(action, Math.ceil(ms / 1000)).catch(() => {});
  sleepTimer.warnHandle = setTimeout(warnSleepTimer, warnDelay(ms));
  sleepTimer.handle = setTimeout(fireSleepTimer, ms);
  reportSleepTimer();
}

/** Runs the power entry the timer was set for (its own host call only; the action was checked when it was stored). */
function fireSleepTimer() {
  const action = byId(timerAction(sleepTimer.action));
  stopSleepTimer();
  // Checked again at the end: a timer set before PC mode never shuts the desktop down (the host also ends it when PC mode begins).
  if (pcModeActive) return;
  attempt(() => action.run(), action.label);
}

/**
 * A minute before the end: a question the controller can reach, "Abbrechen" (the timer ends) or "Jetzt". Without an answer the
 * timer fires at 0 and closes the question. A menu that is open gives way (cancelled, as with B).
 */
async function warnSleepTimer() {
  const gen = sleepTimer.gen;
  const action = timerAction(sleepTimer.action);
  if (state.overlay) closeOverlay();
  await new Promise((resolve) => setTimeout(resolve, 0));
  if (gen !== sleepTimer.gen) return;
  const entry = byId(action);
  const seconds = Math.round(TIMER_WARN_MS / 1000);
  const asked = confirmDialog(t(timerTexts(action).warn, { n: seconds }), t('power.timerNow'), {
    danger: action === 'shutdown', icon: entry.icon, tag: WARN_TAG, focusCancel: true
  });
  // A game in front hides the question: the host shows a notice over it and brings the HUD (with the question) to the front.
  api.sleepTimerWarning?.(action, seconds).catch(() => {});
  const now = await asked;
  // The timer was cancelled, replaced or fired meanwhile: this answer is about nothing any more.
  if (gen !== sleepTimer.gen) return;
  if (now) return fireSleepTimer();
  stopSleepTimer();
  toast(t('power.timerOff'));
}

/**
 * A notice that a plugin change takes effect at the next start, with a way to do it now: a question ("Jetzt neu starten" / "Später"),
 * because a toast cannot be reached with the controller. The same couch lock and the same host call as the power menu's entry.
 */
export async function offerRestart(text) {
  const ok = await confirmDialog(text, t('power.restartApp.now'), { message: t('power.restartApp.offer'), icon: '⟲', cancelLabel: t('power.restartApp.later') });
  if (!ok) return false;
  const action = byId('restart-app');
  if (!(await ensureUnlocked(action.unlock))) return false;
  return (await attempt(async () => (await action.run(), true), action.label)) === true;
}
