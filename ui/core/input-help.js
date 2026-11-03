// What the code prompt says when no controller can type the code: keyboard and mouse work too, and where Bluetooth stands.
// Pure (keys only, no DOM), checked by scripts/ui-logic-check.mjs.

/** The on-screen code buttons show when no controller is connected; an unknown pad list (null) counts as none, so the fallback is never lost. */
export const shouldShowCodeKeys = (padCount) => !(Number.isInteger(padCount) && padCount > 0);

/** The answer of `promise`, or `fallback` when it does not come within `ms` or fails (the code buttons must not wait for a silent controller list). */
export function withTimeout(promise, ms, fallback = null) {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(fallback), ms);
    Promise.resolve(promise).then((value) => { clearTimeout(timer); resolve(value); }, () => { clearTimeout(timer); resolve(fallback); });
  });
}

/** The code buttons in the order of the on-screen row (same set as the controller and the keyboard). */
export const CODE_KEYS = ['a', 'b', 'x', 'y', 'lb', 'rb', 'up', 'down', 'left', 'right'];

/**
 * pads: number of connected controllers (null: not known), bt: the answer of getBluetooth() (null: not known).
 * Returns { noPad, bluetooth } where bluetooth is a text key or null. Nothing is said while the state is not known: a wrong
 * "no controller" would only confuse.
 */
export function padHelp(pads, bt) {
  if (pads !== 0) return { noPad: false, bluetooth: null };
  if (!bt || !bt.available) return { noPad: true, bluetooth: null };
  // i18n-used: passcode.bt.offAuto passcode.bt.off passcode.bt.onPaired
  if (!bt.on) return { noPad: true, bluetooth: bt.autoOn && bt.controllersPaired > 0 ? 'passcode.bt.offAuto' : 'passcode.bt.off' };
  return { noPad: true, bluetooth: bt.controllersPaired > 0 ? 'passcode.bt.onPaired' : null };
}

/**
 * Switching Bluetooth off cuts a Bluetooth controller off: ask first. Returns the text key of the warning, or null when nothing
 * needs to be said (no Bluetooth controller connected, or switching on).
 */
/**
 * A page where the code is typed (code prompt): every button is a code button, so the mouse wheel and the side buttons type nothing
 * and held arrows do not repeat (one press, one code step).
 */
export const isCodeEntry = (view) => view?.codeEntry === true;

/**
 * Pages that no dialog may cover: the code prompt (a dialog over it would take the next button press as an answer) and the
 * parental time-up page. The Bluetooth countdown waits behind them and shows up once they are closed.
 */
export const holdsDialogs = (view) => isCodeEntry(view) || view?.id === 'parental-timeup';

/**
 * Whether a click on a tab of the bar may switch tabs now. Not while a page holds the screen: the code prompt (the controller's LB/RB belong to it, and a tab
 * switch would close it unanswered), the parental time-up page (clearStack would take it off the screen for good, the game under it uncovered) or a required
 * profile picker (home, tab switches and the guide menu stay out). The click does nothing then, like LB/RB do not.
 */
export const tabClickAllowed = (view, state) => !state?.pickerHold && !holdsDialogs(view);

/** Whether something holds the screen so that no navigation (a tab, home, the guide menu, a chip, a call of the host or the phone) may take it away: the required profile picker or the time-up page. */
export const screenHeld = (state) => !!state?.pickerHold || !!state?.stack?.some((p) => p.id === 'parental-timeup');

/**
 * What clearStack keeps and closes. A sticky page (the first-start notice, a required profile picker, the time-up page) is never closed by it, whoever asks:
 * a tab switch, home, a chip, the guide menu, the phone. Everything else is closed, newest first (an open code prompt then resolves unanswered).
 */
export function splitStack(stack) {
  return { keep: stack.filter((p) => p.sticky), closing: stack.filter((p) => !p.sticky).reverse() };
}

export function switchOffWarning(bt, turningOn) {
  if (turningOn || !bt?.controllerConnected) return null;
  // i18n-used: bluetooth.offWarnAuto bluetooth.offWarn
  return bt.autoOn ? 'bluetooth.offWarnAuto' : 'bluetooth.offWarn';
}
