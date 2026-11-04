// The boot screen swallows all input until it is gone. The state is a flag that starts closed and only the end of the boot screen opens it,
// so an unknown or broken state (the page failed before the screen was dismissed) keeps input blocked instead of letting a press through to the
// UI underneath (the parental lock, the PIN prompt, a tile). Every way in goes through here: the controller and keyboard actions (input.js),
// the app's action handler (the mouse and touch end there too) and, for everything else, a shield on the window that stops the raw events.

/** How long the fade out of the boot screen takes (styles.css #boot): input opens after it, so nothing is operated while it is still visible. */
export const BOOT_FADE_MS = 650;

/** The events the shield stops while input is closed: every kind of key, mouse, pointer, touch and wheel event that can reach the page. */
export const SHIELDED_EVENTS = Object.freeze([
  'keydown', 'keyup', 'keypress', 'mousedown', 'mouseup', 'click', 'dblclick', 'auxclick', 'contextmenu', 'pointerdown', 'pointerup',
  'touchstart', 'touchend', 'wheel', 'beforeinput', 'compositionstart', 'compositionupdate', 'input', 'paste', 'drop'
]);

let open = false; // closed until the boot screen says it is gone

/** Whether input may reach the UI now. */
export const inputOpen = () => open;

/** The boot screen is gone: input opens (once; there is no way to close it again, the screen is shown only at the start). */
export function openInput() {
  open = true;
}

/** For tests: back to the closed start state. */
export function resetGate() {
  open = false;
}

/** Stops an event while input is closed; true when it was stopped. */
export function shield(event) {
  if (open) return false;
  event.stopImmediatePropagation?.();
  event.stopPropagation?.();
  event.preventDefault?.();
  return true;
}

/** Puts the shield on the target in the capture phase; it has to be the first code of the page to listen (app.js imports this first). */
export function installShield(target = typeof window === 'undefined' ? null : window) {
  if (!target) return;
  for (const name of SHIELDED_EVENTS) target.addEventListener(name, shield, { capture: true, passive: false });
}

/**
 * Ends the boot screen: its node gets the "done" class (the fade out) and input opens after the fade. Without a boot node there is
 * nothing to wait for and input opens at once. A node that already is "done" (it is gone to the eye) opens input too. Returns true
 * when the screen was shut by this call (the caller plays the start sound then).
 */
export function endBoot(boot, schedule = setTimeout) {
  const fresh = !!boot && !boot.classList.contains('done');
  if (fresh) boot.classList.add('done');
  if (boot) schedule(openInput, BOOT_FADE_MS);
  else openInput();
  return fresh;
}

// On load, before any other module of the page can add a listener (no window in the tests: nothing happens there).
installShield();
