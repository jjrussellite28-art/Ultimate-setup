// What happens to the page stack when something wants to navigate (a tab, home, a chip, the browser taking over the screen, the host or the phone), as pure
// rules over a state object { stack, tab, pickerHold, ... }: nav.js, views/browser.js, components/parental.js, core/bar-chips.js and app.js call these, and
// scripts/ui-logic-check.mjs runs them on a real stack. A page that holds the screen (the parental time-up page, a required profile picker: sticky) is never
// taken off the stack by any of them; a code prompt is closed unanswered (nothing is granted).
import { screenHeld, splitStack, tabClickAllowed } from './input-help.js';

/** Closes every page that does not hold the screen, newest first (each one is told: an open code prompt resolves unanswered); sticky pages stay. */
export function clearStackOf(state) {
  const { keep, closing } = splitStack(state.stack);
  state.stack = keep;
  for (const page of closing) page.onClose?.();
}

/**
 * The browser takes over the screen (the host says "show"): the pages of the tab it leaves are put aside, but whatever holds the screen stays on the stack
 * (it is shown over the browser tab). Returns what was put aside, for restoreStack.
 */
export function takeOverStack(state) {
  const { keep, closing } = splitStack(state.stack);
  state.stack = keep;
  return closing.reverse();
}

/**
 * The browser gives the screen back: the pages put aside come back under whatever holds the screen now. Only pages that may be restored (nothing sticky: a
 * sticky page that was put aside would be one that is no longer live), and never one that is already there. Pages opened meanwhile that do not hold the screen
 * (a code prompt above the time-up page, say) are closed properly, like clearStack does: each is told, so a code entry resolves as cancelled and nothing hangs.
 */
export function restoreStack(state, aside) {
  const unique = [...new Set(aside ?? [])];
  const { keep, closing } = splitStack(state.stack);
  const back = unique.filter((p) => !p.sticky && !state.stack.includes(p));
  state.stack = [...back, ...keep];
  for (const page of closing) if (!unique.includes(page)) page.onClose?.();
}

/**
 * The browser takes over the screen (the host says "show"); true when it did (a tab switch to it happened). Whatever holds the screen (the time-up page, a
 * required profile picker) stays on the stack, shown over the browser tab; where the browser came from is kept only when there is none (a second "show" never
 * overwrites it) or when we are not on the browser tab any more (then it is stale); pages over the browser tab itself are closed properly in the other case.
 */
export function enterBrowser(state, at, currentIsBrowser) {
  if (!(state.tab !== at || (!currentIsBrowser && !screenHeld(state)))) return false;
  if (!state.browserOrigin || state.tab !== at) state.browserOrigin = { tab: state.tab, stack: takeOverStack(state) };
  else clearStackOf(state);
  state.tab = at;
  return true;
}

/** The browser gives the screen back: false when it does not know where it came from (the caller goes home); true when the tab and the live pages are back. */
export function leaveBrowser(state) {
  if (!state.browserOrigin) return false;
  const { tab, stack } = state.browserOrigin;
  state.browserOrigin = null;
  restoreStack(state, stack);
  state.tab = tab;
  return true;
}

/** The host and the phone may navigate (home, the guide menu, a tab) only when nothing holds the screen. */
export const navAllowed = (state) => !screenHeld(state);

/** What a chip of the bar does now: 'blocked' (an overlay, a held screen, a page that holds the screen), 'bell' or 'downloads'. */
export function chipAction(state, view, id) {
  if (state?.overlay || !tabClickAllowed(view, state)) return 'blocked';
  return id === 'bell' ? 'bell' : id === 'downloads' ? 'downloads' : id === 'unlock' ? 'unlock' : 'blocked';
}

/**
 * What to do about the time-up page: 'close' when the status is known and the time is not up (or the game is gone), so a sticky page can never hang because an
 * event was missed, but only from a status that was freshly and successfully refreshed (`fresh`: a cached status from before must not close it); 'push' when the time is up, a game runs and the page is missing from the stack (whatever took it off); otherwise null.
 */
export function timeUpAction({ page, stack, status, session, fresh = false }) {
  if (!page) return null;
  if (status && fresh && (!status.expired || !session)) return 'close';
  if (status?.expired && session && !stack.includes(page)) return 'push';
  return null;
}
