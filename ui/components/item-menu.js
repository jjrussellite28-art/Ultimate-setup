// The Y menu of one library entry: the same in every library view (Games, Apps, a collection, Favorites), so "Hide" is always one
// button and one entry away.

import { t } from '../core/i18n.js';
import { api } from '../core/api.js';
import { toggleFavorite } from '../core/library.js';
import { push } from '../core/nav.js';
import { changeCollections, collectionById } from '../core/collections.js';
import { confirmDialog, openOverlay } from './overlay.js';
import { collectionMenu } from './collections.js';
import { guarded } from './passcode.js';
import { hideEntry } from './library-tools.js';
import { detailPage } from '../views/detail.js';

/** The button and label of the menu in the hint bar of every library view. */
export const itemMenuHint = () => ['Y', t('grid.hint.menu')];

/**
 * Opens the menu for `item`. `collectionId`: the page of that collection (it offers taking the entry out of it).
 * Hide is the last entry before Cancel; it asks once and needs the unlock (and the parents' code with parental controls on).
 */
export function openItemMenu(item, { collectionId = '' } = {}) {
  const active = collectionId ? collectionById(collectionId) : null;
  openOverlay({
    title: item.name,
    entries: [
      { label: item.favorite ? t('grid.item.unfavorite') : t('grid.item.favorite'), icon: item.favorite ? '★' : '☆', run: () => toggleFavorite(item) },
      { label: t('grid.item.collections'), icon: '▤', detail: t('grid.item.collectionsHint'), run: guarded(t('grid.unlock.collections'), () => collectionMenu(item)) },
      { icon: '▣', label: t('grid.item.details'), run: () => push(detailPage(item)) },
      ...(active ? [{
        icon: '🗑',
        label: t('grid.item.remove', { name: active.name }),
        danger: true,
        run: guarded(t('grid.unlock.collections'), async () => {
          if (!(await confirmDialog(t('grid.item.removeConfirm', { item: item.name, name: active.name }), t('grid.item.removeAction'), { danger: true }))) return;
          await changeCollections(() => api.setCollectionMember(active.id, item.id, false), t('grid.item.removed', { item: item.name, name: active.name }));
        })
      }] : []),
      { icon: '⛔', label: t('grid.item.hide'), detail: t('grid.item.hideHint'), run: () => hideEntry(item) },
      { icon: '✕', label: t('grid.cancel'), run: () => {} }
    ]
  });
}
