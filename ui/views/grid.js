import { el, entriesLabel, keepInView, setFocused } from '../core/dom.js';
import { artwork, plainBackground, setBackground } from '../core/art.js';
import { SOURCE_LABEL } from '../core/library.js';
import { toast } from '../core/feedback.js';
import { move, push, render } from '../core/nav.js';
import { chooseDialog } from '../components/overlay.js';
import { openKeyboard } from '../components/keyboard.js';
import { manageCollections } from '../components/collections.js';
import { collectionById, visibleCount } from '../core/collections.js';
import { ensureUnlocked } from '../components/passcode.js';
import { openItemMenu, itemMenuHint } from '../components/item-menu.js';
import { uninstalledPage } from './uninstalled.js';
import { areaOn } from '../core/areas.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { lang, lower, t } from '../core/i18n.js';

const sorts = () => [
  { value: 'recent', label: t('grid.sort.recent') },
  { value: 'name', label: t('grid.sort.name') },
  { value: 'playtime', label: t('grid.sort.playtime') },
  { value: 'source', label: t('grid.sort.source') }
];

/** Search, favorites and sort on top of the tab's list, like the library filters on handhelds. */
function filtered(list, { query, favorites, sort }) {
  let items = list;
  if (query) {
    const q = lower(query);
    items = items.filter((i) => lower(i.name).includes(q));
  }
  if (favorites) items = items.filter((i) => i.favorite);
  const byName = (a, b) => a.name.localeCompare(b.name, lang());
  switch (sort) {
    case 'name': return [...items].sort(byName);
    case 'playtime': return [...items].sort((a, b) => (b.playtimeMinutes ?? 0) - (a.playtimeMinutes ?? 0) || byName(a, b));
    case 'source': return [...items].sort((a, b) => (a.source ?? '').localeCompare(b.source ?? '') || byName(a, b));
    default: return items;
  }
}
import { detailPage } from './detail.js';
import { scheduleTrailer } from '../core/media.js';
import { addEntryFlow } from '../components/library-tools.js';
import { requestPower } from '../core/power.js';

/** The last tile of the game and app pages: starts the add flow for the kind of the page. */
const addTile = (kind) => ({
  id: '__add', name: t('grid.add.name'), type: kind, source: '', builtin: true, add: true,
  art: { cover: ['assets/add-tile.svg'], hero: [], logo: [] }, open: () => addEntryFlow({ kind })
});

/**
 * The way out of Mega Hub as the last tile of the Games and Apps pages: "Quit Mega Hub" in app mode, "PC mode" in shell mode (where
 * quitting is not offered). The same question and couch lock as the power menu (requestPower).
 */
const quitTile = () => {
  const id = state.info?.isShell ? 'pc-mode' : 'quit';
  return {
    id: '__quit', name: t(id === 'quit' ? 'power.quit.label' : 'power.pcMode.label'), type: 'app', source: '', builtin: true, add: true,
    art: { cover: ['assets/quit-tile.svg'], hero: [], logo: [] }, open: () => requestPower(id)
  };
};

/** Opens the mixed games-and-apps page of a collection (app.js pushes it). */
const openCollection = (id) => window.dispatchEvent(new CustomEvent('couchhud:show-collection', { detail: { id } }));

/**
 * Cover grid for the game and app tabs. With collectionId it is the page of one collection
 * (games and apps together, B goes back one step, Y can take an entry out of it).
 */
