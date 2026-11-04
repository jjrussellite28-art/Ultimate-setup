// Power menu rules without DOM or host calls (tested in scripts/ui-logic-check.mjs): which actions the mode offers, how the
// menu groups them and where the focus starts or comes back to.

/**
 * The action an id stands for in the current mode. Shell mode: the PC mode (back with the Guide button) and a restart into it; no
 * quit. App mode: "pc-mode" is closing Mega Hub, and a restart into the PC mode means nothing (Windows starts its desktop anyway).
 */
export function forMode(id, isShell) {
  if (isShell) return id === 'quit' ? null : id;
  if (id === 'pc-mode') return 'quit';
  return id === 'restart-pc' ? null : id;
}

/** The sleep timer sits in the menu like an action, but it is no power action (it only picks a time). */
export const SLEEP_TIMER = 'sleep-timer';

/** The "Mehr" row's id in the flat entry list (it is no power action: it folds the rare entries in and out). */
export const MORE = 'more';

/**
 * The menu in three parts: the everyday computer actions as large tiles, the rare ones behind a collapsed "Mehr" row, the sleep
 * timer as one row at the bottom. Ids before forMode. The secondary list is in the order of the menu; app mode turns "pc-mode"
 * into "quit" (forMode), so quit is listed once.
 */
export const POWER_PRIMARY = ['standby', 'shutdown', 'restart'];
export const POWER_SECONDARY = ['pc-mode', 'restart-pc', 'restart-app', 'quit'];

/** Entries that end something or lose unsaved work are tinted as dangerous; the Mega Hub entries and standby come back by themselves. */
export const isDangerous = (id) => id === 'restart' || id === 'restart-pc' || id === 'shutdown';

const offered = (ids, isShell) => [...new Set(ids.map((id) => forMode(id, isShell)).filter(Boolean))];

/** What the mode offers: { primary, more, timer } with forMode applied; "more" is empty (the row is hidden) if nothing is left. */
export function powerLayout(isShell, { sleepTimer = true } = {}) {
  return { primary: offered(POWER_PRIMARY, isShell), more: offered(POWER_SECONDARY, isShell), timer: !!sleepTimer };
}

/** The flat entry list in menu order: tiles, the "Mehr" row (if there is anything behind it), its entries when expanded, the timer. */
export function powerEntryIds(layout, expanded) {
  return [
    ...layout.primary,
    ...(layout.more.length ? [MORE, ...(expanded ? layout.more : [])] : []),
    ...(layout.timer ? [SLEEP_TIMER] : [])
  ];
}

/** True for an entry that lives behind "Mehr": a menu that comes back for it reopens expanded. */
export const isSecondary = (layout, id) => layout.more.includes(id);

/** Where the focus starts: on standby (reversible and the most common wish), else on the first entry. */
export const POWER_START = 'standby';

/** Index of an id in the flat entry list (a menu that comes back after a cancelled question); unknown ids start on POWER_START. */
export function powerIndex(ids, id) {
  const at = ids.indexOf(id);
  if (at >= 0) return at;
  return Math.max(0, ids.indexOf(POWER_START));
}
