// Volume per app: the running game first, then everything else that is making sound.

import { t } from '../core/i18n.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { sound } from '../sound.js';
import { quick } from '../components/quick-menu.js';

const POLL_MS = 4000;

export function appVolumePage() {
  return {
    id: 'app-volume',
    get name() { return t('appVolume.title'); },
    index: 0,
    apps: null,
    failed: false,
    timer: 0,
    // Changes go to Windows one after another, so a fast stick never overtakes itself.
    queue: Promise.resolve(),
    pending: 0,
    /** Quiet polls never toast; the first load and X do. Nothing is taken over while changes are still on their way. */
    async load(loud = false) {
      let apps;
      try {
        apps = await api.getAppVolumes();
        this.failed = false;
      } catch (err) {
        this.failed = true;
        if (loud) toast(t('appVolume.unavailable', { error: err?.message || err }), 5000);
      }
      if (this.pending > 0) return;
      if (!apps) {
        this.apps = this.apps ?? [];
        if (currentView() === this && this.items?.length !== this.apps.length) render();
        return;
      }
      const focusedId = this.apps?.[this.index]?.id;
      const same = this.apps && apps.length === this.apps.length && apps.every((a, i) => a.id === this.apps[i].id);
      this.apps = apps;
      // The focused app stays focused when others come or go.
      if (focusedId) {
        const at = apps.findIndex((a) => a.id === focusedId);
        if (at >= 0) this.index = at;
      }
      if (currentView() !== this) return;
      if (same) this.paint();
      else render();
    },
    poll() {
      clearTimeout(this.timer);
      this.timer = setTimeout(async () => {
        if (currentView() !== this) return; // closed: the loop ends, build() starts it again
        await this.load();
        this.poll();
      }, POLL_MS);
    },
    build(root) {
      if (this.apps === null) this.load(true);
      this.poll();
      const apps = this.apps ?? [];
      this.index = Math.max(0, Math.min(this.index, apps.length - 1));
      this.items = apps.map((a) => el('li', { className: 'check-row app-volume-row' },
        el('span', { className: 'check-box', textContent: a.isSystem ? '🔔' : a.isGame ? '🎮' : '♪' }),
        el('span', { className: 'check-main' },
          el('strong', { textContent: a.name }),
          a.isGame ? el('small', { textContent: t('appVolume.playing') }) : null),
        el('div', { className: 'quick-slider' },
          el('div', { className: 'quick-track' }, el('div', { className: 'quick-fill' })),
          el('span', { className: 'quick-level' }))));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      const empty = this.apps !== null && !apps.length;
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' },
          el('h2', { textContent: t('appVolume.title') }),
          el('p', { className: 'meta', textContent: this.apps === null ? t('appVolume.loading') : empty ? (this.failed ? t('appVolume.failed') : (quick.audio?.devices?.length ?? 1) === 0 ? t('appVolume.noOutput') : t('appVolume.silent')) : t('appVolume.help') })),
        empty || this.apps === null ? null : this.scroller));
      this.paint();
      this.update({ instant: true });
    },
    /** Fill and level without rebuilding the list. */
    paint() {
      (this.apps ?? []).forEach((a, i) => {
        const item = this.items?.[i];
        if (!item) return;
        const level = a.muted ? 0 : a.volume;
        item.querySelector('.quick-fill').style.width = `${level}%`;
        item.querySelector('.quick-level').textContent = a.muted ? t('appVolume.muted') : t('appVolume.percent', { value: a.volume });
        item.classList.toggle('dim', a.muted);
      });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    send(call) {
      this.pending++;
      this.queue = this.queue.then(async () => {
        const ok = (await attempt(call, t('appVolume.changeFailed'))) !== undefined;
        this.pending--;
        // The queue is empty again: show what Windows really has (also after a failure).
        if (this.pending === 0 && (!ok || currentView() === this)) this.load();
      });
      return this.queue;
    },
    async handle(action) {
      const apps = this.apps ?? [];
      switch (action) {
        case 'up': return apps.length && move(this, -1, apps.length, true);
        case 'down': return apps.length && move(this, 1, apps.length, true);
        case 'x': return this.load(true);
        case 'left':
        case 'right': {
          const app = apps[this.index];
          if (!app) return;
          const next = Math.max(0, Math.min(100, (app.muted ? 0 : app.volume) + (action === 'left' ? -5 : 5)));
          if (next === app.volume && !app.muted) return sound.bump();
          sound.move();
          app.volume = next;
          app.muted = false;
          this.paint();
          return this.send(() => api.setAppVolume(app.id, next));
        }
        case 'a': {
          const app = apps[this.index];
          if (!app) return;
          sound.select();
          app.muted = !app.muted;
          this.paint();
          if (app.muted) toast(t('appVolume.mutedToast', { name: app.name }));
          return this.send(() => api.setAppMute(app.id, app.muted));
        }
      }
    },
    pointer(node, index) {
      this.index = index;
      this.update();
    },
    hints() {
      return this.apps?.length ? [['◀▶', t('appVolume.hintVolume')], ['A', t('appVolume.hintMute')], ['X', t('appVolume.hintRefresh')], ['B', t('appVolume.hintBack')]] : [['X', t('appVolume.hintRefresh')], ['B', t('appVolume.hintBack')]];
    }
  };
}