export function gridView(name, getItems, { columns, kind, className, empty, collectionId = '', addKind = '' }) {
  const backLabel = () => (collectionId ? t('grid.back') : t('grid.home'));
  /** The Games tab (not a collection, not Apps) with the Gaming area on: View opens the games that are not installed. */
  const installsGames = () => addKind === 'game' && !collectionId && areaOn('games');
  return {
    name,
    index: 0,
    items: getItems,
    columns,
    keepsTrailer: true,
    filter: { query: '', favorites: false, sort: 'recent' },
    filterLabel() {
      const f = this.filter;
      return [f.query ? `„${f.query}“` : null, f.favorites ? t('grid.favorites') : null, f.sort !== 'recent' ? sorts().find((s) => s.value === f.sort)?.label : null].filter(Boolean).join(' · ');
    },
    build(root) {
      const all = getItems();
      const found = filtered(all, this.filter);
      // The way to add an entry is always the last tile (not on the page of a collection).
      const adder = addKind && !collectionId ? [addTile(addKind)] : [];
      this.list = [...found, ...adder, ...(addKind && !collectionId ? [quitTile()] : [])];
      this.tiles = [];
      if (all.length === 0 && !adder.length) {
        plainBackground();
        root.append(el('div', { className: 'empty' }, el('h2', { textContent: empty[0] }), el('p', { textContent: empty[1] })));
        return;
      }
      if (found.length === 0 && !adder.length) {
        plainBackground();
        root.append(el('div', { className: 'empty' },
          el('h2', { textContent: t('grid.none.title') }),
          el('p', { textContent: t('grid.none.text', { filter: this.filterLabel() }) })));
        return;
      }
      this.index = Math.min(this.index, this.list.length - 1);
      // Nothing to show yet or nothing found: the add tile stands alone, with the reason above it.
      if (found.length === 0) {
        root.append(el('div', { className: 'empty empty-above' },
          el('h2', { textContent: all.length === 0 ? empty[0] : t('grid.none.title') }),
          el('p', { textContent: all.length === 0 ? empty[1] : t('grid.none.text', { filter: this.filterLabel() }) })));
      }

      const grid = el('div', { className: `grid ${className}` });
      grid.style.setProperty('--cols', columns);
      this.tiles = this.list.map((item, i) => {
        const tile = el('div', { className: 'tile', dataset: { index: i } },
          artwork(item, kind, kind, { tile: true }),
          kind !== 'cover' ? el('span', { className: 'tile-label', textContent: item.name }) : null,
          item.favorite ? el('span', { className: 'fav', textContent: '★' }) : null);
        if (item.add) tile.classList.add('tile-add');
        grid.append(tile);
        return tile;
      });
      this.title = el('h2');
      this.meta = el('span', { className: 'meta' });
      this.scroller = el('div', { className: 'grid-scroll' }, grid);
      root.append(el('div', { className: 'grid-title' }, this.title, this.meta), this.scroller);
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.tiles.length) return;
      const focused = this.list[this.index];
      setFocused(this.tiles, this.index);
      // A collection page is headed by the collection; the focused entry moves into the subtitle.
      const collectionName = collectionId ? collectionById(collectionId)?.name ?? '' : '';
      this.title.textContent = collectionId ? collectionName : focused.name;
      const filter = this.filterLabel();
      this.meta.textContent = `${collectionId ? `${focused.name} · ` : ''}${focused.builtin ? '' : `${SOURCE_LABEL[focused.source] || focused.source || ''} · `}${this.index + 1} / ${this.list.length}${filter ? ` · ${filter}` : ''}`;
      setBackground(focused);
      scheduleTrailer(focused, 2200);
      // Keep the focused row vertically centered.
      keepInView(this.scroller, this.tiles[this.index], { center: true, instant });
    },
    activate() {
      const item = this.list[this.index];
      if (!item) return;
      // Built-in tiles (the browser) open their own page instead of a detail page.
      if (item.open) return item.open();
      push(detailPage(item));
    },
    async search() {
      const query = await openKeyboard({ title: t('grid.search.title', { name }), value: this.filter.query, placeholder: t('grid.search.placeholder') });
      if (query === undefined) return;
      this.filter = { ...this.filter, query: query.trim() };
      this.index = 0;
      render();
    },
    async filterMenu() {
      const f = this.filter;
      const choice = await chooseDialog(t('grid.filter.title', { name }), [
        ...(addKind && !collectionId ? [{ label: t('grid.menu.add'), icon: '+', detail: t('grid.menu.addHint'), value: 'add' }] : []),
        ...(addKind === 'game' && !collectionId ? [{ label: t('grid.menu.uninstalled'), icon: '⤓', detail: t('grid.menu.uninstalledHint'), value: 'uninstalled' }] : []),
        { label: t('grid.menu.search'), icon: '⌕', detail: f.query ? t('grid.menu.searchCurrent', { query: f.query }) : t('grid.menu.searchHint'), value: 'search' },
        { icon: '⇄', label: t('grid.menu.sort', { sort: sorts().find((s) => s.value === f.sort)?.label }), value: 'sort' },
        { label: f.favorites ? t('grid.menu.showAll') : t('grid.menu.onlyFavorites'), icon: '★', value: 'favorites' },
        ...(collectionId ? [] : [
          { label: t('grid.menu.collection'), icon: '▤', detail: t('grid.menu.collectionHint'), value: 'collection' },
          { label: t('grid.menu.manage'), icon: '▤', detail: t('grid.menu.manageHint'), value: 'manage' }
        ]),
        ...(f.query || f.favorites || f.sort !== 'recent' ? [{ icon: '↻', label: t('grid.menu.reset'), value: 'reset' }] : [])
      ]);
      switch (choice) {
        case 'add': return addEntryFlow({ kind: addKind });
        case 'uninstalled': return push(uninstalledPage());
        case 'search': return this.search();
        case 'sort': {
          const sort = await chooseDialog(t('grid.sort.title'), sorts(), f.sort);
          if (sort) this.filter = { ...f, sort };
          break;
        }
        case 'favorites':
          this.filter = { ...f, favorites: !f.favorites };
          break;
        case 'collection': {
          if (!state.collections.length) return toast(t('grid.toast.noCollection'));
          const id = await chooseDialog(t('grid.collection.title'), state.collections.map((c) => ({ label: c.name, detail: entriesLabel(visibleCount(c)), value: c.id })));
          if (id) openCollection(id);
          return;
        }
        case 'manage': {
          if (!(await ensureUnlocked(t('grid.unlock.collections')))) return;
          const id = await manageCollections();
          if (id) openCollection(id);
          return;
        }
        case 'reset':
          this.filter = { query: '', favorites: false, sort: 'recent' };
          break;
        default:
          return;
      }
      this.index = 0;
      render();
    },
    /** Y: favorite, collections, details and hide of the focused tile in one menu (the same menu as on Favorites). */
    itemMenu(item) {
      openItemMenu(item, { collectionId });
    },
    handle(action) {
      if (action === 'x') {
        sound.select();
        return this.filterMenu();
      }
      // View (the controller's back button; "view" from a click on its hint, V on the keyboard): the games that are not installed,
      // in the Games tab only. Also with an empty or filtered list: installing is the way to fill it.
      if (action === 'back' || action === 'view') {
        if (!installsGames()) return sound.bump();
        sound.select();
        return push(uninstalledPage());
      }
      if (!this.list.length) return;
      const item = this.list[this.index];
      switch (action) {
        case 'left': return move(this, -1, this.list.length);
        case 'right': return move(this, 1, this.list.length);
        case 'up': return move(this, -columns, this.list.length, true);
        case 'down': return move(this, columns, this.list.length, true);
        case 'a': return this.activate();
        case 'y': return item.builtin ? sound.bump() : this.itemMenu(item);
      }
    },
    hints() {
      const install = installsGames() ? [['View', t('grid.hint.installGames')]] : [];
      if (!this.list?.length) {
        // Nothing to open or mark; with a filter active X is the way out.
        return getItems().length ? [['X', t('grid.hint.changeFilter')], ...install, ['B', backLabel()]] : [...install, ['B', backLabel()]];
      }
      const item = this.list[this.index];
      return [
        ['A', item?.id === '__quit' ? t('grid.hint.quit') : item?.add ? t('grid.hint.add') : t('grid.hint.open')],
        ['X', t('grid.hint.filter')],
        ...(item?.builtin ? [] : [itemMenuHint()]),
        ...install,
        ['B', backLabel()]
      ];
    }
  };
}
