// Companion apps and apps in the Guide menu, the page's side (pure, no DOM; tested by scripts/ui-logic-check.mjs). The host decides everything
// (src/MegaHub.Core/Library/CompanionRules.cs, GuideAppRules.cs) and checks again at each start; this module only shapes what the page shows
// and what it sends. A rule names a library app by id and never a path or a parameter.

/** Mirrors of the host's limits (the host refuses more). */
export const MAX_RULES = 12;
export const MAX_GAMES = 50;
export const MAX_GUIDE_APPS = 12;

export const MODE_ALL = 'all';
export const MODE_GAMES = 'games';

/** Rule fields the page may change (the host reads exactly these). */
export const FIELDS = ['mode', 'gameIds', 'closeWithGame', 'minimized', 'whenLocked'];

/** A fresh draft of a rule from what the host sent (games as ids). */
export function draftOf(rule) {
  return {
    mode: rule?.mode === MODE_ALL ? MODE_ALL : MODE_GAMES,
    gameIds: (rule?.games ?? rule?.gameIds ?? []).map((g) => (typeof g === 'string' ? g : g.id)),
    closeWithGame: rule?.closeWithGame === true,
    minimized: rule?.minimized !== false,
    whenLocked: rule?.whenLocked === true
  };
}

const sameIds = (a, b) => a.length === b.length && a.every((id, i) => id === b[i]);

/** What differs between the saved rule and the draft, as the host's change object; null when nothing does. */
export function changeOf(rule, draft) {
  const saved = draftOf(rule);
  const change = {};
  if (draft.mode !== saved.mode) change.mode = draft.mode;
  if (!sameIds(draft.gameIds, saved.gameIds)) change.gameIds = [...draft.gameIds];
  for (const key of ['closeWithGame', 'minimized', 'whenLocked']) if (draft[key] !== saved[key]) change[key] = draft[key];
  return Object.keys(change).length ? change : null;
}

/** A new rule is saved even when it equals the defaults (the first save creates it). */
export const isNewRule = (rule) => !rule || rule.saved === false;

/** The second line of a rule's row: which games, as a text key with its values. */
// i18n-used: companion.sum.all companion.sum.games companion.sum.none
export function summary(rule) {
  if (rule?.mode === MODE_ALL) return { key: 'companion.sum.all' };
  const count = (rule?.games ?? rule?.gameIds ?? []).length;
  return count ? { key: 'companion.sum.games', params: { count } } : { key: 'companion.sum.none' };
}

/** The switches that are on, as text keys, in a fixed order. */
// i18n-used: companion.flag.close companion.flag.minimized companion.flag.locked
export function flagKeys(rule) {
  const keys = [];
  if (rule?.closeWithGame) keys.push('companion.flag.close');
  if (rule?.minimized !== false) keys.push('companion.flag.minimized');
  if (rule?.whenLocked) keys.push('companion.flag.locked');
  return keys;
}

/** The host names the reason a rule cannot start in its words (CompanionVerdict names, "missing", "area", "start"); the page shows a text for it. */
// i18n-used: companion.problem.missing companion.problem.area companion.problem.start companion.problem.signature companion.problem.pinMissing companion.problem.pinChanged companion.problem.folderWritable companion.problem.other
export function problemKey(problem) {
  if (!problem) return null;
  return ['missing', 'area', 'start', 'signature', 'pinMissing', 'pinChanged', 'folderWritable'].includes(problem) ? `companion.problem.${problem}` : 'companion.problem.other';
}

/** The problems a parent can clear with one "confirm" for the locked couch (no hash yet, or the program changed). */
export const confirmable = (problem) => problem === 'pinMissing' || problem === 'pinChanged' || problem === 'folderWritable';

/** File size for the confirm dialog (binary units, one decimal from KB up). */
export function formatSize(bytes) {
  const n = Number(bytes) || 0;
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = n / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  return `${value.toFixed(1)} ${units[i]}`;
}

/** The lines of the confirm dialog from the host's preview: hash prefix, size, date, signer or "unsigned", version, and the warnings. */
// i18n-used: companion.confirmFile companion.confirmSigned companion.confirmUnsigned companion.confirmVersion companion.confirmChanged companion.confirmFolder
export function pinLines(preview, t) {
  const lines = [t('companion.confirmFile', { hash: preview.hash, size: formatSize(preview.size), modified: preview.modified })];
  lines.push(preview.signer ? t('companion.confirmSigned', { signer: preview.signer }) : t('companion.confirmUnsigned'));
  if (preview.version) lines.push(t('companion.confirmVersion', { version: preview.version }));
  if (preview.changed) lines.push(t('companion.confirmChanged'));
  if (preview.folderWritable) lines.push(t('companion.confirmFolder'));
  return lines;
}

/** The game list after a toggle: added at the end, or taken out. Capped like the host. */
export function toggleGame(ids, id) {
  if (ids.includes(id)) return ids.filter((x) => x !== id);
  return ids.length >= MAX_GAMES ? ids : [...ids, id];
}

/** Rows of games for the picker: checked ones are marked, the rest of the list keeps the host's order; a game no longer in the library is dropped. */
export function gameRows(games, ids) {
  const chosen = new Set(ids);
  return (games ?? []).map((g) => ({ id: g.id, name: g.name, checked: chosen.has(g.id) }));
}

/** Can the Wand offer show: Wand.exe was found and Wand is not a companion yet. */
export const wandOffer = (wand) => !!wand?.found && !wand?.hasRule;

// ---- apps in the Guide menu

/** Only an app can be put into the Guide menu. */
export const canGuide = (item) => item?.type === 'app';

/** Is this app in the saved list. */
export const inGuide = (ids, id) => Array.isArray(ids) && ids.includes(id);

/** The row in an app's menu: its text key, depending on the state. */
// i18n-used: guideApps.add guideApps.remove
export const guideRowKey = (ids, id) => (inGuide(ids, id) ? 'guideApps.remove' : 'guideApps.add');

/** The Guide menu rows from the host's list: id, name; nothing else is trusted for display. */
export function guideRows(items) {
  return (Array.isArray(items) ? items : [])
    .filter((i) => i && typeof i.id === 'string' && typeof i.name === 'string')
    .slice(0, MAX_GUIDE_APPS)
    .map((i) => ({ id: i.id, name: i.name }));
}
