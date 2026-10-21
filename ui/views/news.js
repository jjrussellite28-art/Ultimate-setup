// News tab: RSS/Atom headlines as big cards, and a reader made for the couch.

import { api } from '../core/api.js';
import { el, keepInView, loadFirst, setFocused } from '../core/dom.js';
import { hue, setBackground } from '../core/art.js';
import { attempt } from '../core/feedback.js';
import { openLink } from '../core/links.js';
import { currentView, move, push, render } from '../core/nav.js';
import { sound } from '../sound.js';
import { fmtDate, t } from '../core/i18n.js';

const COLUMNS = 3;
let news = null;
let loadedAt = 0;

export function timeAgo(ms) {
  const minutes = Math.round((Date.now() - ms) / 60000);
  if (!ms) return '';
  if (minutes < 1) return t('news.ago.now');
  if (minutes < 60) return t('news.ago.minutes', { count: minutes });
  const hours = Math.round(minutes / 60);
  if (hours < 24) return t('news.ago.hours', { count: hours });
  return fmtDate(ms, { day: 'numeric', month: 'short' });
}

let failed = false;

async function load(force = false) {
  // Ten minutes are plenty; the host caches feeds as well.
  if (!force && news && Date.now() - loadedAt < 10 * 60 * 1000) return;
  const items = await attempt(() => api.getNews(), t('news.loadFailed'));
  if (items) {
    news = items;
    failed = false;
    loadedAt = Date.now();
  } else if (!news) {
    // Offline: show that once instead of asking again on every render.
    news = [];
    failed = true;
  }
}

/** Feeds leave their own clutter behind: "( Weiter lesen )", "noch......", "Zum Beitrag: …". */
export function cleanText(text = '') {
  return text
    .replace(/\s*[([]?\s*(?:weiter\s*lesen|weiterlesen|mehr\s+lesen|read\s+more|mehr\s+erfahren)\s*[)\]]?\s*[»>]*/gi, ' ')
    .replace(/\s*(?:zum\s+beitrag|der\s+beitrag.*?erschien\s+zuerst|the\s+post.*?appeared\s+first).*$/is, '')
    // Feed promos: "Auf dem Laufenden bleiben? Fügt uns doch bei Google als bevorzugte Quelle hinzu!"
    .replace(/(?:\s*auf\s+dem\s+laufenden\s+bleiben\s*\?)?[^.!?]*bevorzugte[nr]?\s+quelle[^.!?]*[.!?]?/gi, ' ')
    .replace(/\s*[([]\s*(?:\.{2,}|…)\s*[)\]]/g, '…')
    .replace(/\s*(?:\.{2,}|…)+\s*$/g, '…')
    .replace(/\.{3,}/g, '…')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

/** Photo that fades in over the tinted placeholder once it has loaded. */
function photo(box, url) {
  loadFirst([url], (loaded) => {
    const layer = el('div', { className: 'photo' });
    layer.style.backgroundImage = `url("${loaded}")`;
    box.append(layer);
    requestAnimationFrame(() => requestAnimationFrame(() => layer.classList.add('in')));
  });
}

function card(item) {
  const image = el('div', { className: 'news-image' });
  image.style.setProperty('--h', hue(item.feed));
  if (item.image) photo(image, item.image);
  return el('article', { className: 'news-card' },
    image,
    el('div', { className: 'news-text' },
      el('span', { className: 'news-source', textContent: `${item.feed} · ${timeAgo(item.published)}` }),
      el('h3', { textContent: item.title }),
      el('p', { textContent: cleanText(item.summary) })));
}

export const newsView = {
  get name() { return t('news.name'); },
  id: 'news',
  index: 0,
  keepsTrailer: false,
  build(root) {
    if (!news) {
      this.cards = null;
      root.append(el('p', { className: 'empty-note news-loading', textContent: t('news.loading') }));
      load().then(() => currentView() === newsView && render());
      return;
    }
    if (!news.length) {
      this.cards = null;
      root.append(el('div', { className: 'empty' },
        el('h2', { textContent: failed ? t('news.offline.title') : t('news.empty.title') }),
        el('p', { textContent: failed ? t('news.offline.text') : t('news.empty.text') })));
      return;
    }
    this.index = Math.min(this.index, news.length - 1);
    this.cards = news.map(card);
    // The newest story gets the big spot, like on a magazine cover.
    this.cards[0].classList.add('news-lead');
    this.scroller = el('div', { className: 'news-grid list-scroll' }, this.cards);
    root.append(el('div', { className: 'news' }, this.scroller));
    setBackground({ id: '__news', name: 'News', art: {} });
    this.update({ instant: true });
    // Refresh quietly in the background when the tab is opened again later.
    load().then(() => {});
  },
  update({ instant = false } = {}) {
    if (!this.cards) return;
    setFocused(this.cards, this.index);
    keepInView(this.scroller, this.cards[this.index], { instant });
  },
  activate() {
    if (news?.[this.index]) push(readerPage(news[this.index]));
  },
  handle(action) {
    const count = news?.length ?? 0;
    // The lead card spans the first row; moving down from it goes to the second card.
    switch (action) {
      case 'left': return move(this, -1, count);
      case 'right': return move(this, 1, count);
      case 'up': return move(this, this.index <= COLUMNS ? -this.index : -COLUMNS, count, true);
      case 'down': return move(this, this.index === 0 ? 1 : COLUMNS, count, true);
      case 'a': return this.activate();
      case 'x':
        sound.select();
        return load(true).then(render);
    }
  },
  hints() {
    return this.cards ? [['A', t('news.hint.read')], ['X', t('news.hint.refresh')], ['B', t('news.hint.home')]] : [['X', t('news.hint.refresh')], ['B', t('news.hint.home')]];
  }
};

function readerPage(item) {
  return {
    id: 'reader',
    get name() { return t('news.article'); },
    build(root) {
      const image = item.image ? el('div', { className: 'reader-image' }) : null;
      if (image) photo(image, item.image);
      this.scroller = el('div', { className: 'reader list-scroll' },
        el('span', { className: 'news-source', textContent: `${item.feed} · ${timeAgo(item.published)}` }),
        el('h1', { textContent: item.title }),
        image,
        (item.paragraphs?.length ? item.paragraphs : [item.summary]).map(cleanText).filter(Boolean).map((p) => el('p', { textContent: p })));
      root.append(this.scroller);
    },
    update() {},
    handle(action) {
      const step = this.scroller.clientHeight * 0.4;
      switch (action) {
        case 'down':
        case 'up':
          sound.move();
          return this.scroller.scrollBy({ top: action === 'down' ? step : -step, behavior: 'smooth' });
        case 'a':
          return openLink(item.link, 'news');
      }
    },
    hints() {
      const scrollable = this.scroller && this.scroller.scrollHeight > this.scroller.clientHeight + 4;
      return [...(scrollable ? [['↕', t('news.hint.scroll')]] : []), ['A', t('news.hint.browser')], ['B', t('news.hint.back')]];
    }
  };
}
