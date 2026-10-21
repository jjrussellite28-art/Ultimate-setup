// Captures: screenshots and clips from Game Bar, Steam, NVIDIA and Windows.

import { t } from '../core/i18n.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { currentView, move, push, render } from '../core/nav.js';
import { timeAgo } from './news.js';

const COLUMNS = 4;

export function galleryPage() {
  return {
    id: 'gallery',
    get name() { return t('gallery.title'); },
    index: 0,
    items: null,
    build(root) {
      if (this.items === null) {
        root.append(el('p', { className: 'empty-note', textContent: t('gallery.searching') }));
        attempt(() => api.getCaptures(), t('gallery.loadFailed')).then((list) => {
          this.items = list ?? [];
          if (currentView() === this) render();
        });
        this.tiles = [];
        return;
      }
      this.index = Math.min(this.index, Math.max(0, this.items.length - 1));
      this.tiles = this.items.map((c) => el('div', { className: 'capture' },
        // Clips get a placeholder: loading every video just for a thumbnail costs too much.
        c.video
          ? el('div', { className: 'capture-clip', textContent: c.name })
          : el('img', { src: c.url, loading: 'lazy', alt: '' }),
        c.video ? el('span', { className: 'capture-play', textContent: '▶' }) : null,
        el('span', { className: 'capture-meta', textContent: `${c.source} · ${timeAgo(c.time)}` })));
      this.grid = el('div', { className: 'capture-grid list-scroll' }, this.tiles);
      root.append(el('div', { className: 'files gallery' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('gallery.title') }),
          el('p', { className: 'meta', textContent: this.items.length ? t('gallery.meta', { count: this.items.length }) : t('gallery.empty') })),
        this.items.length ? this.grid : null));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.tiles?.length) return;
      setFocused(this.tiles, this.index);
      keepInView(this.grid, this.tiles[this.index], { instant });
    },
    handle(action) {
      const count = this.items?.length ?? 0;
      switch (action) {
        case 'left': return move(this, -1, count, true);
        case 'right': return move(this, 1, count, true);
        case 'up': return move(this, -COLUMNS, count, true);
        case 'down': return move(this, COLUMNS, count, true);
        case 'a':
          if (count) push(viewerPage(this.items, this.index));
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return this.items?.length ? [['A', t('gallery.hintView')], ['B', t('gallery.hintBack')]] : [['B', t('gallery.hintBack')]];
    }
  };
}

function viewerPage(items, start) {
  return {
    id: 'viewer',
    get name() { return t('gallery.capture'); },
    index: start,
    build(root) {
      const c = items[this.index];
      const media = c.video
        ? el('video', { src: c.url, controls: true, autoplay: true, className: 'viewer-media' })
        : el('img', { src: c.url, alt: '', className: 'viewer-media' });
      root.append(el('div', { className: 'viewer' }, media,
        el('div', { className: 'viewer-caption' },
          el('strong', { textContent: c.name }),
          el('span', { textContent: `${c.source} · ${timeAgo(c.time)} · ${formatSize(c.size)} · ${this.index + 1} / ${items.length}` }))));
    },
    handle(action) {
      if (action === 'left' && this.index > 0) {
        this.index--;
        return render();
      }
      if (action === 'right' && this.index < items.length - 1) {
        this.index++;
        return render();
      }
    },
    hints() {
      return [['◀▶', t('gallery.hintBrowse')], ['B', t('gallery.hintBack')]];
    }
  };
}
