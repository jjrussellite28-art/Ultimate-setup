// One collection as its own page: games and apps together, counted as shown (no hidden or uninstalled entries).

import { t } from '../core/i18n.js';
import { collectionById, collectionItems } from '../core/collections.js';
import { gridView } from './grid.js';

export function collectionPage(id) {
  const view = gridView(t('collection.title'), () => {
    const collection = collectionById(id);
    return collection ? collectionItems(collection) : [];
  }, {
    columns: 6,
    kind: 'cover',
    className: 'grid-covers',
    collectionId: id,
    empty: [t('collection.emptyTitle'), t('collection.emptyText')]
  });
  view.id = 'collection';
  return view;
}
