// Controller file browser for picking executables and folders.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { move, push, render, removePage } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';

const ICONS = { drive: '🖴', place: '★', folder: '📁', file: '▶', pick: '✓' };

/**
 * Opens the browser as a page. Resolves to the chosen path, or undefined if cancelled.
 * options: { title, mode: 'file' | 'folder', extensions: ['.exe'], start }
 */
export function openFileBrowser({ title = t('fileBrowser.title'), mode = 'file', extensions = null, start = null } = {}) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      resolve(result);
    };

    const page = {
      id: 'files',
      name: t('fileBrowser.name'),
      index: 0,
      path: start,
      listing: null,
      loading: false,
      // B goes up a folder; only at the top level it closes the browser.
      captures: (action) => action === 'b' && page.path !== null,
      onClose: () => finish(undefined),
      async open(path, focusName = null) {
        this.loading = true;
        render();
        const listing = await attempt(() => api.browse(path, mode === 'file' ? extensions : null), t('fileBrowser.readFailed'));
        this.loading = false;
        if (listing) {
          this.path = listing.path;
          this.listing = listing;
          const found = focusName ? this.rows().findIndex((r) => r.name === focusName) : -1;
          this.index = Math.max(0, found);
        }
        if (state.stack.at(-1) === this) render();
      },
      rows() {
        const entries = this.listing?.entries ?? [];
        // Folder mode: picking the current folder is the first row.
        return mode === 'folder' && this.path ? [{ name: t('fileBrowser.pickFolder'), path: this.path, kind: 'pick' }, ...entries] : entries;
      },
      build(root) {
        if (!this.listing && !this.loading) {
          this.open(this.path);
          return;
        }
        const rows = this.rows();
        this.index = Math.min(this.index, Math.max(0, rows.length - 1));
        this.items = rows.map((r) => el('li', { className: `file-row file-${r.kind}` },
          el('span', { className: 'file-icon' }, iconEl(ICONS[r.kind])),
          el('span', { className: 'file-name', textContent: r.name }),
          r.kind === 'file' ? el('span', { className: 'file-size', textContent: formatSize(r.size) }) : null,
          r.kind === 'drive' && r.size ? el('span', { className: 'file-size', textContent: t('fileBrowser.free', { size: formatSize(r.size) }) }) : null));
        this.scroller = el('ul', { className: 'file-list list-scroll' }, this.items);
        root.append(el('div', { className: 'files' },
          el('div', { className: 'files-head' },
            el('h2', { textContent: title }),
            el('p', { className: 'meta files-path', textContent: this.path ?? t('fileBrowser.roots') })),
          this.loading
            ? el('p', { className: 'empty-note', textContent: t('fileBrowser.loading') })
            : rows.length ? this.scroller : el('p', { className: 'empty-note', textContent: mode === 'file' ? t('fileBrowser.noFiles') : t('fileBrowser.noFolders') })));
        this.update({ instant: true });
      },
      update({ instant = false } = {}) {
        if (!this.items?.length) return;
        setFocused(this.items, this.index);
        keepInView(this.scroller, this.items[this.index], { instant });
      },
      choose(path) {
        removePage(this);
        sound.select();
        render();
        finish(path);
      },
      activate() {
        const row = this.rows()[this.index];
        if (!row) return;
        if (row.kind === 'pick' || (row.kind === 'file' && mode === 'file')) return this.choose(row.path);
        if (row.kind !== 'file') {
          sound.select();
          return this.open(row.path);
        }
      },
      up() {
        if (this.path === null) return;
        sound.back();
        const current = this.path;
        const name = current.replace(/[\\/]+$/, '').split(/[\\/]/).pop();
        // Leaving a drive root goes back to the list of drives.
        this.open(this.listing?.parent ?? null, this.listing?.parent ? name : null);
      },
      handle(action) {
        const count = this.rows().length;
        switch (action) {
          case 'up': return move(this, -1, count, true);
          case 'down': return move(this, 1, count, true);
          case 'a': return this.activate();
          case 'b': return this.up();
          case 'y':
            sound.select();
            return this.open(null);
        }
      },
      hints() {
        const row = this.rows()[this.index];
        const pick = row?.kind === 'pick' ? t('fileBrowser.pickFolder') : row?.kind === 'file' ? (mode === 'file' ? t('fileBrowser.hint.select') : null) : t('fileBrowser.hint.open');
        return [...(row && pick ? [['A', pick]] : []), ...(this.path === null ? [] : [['Y', t('fileBrowser.hint.drives')]]), ['B', this.path ? t('fileBrowser.hint.up') : t('fileBrowser.hint.cancel')]];
      }
    };
    push(page);
  });
}
