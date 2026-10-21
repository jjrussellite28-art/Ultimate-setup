// Autostart profiles, services to pause on the couch, and a small task manager.


import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, render } from '../core/nav.js';
import { confirmDialog } from '../components/overlay.js';
import { sound } from '../sound.js';
import { ensureUnlocked } from '../components/passcode.js';
import { t } from '../core/i18n.js';
// i18n-used: systemTools.services.detail systemTools.services.detailSuggested

/** Shared list page: rows from `load`, A runs the row, optional X/Y actions. */
function listPage({ id, title, intro, load, row, onA, onX, onY, empty, extra = [], hints }) {
  return {
    id,
    name: title,
    index: 0,
    data: null,
    async reload() {
      this.data = (await attempt(load, t('systemTools.notLoaded', { title }))) ?? this.data ?? [];
      if (currentView() === this) render();
    },
    build(root) {
      if (this.data === null) {
        root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' }, el('h2', { textContent: title })), el('p', { className: 'empty-note', textContent: t('systemTools.loading') })));
        this.items = [];
        this.reload();
        return;
      }
      const rows = [...extra.filter((e) => !e.show || e.show(this.data)).map((e) => ({ ...e, extra: true })), ...(Array.isArray(this.data) ? this.data : this.data.rows ?? [])];
      this.rows = rows;
      this.index = Math.min(this.index, Math.max(0, rows.length - 1));
      this.items = rows.map((r) => {
        const view = r.extra ? { icon: r.icon, label: r.label(this.data), detail: r.detail?.(this.data), highlight: true } : row(r);
        return el('li', { className: `check-row update-row${view.highlight ? ' highlight' : ''}${view.checked ? ' checked' : ''}${view.dim ? ' dim' : ''}` },
          el('span', { className: 'check-box' }, iconEl(view.icon)),
          el('span', { className: 'check-main' }, el('strong', { textContent: view.label }), view.detail ? el('small', { textContent: view.detail }) : null),
          view.pill ? el('span', { className: `pill ${view.pillOn ? 'on' : 'off'}`, textContent: view.pill }) : null);
      });
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: title }), intro ? el('p', { className: 'meta', textContent: intro }) : null),
        rows.length || !empty ? this.scroller : el('p', { className: 'empty-note', textContent: empty })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      const r = this.rows?.[this.index];
      switch (action) {
        case 'up': return move(this, -1, this.rows?.length ?? 0, true);
        case 'down': return move(this, 1, this.rows?.length ?? 0, true);
        case 'x':
          if (onX && r && !r.extra) {
            sound.open();
            return onX(r, this);
          }
          sound.select();
          this.data = null;
          return render();
        case 'y':
          if (!onY || !r || r.extra) return;
          sound.open();
          return onY(r, this);
        case 'a':
          if (!r) return sound.bump();
          sound.select();
          if (r.extra) await r.run(this);
          else await onA(r, this);
          return;
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return hints ?? [['A', t('systemTools.hint.change')], ['X', t('systemTools.hint.reload')], ['B', t('systemTools.hint.back')]];
    }
  };
}

// ---------------------------------------------------------------------------
// Autostart

export function startupPage() {
  return listPage({
    id: 'startup',
    title: t('systemTools.startup.title'),
    intro: t('systemTools.startup.intro'),
    load: () => api.getStartup(),
    extra: [{
      icon: '✦',
      label: () => t('systemTools.startup.apply'),
      detail: (data) => t('systemTools.startup.applyDetail', { count: (data ?? []).filter((e) => e.suggested === 'pc').length }),
      run: async (page) => {
        if (!(await confirmDialog(t('systemTools.startup.applyQuestion'), t('systemTools.startup.applyConfirm')))) return;
        const data = await attempt(() => api.applyStartupSuggestions(), t('systemTools.notSaved'));
        if (data) {
          page.data = data;
          toast(t('systemTools.startup.adjusted'));
          render();
        }
      }
    }],
    row: (e) => ({
      icon: e.disabled ? '⦸' : e.mode === 'pc' ? '🖥' : '🎮',
      label: e.name,
      detail: e.disabled ? t('systemTools.startup.disabledInWindows', { source: e.source }) : e.suggested !== e.mode ? t('systemTools.startup.suggestion', { source: e.source, mode: e.suggested === 'pc' ? t('systemTools.startup.pcMode') : t('systemTools.startup.couch') }) : e.source,
      pill: e.disabled ? t('systemTools.startup.off') : e.mode === 'pc' ? t('systemTools.startup.pcMode') : t('systemTools.startup.couch'),
      pillOn: !e.disabled && e.mode !== 'pc',
      dim: e.disabled
    }),
    onA: async (e, page) => {
      if (e.disabled) return toast(t('systemTools.startup.disabledInTaskManager'));
      const data = await attempt(() => api.setStartupMode(e.id, e.mode === 'pc' ? 'couch' : 'pc'), t('systemTools.notSaved'));
      if (data) {
        page.data = data;
        render();
      }
    }
  });
}

