// The structure of the Downloads tab, pure (tested by scripts/ui-logic-check.mjs). The list holds only what loads, installs or waits
// to be installed; the page's own action (search for updates) is a footer shortcut, never a row.

export const isFinished = (entry) => entry?.state === 'done' || entry?.state === 'failed';

/**
 * The sections of the list, in this order and only when they hold something: what runs now (every source), the updates that can be
 * started, each kind in its own section (Mega Hub, apps through winget, Windows, plugins and skins), then the finished lines. A start
 * row names its section in `group`; the restart that is due (`first`) stands with the Windows updates, it finishes them. A start row
 * of a kind this page does not know goes with the plugins (the rest that can be updated), never lost.
 */
export const SECTIONS = ['running', 'couchhud', 'apps', 'windows', 'plugins', 'finished'];
const UPDATE_SECTIONS = ['couchhud', 'apps', 'windows', 'plugins'];

export function downloadSections({ entries = [], first = [], pending = [] } = {}) {
  const rows = new Map(SECTIONS.map((id) => [id, []]));
  for (const entry of entries) if (!isFinished(entry)) rows.get('running').push({ kind: 'entry', entry });
  for (const row of first) rows.get(UPDATE_SECTIONS.includes(row.group) ? row.group : 'windows').push(row);
  for (const row of pending) rows.get(UPDATE_SECTIONS.includes(row.group) ? row.group : 'plugins').push(row);
  for (const entry of entries) if (isFinished(entry)) rows.get('finished').push({ kind: 'entry', entry });
  return SECTIONS.map((id) => ({ id, rows: rows.get(id) })).filter((s) => s.rows.length);
}

/**
 * The rows of the list, in order (the sections one after the other): only what loads, waits to be installed or has finished. Nothing
 * else: no search row, no row for the games that are not installed.
 */
export function downloadRows(lists = {}) {
  return downloadSections(lists).flatMap((s) => s.rows);
}

/**
 * The page's footer actions, the way plugin pages have theirs: X searches for updates (X searches or reloads on every core page).
 * `searching`: the search runs, the X hint is busy. Installing new games is no download action: it is View in the Games tab.
 */
export function downloadShortcuts({ canSearch = false, searching = false } = {}) {
  return canSearch ? [{ button: 'X', id: 'search', busy: !!searching }] : [];
}

/** Finished lines: with more than one, a finished line's menu also offers to clear them all (it was X before X became the search). */
export function finishedMenu(actions, entries = []) {
  return entries.filter(isFinished).length > 1 && actions.includes('remove') ? [...actions, 'clearFinished'] : actions;
}

/**
 * The A press on an update row and the X press of the search, with what they touch passed in (tested in Node with fakes). A busy row
 * or search only bumps. The lock is asked before anything looks busy: on a locked couch the code entry (or the Windows sign-in) opens
 * right away, and the press goes on once it opened; cancelled, nothing runs and nothing flashed busy (0.6.4: before, the page only said
 * "unlock first" and never asked, so no update could be reached on the locked couch). Then the start runs under its busy key (a second
 * press meanwhile is ignored there); a row without a key (the restart) runs directly, and the restart (open) needs no unlock, like power.
 * The host checks the lock again; a lock that closed in between is answered by withUnlock.
 * deps: { busy, locked, run(key, fn), unlock() -> Promise<boolean>, bump(), select() }
 */
export async function pressPending(row, { busy, locked, run, unlock, bump, select }) {
  if (busy) return bump();
  select();
  if (!row.open && (await locked()) && !(await unlock())) return undefined;
  return row.busyKey ? run(row.busyKey, row.run) : row.run();
}

/** X: the same order for the search (key: the page's search key). */
export async function pressSearch(key, search, { busy, locked, run, unlock, bump, select }) {
  if (busy) return bump();
  select();
  if ((await locked()) && !(await unlock())) return undefined;
  return run(key, search);
}

/**
 * A call of the Downloads page the host may refuse with "unlock first" (the 2-minute unlock ran out while a dialog was open): the code entry
 * opens (`unlock`, resolving to true or false) and the call is made once more; cancelled, the answer is undefined. Any other error, and a
 * second refusal, goes to the caller as it is (the host decides, this only asks).
 */
export async function withUnlock(call, unlock) {
  try {
    return await call();
  } catch (err) {
    if (!err?.locked) throw err;
    if (!(await unlock())) return undefined;
    return call();
  }
}

/**
 * One code entry at a time for a set of gated calls, and a short memory of the last success: calls refused together share the open entry, and a
 * call refused right after that entry closed with an unlock (the second call of the search, ~1 s) reuses it instead of asking again.
 * `ask` resolves to true or false; `now` is the clock in ms (tests pass their own). A cancel or a failure is never remembered.
 */
export function sharedUnlock(ask, now = () => Date.now(), keepMs = 1000) {
  let running = null;
  let openedAt = -Infinity;
  return () => {
    if (running) return running;
    if (now() - openedAt < keepMs) return Promise.resolve(true);
    running = Promise.resolve(ask()).then((ok) => {
      if (ok) openedAt = now();
      return ok;
    }).finally(() => { running = null; });
    return running;
  };
}

/**
 * The footer hints: A stays while its row is busy and only greys out (like Bluetooth and Wi-Fi), then the page's shortcuts with their
 * busy state, then B. labels: { a, search, back }.
 */
export function downloadHints({ hasRow = false, rowBusy = false, shortcuts = [], labels }) {
  return [
    ...(hasRow ? [['A', labels.a, { busy: !!rowBusy }]] : []),
    ...shortcuts.map((s) => [s.button, labels[s.id], { busy: !!s.busy }]),
    ['B', labels.back]
  ];
}
