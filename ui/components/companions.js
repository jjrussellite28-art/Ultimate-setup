// Settings › Library › Companion apps: library apps that start next to a game (Wand, Discord, a monitoring tool). All with the controller.
// The page only picks a library app by its id and a few switches; the host checks everything again, at saving and at each start.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { move, pop, push, render } from '../core/nav.js';
import { sound } from '../sound.js';
import { chooseDialog, confirmDialog } from './overlay.js';
import { ensureParentGrant, parental } from './parental.js';
import { ensureUnlocked } from './passcode.js';
import { t } from '../core/i18n.js';
import {
  MAX_GAMES, MODE_ALL, MODE_GAMES, changeOf, draftOf, flagKeys, gameRows, problemKey, summary, toggleGame, wandOffer
} from '../core/companion-rules.js';

/** One list page: rows of { icon, label, detail, run, hint? }. */
function listPage(id, name, head, intro, buildRows) {
  return {
    id,
    name,
    index: 0,
    build(root) {
      this.rows = buildRows(this);
      this.index = Math.min(this.index, Math.max(0, this.rows.length - 1));
      this.items = this.rows.map((r) => el('li', { className: `check-row update-row${r.checked ? ' checked' : ''}` },
        el('span', { className: 'check-box' }, r.check === undefined ? iconEl(r.icon) : document.createTextNode(r.check ? '✓' : '')),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), r.detail ? el('small', { textContent: r.detail }) : null)));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: head }), intro ? el('p', { className: 'meta', textContent: intro }) : null),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      switch (action) {
        case 'up': return move(this, -1, this.rows.length, true);
        case 'down': return move(this, 1, this.rows.length, true);
        case 'a':
          sound.select();
          return this.rows[this.index]?.run();
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return [['A', this.rows?.[this.index]?.hint ?? t('companion.hint.open')], ['B', t('companion.hint.back')]];
    }
  };
}

/** The settings entry: needs the unlock, then the list of companion apps. */
export async function openCompanionApps() {
  if (!(await ensureUnlocked(t('companion.guard')))) return;
  const data = await attempt(() => api.getCompanions(), t('companion.loadFailed'));
  if (!data) return;
  push(companionListPage(data));
}

function companionListPage(initial) {
  let data = initial;
  const page = listPage('companions', t('companion.title'), t('companion.title'), t('companion.intro'), (self) => {
    const rows = [
      { icon: '+', label: t('companion.add'), detail: t('companion.addDetail'), run: () => addCompanion(data, (next) => { data = next; render(); }) }
    ];
    if (wandOffer(data.wand)) {
      rows.push({
        icon: data.wand.verified ? '✦' : '⚠',
        label: data.wand.verified ? t('companion.wand') : t('companion.wandUnverifiedRow'),
        detail: !data.wand.verified ? t('companion.wandUnverifiedDetail') : data.wand.pluginInstalled ? t('companion.wandPlugin') : t('companion.wandDetail'),
        run: () => setupWand(data.wand, (next) => { data = next; render(); })
      });
    }
    for (const rule of data.rules) {
      const sum = summary(rule);
      const flags = flagKeys(rule).map((k) => t(k)).join(' · ');
      const problem = problemKey(rule.problem);
      rows.push({
        icon: problem ? '⚠' : '▦',
        label: rule.name,
        detail: problem ? t(problem) : [t(sum.key, sum.params), flags].filter(Boolean).join(' · '),
        hint: t('companion.hint.edit'),
        run: () => push(companionRulePage(rule, data, (next) => { data = next; render(); }))
      });
    }
    if (!data.rules.length) rows.push({ icon: 'ℹ', label: t('companion.none'), detail: t('companion.noneDetail'), run: () => {} });
    return rows;
  });
  return page;
}

async function addCompanion(data, done) {
  if (data.rules.length >= data.max) return toast(t('companion.full', { max: data.max }));
  if (!data.candidates.length) return toast(t('companion.noCandidates'), 5000);
  const id = await chooseDialog(t('companion.pick'), data.candidates.map((c) => ({ label: c.name, icon: '▦', value: c.id })));
  if (!id) return;
  const app = data.candidates.find((c) => c.id === id);
  // A new rule exists only on the page until it is saved: the first save creates it.
  push(companionRulePage({ appId: id, name: app.name, mode: MODE_GAMES, games: [], closeWithGame: false, minimized: true, whenLocked: false, saved: false }, data, done));
}

async function setupWand(wand, done) {
  // The host names the path it found (the page never types one); it is always shown in full before anything happens.
  if (!wand.verified) {
    // No trusted signature by Wand or WeMod: at most the normal add-an-app route, with a warning, no companion rule.
    if (!(await confirmDialog(t('companion.wandTitle'), t('companion.wandAddAnyway'), { message: t('companion.wandUnverified', { path: wand.path ?? '' }), danger: true, focusCancel: true }))) return;
    if (parental.status?.enabled && !(await ensureParentGrant(t('companion.guardChange'), { action: 'companions', item: 'wand' }))) return;
    const added = await attempt(() => api.setupWand(false, true), t('companion.saveFailed'));
    if (!added) return;
    toast(t('companion.wandAdded'));
    done(added);
    return;
  }
  if (!(await confirmDialog(t('companion.wandTitle'), t('companion.wandContinue'), { message: t('companion.wandConfirm', { path: wand.path ?? '' }) }))) return;
  const choice = await chooseDialog(t('companion.wandTitle'), [
    { label: t('companion.wandAll'), detail: t('companion.wandAllDetail'), icon: '▶', value: 'all' },
    { label: t('companion.wandGuide'), detail: t('companion.wandGuideDetail'), icon: '≡', value: 'guide' }
  ]);
  if (!choice) return;
  if (parental.status?.enabled && !(await ensureParentGrant(t('companion.guardChange'), { action: 'companions', item: 'wand' }))) return;
  const next = await attempt(() => api.setupWand(choice === 'all', false), t('companion.saveFailed'));
  if (!next) return;
  toast(t('companion.wandDone'));
  done(next);
}

