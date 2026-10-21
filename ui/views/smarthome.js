// Smart home tab: Home Assistant entities as cards; switch lights, run scenes,
// read sensors. Pinned entities also show up in the guide menu.

import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { setBackground } from '../core/art.js';
import { attempt, toast } from '../core/feedback.js';
import { move, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { addQuickRows, quick, renderQuick } from '../components/quick-menu.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';

const COLUMNS = 4;
const POLL_MS = 5000;

const ICONS = {
  scene: '🎬', script: '📜', light: '💡', switch: '🔌', fan: '🌀', input_boolean: '🔘', cover: '🪟', media_player: '🎵',
  climate: '🌡️', lock: '🔒', automation: '🤖', button: '⏺', input_button: '⏺', weather: '🌤️'
};
const SENSOR_ICONS = { temperature: '🌡️', humidity: '💧', power: '⚡', energy: '⚡', battery: '🔋', door: '🚪', window: '🪟', motion: '🏃', illuminance: '☀️' };

// i18n-used: smartHome.state.on smartHome.state.off smartHome.state.playing smartHome.state.paused smartHome.state.idle smartHome.state.open smartHome.state.closed smartHome.state.heat smartHome.state.cool smartHome.state.locked smartHome.state.unlocked smartHome.state.home smartHome.state.not_home smartHome.state.unknown
const KNOWN_STATES = ['on', 'off', 'playing', 'paused', 'idle', 'open', 'closed', 'heat', 'cool', 'locked', 'unlocked', 'home', 'not_home', 'unknown'];

export const iconFor = (e) => (e.domain === 'sensor' || e.domain === 'binary_sensor' ? SENSOR_ICONS[e.deviceClass] ?? '📟' : ICONS[e.domain] ?? '•');

export function stateLabel(e) {
  if (e.domain === 'sensor') return `${e.state}${e.unit ? ` ${e.unit}` : ''}`;
  if (e.domain === 'scene' || e.domain === 'script' || e.domain === 'button') return t('smartHome.run');
  if (e.state === 'scening') return '';
  if (!KNOWN_STATES.includes(e.state)) return e.state;
  const key = `smartHome.state.${e.state}`;
  return t(key);
}

const isOn = (e) => ['on', 'playing', 'open', 'unlocked', 'heat', 'cool'].includes(e.state);

let entities = [];
let pinned = new Set();
let pollTimer;

async function load() {
  const list = await attempt(() => api.getHomeEntities(), t('smartHome.unreachable'));
  if (list) entities = list;
  return !!list;
}

async function activate(entity) {
  sound.select();
  if ((await attempt(() => api.activateHomeEntity(entity.id), t('smartHome.switchFailed'))) === undefined) return;
  if (entity.domain === 'scene' || entity.domain === 'script') toast(t('smartHome.started', { name: entity.name }));
  // Home Assistant needs a moment until the new state shows up.
  setTimeout(async () => {
    await load();
    if (quick.open) renderQuick();
    else if (state.stack.length === 0 && smartHomeView.active) smartHomeView.refreshCards();
  }, 350);
}

// Pinned entities in the guide menu.
addQuickRows(() => entities
  .filter((e) => pinned.has(e.id))
  .map((e) => ({
    icon: iconFor(e),
    label: e.name,
    detail: e.controllable ? stateLabel(e) : stateLabel(e),
    keepOpen: true,
    run: () => (e.controllable ? activate(e) : null)
  })));

/** Loads pinned entities for the guide menu without opening the tab. */
export async function preloadSmartHome(settings) {
  pinned = new Set(settings?.homeAssistantPinned ?? []);
  if (state.info.homeAssistant) await load();
}

export const smartHomeView = {
  get name() { return t('smartHome.title'); },
  id: 'smarthome',
  index: 0,
  active: false,
  onEnter() {
    this.index = 0;
  },
  build(root) {
    this.active = true;
    clearInterval(pollTimer);
    pollTimer = setInterval(async () => {
      if (!this.root?.isConnected) {
        this.active = false;
        return clearInterval(pollTimer);
      }
      if (await load()) this.refreshCards();
    }, POLL_MS);

    this.root = el('div', { className: 'smarthome' });
    root.append(this.root);
    setBackground({ id: '__smarthome', name: t('smartHome.title'), art: {} });
    if (!entities.length) {
      this.root.append(el('p', { className: 'empty-note', textContent: t('smartHome.connecting') }));
      load().then(() => state.stack.length === 0 && this.active && render());
      return;
    }
    this.cards = entities.map((e) => el('div', { className: `ha-card ha-${e.domain}${isOn(e) ? ' on' : ''}` },
      el('span', { className: 'ha-icon', textContent: iconFor(e) }),
      el('strong', { className: 'ha-name', textContent: e.name }),
      el('span', { className: 'ha-state', textContent: stateLabel(e) }),
      pinned.has(e.id) ? el('span', { className: 'fav', textContent: '★' }) : null));
    this.index = Math.min(this.index, this.cards.length - 1);
    this.scroller = el('div', { className: 'ha-grid list-scroll' }, this.cards);
    this.root.append(el('div', { className: 'grid-title' }, el('h2', { textContent: t('smartHome.title') }), el('span', { className: 'meta', textContent: t('smartHome.count', { count: entities.length }) })), this.scroller);
    this.update({ instant: true });
  },
  /** Live updates without rebuilding (keeps scroll position and animations). */
  refreshCards() {
    if (!this.cards || this.cards.length !== entities.length) return render();
    entities.forEach((e, i) => {
      const card = this.cards[i];
      card.classList.toggle('on', isOn(e));
      card.querySelector('.ha-state').textContent = stateLabel(e);
    });
  },
  update({ instant = false } = {}) {
    if (!this.cards) return;
    setFocused(this.cards, this.index);
    keepInView(this.scroller, this.cards[this.index], { instant });
  },
  activate() {
    const e = entities[this.index];
    if (!e) return;
    if (!e.controllable) return sound.bump();
    return activate(e);
  },
  async handle(action) {
    const count = entities.length;
    switch (action) {
      case 'left': return move(this, -1, count);
      case 'right': return move(this, 1, count);
      case 'up': return move(this, -COLUMNS, count, true);
      case 'down': return move(this, COLUMNS, count, true);
      case 'a': return this.activate();
      case 'y': {
        const e = entities[this.index];
        const on = await attempt(() => api.pinHomeEntity(e.id));
        if (on === undefined) return;
        if (on) pinned.add(e.id);
        else pinned.delete(e.id);
        toast(on ? t('smartHome.pinned', { name: e.name }) : t('smartHome.unpinned', { name: e.name }));
        return render();
      }
    }
  },
  hints() {
    return [['A', t('smartHome.hint.switch')], ['Y', t('smartHome.hint.pin')], ['B', t('smartHome.hint.home')]];
  }
};
