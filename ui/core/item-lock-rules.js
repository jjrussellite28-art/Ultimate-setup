// Locks of one game or app: which ones apply to it, and what the parents may choose. Pure rules (scripts/ui-logic-check.mjs).
// The host keeps two lists: the active profile's own (status.lockedItems) and the household's for every child profile
// (status.lockedItemsAllChildren, also for profiles made later).

/** 'none', 'profile' (only the active profile), 'all' (all child profiles) or 'both'. */
export function lockScope(status, id) {
  const own = !!status?.lockedItems?.includes(id);
  const all = !!status?.lockedItemsAllChildren?.includes(id);
  return own && all ? 'both' : all ? 'all' : own ? 'profile' : 'none';
}

/**
 * What the parents can do with this entry: [{ locked, all }] (locked: the new state, all: for all child profiles).
 * Unlocking only the profile never hides a lock for all child profiles: that one is its own choice. A single plain choice
 * (lock this profile only, no child profile to share with) needs no question.
 */
export function lockChoices(status, id) {
  switch (lockScope(status, id)) {
    // Widening needs no unlock first: the entry stays locked for the profile and becomes locked for all child profiles.
    case 'profile': return (status?.childProfiles ?? 0) > 0 ? [{ locked: false, all: false }, { locked: true, all: true }] : [{ locked: false, all: false }];
    case 'all': return [{ locked: false, all: true }];
    case 'both': return [{ locked: false, all: false }, { locked: false, all: true }];
    default: return (status?.childProfiles ?? 0) > 0 ? [{ locked: true, all: false }, { locked: true, all: true }] : [{ locked: true, all: false }];
  }
}

/** The question is skipped only for the plain lock and the plain unlock of the profile's own entry. */
export const needsQuestion = (choices) => choices.length > 1 || choices[0].all;

/** The grant action a choice needs: bound to the scope, so a code for one profile cannot pay for all child profiles. */
export const grantAction = (choice) => (choice.all ? 'lockAll' : 'lock');

/** The household list was damaged: until the parents set it anew, every entry is locked for children. */
export const listDamaged = (status) => status?.lockedItemsAllChildrenUnreadable === true;

/** Entries of the locked page: every entry locked in either list, with its scope. */
export function lockedRows(status, find) {
  const ids = [...new Set([...(status?.lockedItems ?? []), ...(status?.lockedItemsAllChildren ?? [])])];
  return ids.map((id) => ({ item: find(id), scope: lockScope(status, id) })).filter((r) => r.item);
}