function companionRulePage(rule, data, done) {
  const draft = draftOf(rule);
  const dirty = () => rule.saved === false || !!changeOf(rule, draft);
  const page = listPage('companion-rule', rule.name, rule.name, t('companion.ruleIntro'), () => {
    const rows = [
      {
        icon: '▶',
        label: t('companion.when'),
        detail: draft.mode === MODE_ALL ? t('companion.when.all') : t('companion.when.games'),
        hint: t('companion.hint.change'),
        run: async () => {
          const mode = await chooseDialog(t('companion.when'), [
            { label: t('companion.when.all'), detail: t('companion.when.allDetail'), icon: '▶', value: MODE_ALL },
            { label: t('companion.when.games'), detail: t('companion.when.gamesDetail'), icon: '🎮', value: MODE_GAMES }
          ], draft.mode);
          if (mode) draft.mode = mode;
          render();
        }
      }
    ];
    if (draft.mode === MODE_GAMES) {
      rows.push({
        icon: '🎮',
        label: t('companion.games'),
        detail: draft.gameIds.length ? t('companion.sum.games', { count: draft.gameIds.length }) : t('companion.sum.none'),
        hint: t('companion.hint.change'),
        run: () => push(gamesPage(draft, data.games ?? []))
      });
    }
    rows.push(
      { check: draft.closeWithGame, label: t('companion.close'), detail: t('companion.closeDetail'), hint: t('companion.hint.toggle'), run: () => flip('closeWithGame') },
      { check: draft.minimized, label: t('companion.minimized'), detail: t('companion.minimizedDetail'), hint: t('companion.hint.toggle'), run: () => flip('minimized') },
      { check: draft.whenLocked, label: t('companion.locked'), detail: t('companion.lockedDetail'), hint: t('companion.hint.toggle'), run: () => flip('whenLocked') },
      { icon: '✓', label: t('companion.save'), detail: dirty() ? t('companion.saveDetail') : t('companion.saved'), hint: t('companion.hint.save'), run: save }
    );
    if (rule.saved !== false) rows.push({ icon: '✕', label: t('companion.remove'), detail: t('companion.removeDetail'), hint: t('companion.hint.remove'), run: remove });
    return rows;
  });
  const flip = (key) => {
    draft[key] = !draft[key];
    sound.tab();
    render();
  };
  async function save() {
    if (rule.saved !== false && !dirty()) return toast(t('companion.saved'));
    if (draft.mode === MODE_GAMES && !draft.gameIds.length && !(await confirmDialog(t('companion.noGamesConfirm'), t('companion.saveAnyway')))) return;
    if (parental.status?.enabled && !(await ensureParentGrant(t('companion.guardChange'), { action: 'companions', item: rule.appId }))) return;
    const change = rule.saved === false ? { ...draft } : changeOf(rule, draft);
    const next = await attempt(() => api.setCompanion(rule.appId, change), t('companion.saveFailed'));
    if (!next) return;
    toast(t('companion.savedToast', { name: rule.name }));
    done(next);
    pop();
  }
  async function remove() {
    if (!(await confirmDialog(t('companion.removeConfirm', { name: rule.name }), t('companion.removeButton'), { danger: true }))) return;
    const next = await attempt(() => api.removeCompanion(rule.appId), t('companion.saveFailed'));
    if (!next) return;
    done(next);
    pop();
  }
  // B with unsaved changes asks first; every other B closes the page as usual.
  page.captures = (action) => action === 'b' && dirty() && rule.saved !== false;
  const handle = page.handle.bind(page);
  page.handle = async (action) => {
    if (action === 'b') {
      if (await confirmDialog(t('companion.discard'), t('companion.discardButton'), { danger: true, focusCancel: true })) pop();
      return;
    }
    return handle(action);
  };
  return page;
}

function gamesPage(draft, games) {
  const page = listPage('companion-games', t('companion.games'), t('companion.games'), t('companion.gamesIntro', { max: MAX_GAMES }), () => {
    const rows = gameRows(games, draft.gameIds).map((g) => ({
      check: g.checked,
      checked: g.checked,
      label: g.name,
      hint: g.checked ? t('companion.hint.deselect') : t('companion.hint.select'),
      run: () => {
        draft.gameIds = toggleGame(draft.gameIds, g.id);
        sound.tab();
        render();
      }
    }));
    return rows.length ? rows : [{ icon: 'ℹ', label: t('companion.noGames'), run: () => {} }];
  });
  return page;
}