// ---------------------------------------------------------------------------
// Services

/**
 * "Windows restrictions stuck": our values in the Windows policies with no lock, no helper state and no task to explain them (a way
 * back that deleted the tasks too early). One place (Settings > Console, in both modes); true when the cleanup ran.
 */
export async function cleanupStuckPoliciesDialog() {
  if (!(await confirmDialog(t('systemTools.services.stuckQuestion'), t('systemTools.services.stuckConfirm')))) return false;
  toast(t('systemTools.services.confirmPrompt'), 4000);
  const data = await attempt(() => api.cleanupStuckPolicies(), t('systemTools.services.setupFailed'));
  if (!data) return false;
  toast(t('systemTools.services.stuckDone'));
  return true;
}

export function servicesPage() {
  return listPage({
    id: 'services',
    title: t('systemTools.services.title'),
    intro: t('systemTools.services.intro'),
    load: async () => {
      const r = await api.getServices();
      return { ...r, rows: r.services };
    },
    extra: [{
      icon: '🧹',
      // The way back in every mode: what the setup created goes, whatever the integration level is.
      show: (data) => !!data?.leftovers,
      label: () => t('systemTools.services.cleanTitle'),
      detail: () => t('systemTools.services.cleanDetail'),
      run: async (page) => {
        if (!(await confirmDialog(t('systemTools.services.cleanQuestion'), t('systemTools.services.cleanConfirm')))) return;
        const data = await attempt(() => api.setupServiceTask(false), t('systemTools.services.setupFailed'));
        if (data) {
          page.data = { ...data, rows: data.services };
          toast(t('systemTools.services.cleaned'));
          render();
        }
      }
    }, {
      icon: '🛡',
      label: (data) => (data?.legacyTask
        ? t('systemTools.services.legacyTitle')
        : !data?.taskInstalled
          ? t('systemTools.services.setupTitle')
          : data?.pending
            ? t('systemTools.services.applyTitle')
            : t('systemTools.services.doneTitle')),
      detail: (data) => (data?.protectedFolder === false
        ? t('systemTools.services.protectedFolder')
        : data?.legacyTask
          ? t('systemTools.services.legacyDetail')
          : data?.taskInstalled && !data?.pending
            ? t('systemTools.services.doneDetail')
            : t('systemTools.services.setupDetail')),
      run: async (page) => {
        if (page.data?.taskInstalled && !page.data?.pending && !page.data?.legacyTask) return toast(t('systemTools.services.alreadySetUp'));
        const data = await attempt(() => api.setupServiceTask(), t('systemTools.services.setupFailed'));
        if (data) {
          page.data = { ...data, rows: data.services };
          toast(t('systemTools.services.setUp'));
          render();
        }
      }
    }],
    row: (s) => ({
      icon: s.selected ? '✓' : '',
      checked: s.selected,
      label: s.displayName,
      detail: t(s.suggested ? 'systemTools.services.detailSuggested' : 'systemTools.services.detail', { name: s.name, status: s.status === 'running' ? t('systemTools.services.running') : t('systemTools.services.stopped') })
    }),
    onA: async (s, page) => {
      const selected = await attempt(() => api.toggleCouchService(s.name), t('systemTools.notSaved'));
      if (selected === undefined) return;
      s.selected = selected;
      page.data.pending = true;
      toast(t('systemTools.services.chooseApply'), 4000);
      render();
    },
    hints: [['A', t('systemTools.services.hintToggle')], ['X', t('systemTools.hint.reload')], ['B', t('systemTools.hint.back')]]
  });
}

