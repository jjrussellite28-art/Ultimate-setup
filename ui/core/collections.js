// The user's collections: lists of games and apps ("Couch Koop", "Kinder").

import { api } from './api.js';
import { t } from './i18n.js';
import { state } from './state.js';
import { attempt, toast } from './feedback.js';
import { findItem, sortLibrary } from './library.js';
import { render } from './nav.js';

export async function loadCollections() {
  state.collections = (await attempt(() => api.getCollections(), t('collections.loadFailed'))) ?? [];
  render();
}

export const collectionById = (id) => state.collections.find((c) => c.id === id);

/** The visible library entries of a collection (removed or hidden ones are skipped), sorted like the tabs. */
export const collectionItems = (collection) =>
  sortLibrary(collection.items.map(findItem).filter((i) => i && !i.hidden));

/** How many entries of a collection are really there (not hidden, not uninstalled): the number to show. */
export const visibleCount = (collection) => collectionItems(collection).length;

/** Runs a host call that returns the new collection list; true if it worked. */
export async function changeCollections(call, message) {
  const list = await attempt(call, t('collections.changeFailed'));
  if (!list) return false;
  state.collections = list;
  if (message) toast(message);
  render();
  return true;
}
