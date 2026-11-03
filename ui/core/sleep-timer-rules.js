// Sleep timer rules without DOM or host calls (tested in scripts/ui-logic-check.mjs): what the timer does when it runs out, which
// texts name that, and which couch lock setting it needs.

/**
 * What the timer may do, as ids of the power menu's entries (power.js runs that entry's own host call, nothing else). The first is
 * the default for a fresh install: shut down.
 */
export const TIMER_ACTIONS = ['shutdown', 'standby'];

/** The minutes the picker offers (on the TV and on the phone; the host's RemoteCalls.SleepTimerMinutes is the same list, contract test). */
export const TIMER_MINUTES = [30, 60, 90, 120];
export const DEFAULT_TIMER_ACTION = 'shutdown';

/** Where the last used action is kept in the UI's own storage. */
export const TIMER_ACTION_KEY = 'couchhud-sleep-timer-action';

/** How long before the end the timer warns (with "Abbrechen" and "Jetzt"). */
export const TIMER_WARN_MS = 60_000;

/** A stored or passed value as a timer action: anything unknown (tampered, old, empty) is the default, never the raw value. */
export function timerAction(value) {
  return TIMER_ACTIONS.includes(value) ? value : DEFAULT_TIMER_ACTION;
}

// i18n-used: power.timerIn.shutdown power.timerIn.standby power.timerSet.shutdown power.timerSet.standby
// i18n-used: power.timerWarn.shutdown power.timerWarn.standby
/** The i18n keys that name the action: status ("… in 42 Min."), toast when set, warning before the end. */
export function timerTexts(action) {
  const a = timerAction(action);
  return { status: `power.timerIn.${a}`, set: `power.timerSet.${a}`, warn: `power.timerWarn.${a}` };
}

/**
 * The couch lock setting a timer needs: exactly the one of the power entry it ends in (unlockOf(id) gives that entry's unlock text,
 * or nothing when the entry needs none). Never weaker than the direct action, because it is the same.
 */
export function timerUnlock(action, unlockOf) {
  return unlockOf(timerAction(action)) || '';
}

/**
 * A timer the phone asks for (the host checked the power gates and the owner's setting before it passed it on): minutes from
 * TIMER_MINUTES or 0 (off), an action of TIMER_ACTIONS. Anything else: null, nothing happens.
 */
export function phoneTimer(event) {
  if (event?.type !== 'sleepTimer') return null;
  if (event.minutes === 0) return { minutes: 0, action: '' };
  if (!TIMER_MINUTES.includes(event.minutes) || !TIMER_ACTIONS.includes(event.action)) return null;
  return { minutes: event.minutes, action: event.action };
}

/** When the warning comes: TIMER_WARN_MS before the end, at once for timers shorter than that. */
export function warnDelay(ms) {
  return Math.max(0, ms - TIMER_WARN_MS);
}