// ---------------------------------------------------------------------------
// Game clients

export function launchersPage() {
  return listPage({
    id: 'launchers',
    title: t('systemTools.launchers.title'),
    intro: t('systemTools.launchers.intro'),
    load: () => api.getLaunchers(),
    row: (l) => ({
      icon: l.mode === 'standby' ? '✓' : '',
      checked: l.mode === 'standby',
      label: l.name,
      detail: t('systemTools.launchers.detail', { state: { standby: t('systemTools.launchers.mode.standby'), demand: t('systemTools.launchers.mode.demand'), off: t('systemTools.launchers.mode.off') }[l.mode], exe: l.exe }),
      pill: l.running ? t('systemTools.launchers.running') : t('systemTools.launchers.notStarted'),
      pillOn: l.running
    }),
    onA: async (l, page) => {
      // Standby, then on demand, then off, then standby again: one setting per client.
      const next = { standby: 'demand', demand: 'off', off: 'standby' }[l.mode] ?? 'standby';
      const list = await attempt(() => api.setLauncherMode(l.id, next), t('systemTools.notSaved'));
      if (!list) return;
      page.data = list;
      render();
    },
    onY: async (l) => {
      if (!(await ensureUnlocked(t('systemTools.launchers.open', { name: l.name })))) return;
      await attempt(() => api.openLauncher(l.id), t('systemTools.launchers.openFailed', { name: l.name }));
    },
    empty: t('systemTools.launchers.empty'),
    hints: [['A', t('systemTools.launchers.hintMode')], ['Y', t('systemTools.launchers.hintOpen')], ['X', t('systemTools.hint.reload')], ['B', t('systemTools.hint.back')]]
  });
}

// ---------------------------------------------------------------------------
// Task manager

const nameOf = (p) => p.displayName || p.title || p.name;

export function tasksPage() {
  return listPage({
    id: 'tasks',
    title: t('systemTools.tasks.title'),
    intro: t('systemTools.tasks.intro'),
    load: () => api.getProcesses(),
    row: (p) => ({
      icon: p.isGame ? '🎮' : p.hasWindow ? '▣' : '·',
      label: p.title && p.title !== nameOf(p) ? `${nameOf(p)} – ${p.title}` : nameOf(p),
      detail: [formatSize(p.memoryBytes), p.count > 1 ? t('systemTools.tasks.processes', { count: p.count }) : null, p.isGame ? t('systemTools.tasks.runningGame') : null, p.hasWindow ? null : t('systemTools.tasks.background')].filter(Boolean).join(' · '),
      highlight: p.isGame
    }),
    onA: async (p) => {
      if (!p.hasWindow) return toast(t('systemTools.tasks.noWindow'));
      await attempt(() => api.switchProcess(p.name), t('systemTools.tasks.switchFailed'));
    },
    onX: async (p, page) => {
      if (!(await confirmDialog(t('systemTools.tasks.endQuestion', { name: nameOf(p) }), t('systemTools.tasks.end'), { danger: true }))) return;
      // Politely first (the program saves and closes by itself); only if it does not react, ask whether to force it.
      let left = await attempt(() => api.killProcess(p.name, false), t('systemTools.tasks.endFailed'));
      if (left === undefined) return;
      if (left > 0) {
        if (!(await confirmDialog(t('systemTools.tasks.forceQuestion', { name: nameOf(p) }), t('systemTools.tasks.force'), { danger: true }))) {
          page.data = null;
          return render();
        }
        left = await attempt(() => api.killProcess(p.name, true), t('systemTools.tasks.endFailed'));
        if (left === undefined) return;
      }
      toast(t('systemTools.tasks.ended', { name: nameOf(p) }));
      page.data = null;
      render();
    },
    onY: (p, page) => {
      page.data = null;
      render();
    },
    hints: [['A', t('systemTools.tasks.hintSwitch')], ['X', t('systemTools.tasks.end')], ['Y', t('systemTools.hint.reload')], ['B', t('systemTools.hint.back')]]
  });
}
