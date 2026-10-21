// Collection menus: put an entry into collections, create, rename, pin to Home, delete.

import { entriesLabel } from '../core/dom.js';
import { api } from '../core/api.js';
import { state } from '../core/state.js';
import { collectionById, changeCollections, visibleCount } from '../core/collections.js';
import { attempt } from '../core/feedback.js';
import { guarded } from './passcode.js';
import { chooseDialog, confirmDialog, openOverlay } from './overlay.js';
import { openKeyboard } from './keyboard.js';
import { detailPage } from '../views/detail.js';
import { toast } from '../core/feedback.js';
import { render } from '../core/nav.js';
import { t } from '../core/i18n.js';

/** Asks for a name and creates the collection; resolves to it, or undefined if cancelled. */
export async function createCollectionFlow() {
  const name = await openKeyboard({ title: t('collections.nameTitle'), placeholder: t('collections.namePlaceholder'), hint: t('collections.nameHint'), maxLength: 40 });
  if (!name?.trim()) return undefined;
  // The host answers with the new id, so no guessing by name.
  const created = await attempt(() => api.createCollection(name), t('collections.createFailed'));
  if (!created) return undefined;
  state.collections = created.collections;
  toast(t('collections.created', { name: name.trim() }));
  render();
  return state.collections.find((c) => c.id === created.id);
}

/** Check list of all collections for one entry: A adds or removes, the menu stays open. */
export function collectionMenu(item, index = 0) {
  const entries = state.collections.map((c, i) => {
    const member = c.items.includes(item.id);
    return {
      label: `${member ? '✓' : '○'}   ${c.name}`,
      detail: member ? t('collections.member') : entriesLabel(visibleCount(c)),
      run: async () => {
        await changeCollections(
          () => api.setCollectionMember(c.id, item.id, !member),
          member ? t('collections.removed', { item: item.name, collection: c.name }) : t('collections.added', { item: item.name, collection: c.name }));
        collectionMenu(item, i);
      }
    };
  });
  entries.push({
    label: t('collections.new'), icon: '+',
    run: async () => {
      const created = await createCollectionFlow();
      if (created) await changeCollections(() => api.setCollectionMember(created.id, item.id, true), t('collections.added', { item: item.name, collection: created.name }));
      collectionMenu(item, entries.length - 1);
    }
  });
  entries.push({ label: t('collections.done'), run: () => {} });
  openOverlay({ title: t('collections.menuTitle', { name: item.name }), index: Math.min(index, entries.length - 1), entries });
}

/**
 * Overview with create / rename / pin / delete. Resolves to the id of a collection the
 * user wants to look at (the caller filters its list by it), or undefined.
 */
export async function manageCollections() {
  const options = [
    ...state.collections.map((c) => ({
      label: c.name,
      detail: `${entriesLabel(visibleCount(c))}${c.onHome ? ` · ${t('collections.onHome')}` : ''}`,
      value: c.id
    })),
    { label: t('collections.new'), icon: '+', value: '__new' }
  ];
  const picked = await chooseDialog(t('collections.title'), options);
  if (picked === undefined) return undefined;
  if (picked === '__new') {
    await createCollectionFlow();
    return manageCollections();
  }
  return collectionActions(collectionById(picked));
}

async function collectionActions(collection) {
  if (!collection) return undefined;
  const choice = await chooseDialog(collection.name, [
    { label: t('collections.show'), detail: entriesLabel(visibleCount(collection)), value: 'show' },
    { label: t('collections.rename'), value: 'rename' },
    { label: collection.onHome ? t('collections.unpin') : t('collections.pin'), detail: collection.onHome ? t('collections.unpinDetail') : t('collections.pinDetail'), value: 'home' },
    { label: t('collections.delete'), detail: t('collections.deleteDetail'), value: 'delete' }
  ]);
  switch (choice) {
    case 'show':
      return collection.id;
    case 'rename': {
      const name = await openKeyboard({ title: t('collections.newName'), value: collection.name });
      if (name?.trim()) await changeCollections(() => api.renameCollection(collection.id, name), t('collections.renamed'));
      return manageCollections();
    }
    case 'home':
      await changeCollections(() => api.setCollectionOnHome(collection.id, !collection.onHome),
        collection.onHome ? t('collections.unpinned') : t('collections.pinned'));
      return manageCollections();
    case 'delete':
      if (await confirmDialog(t('collections.deleteConfirm', { name: collection.name }), t('collections.delete'), { danger: true })) {
        await changeCollections(() => api.deleteCollection(collection.id), t('collections.deleted'));
      }
      return manageCollections();
    default:
      return undefined;
  }
}

detailPage.extraActions.push((item) => {
  const n = state.collections.filter((c) => c.items.includes(item.id)).length;
  return { label: n ? t('collections.titleCount', { count: n }) : t('collections.title'), icon: '▤', run: guarded(t('collections.guard'), () => collectionMenu(item)) };
});
