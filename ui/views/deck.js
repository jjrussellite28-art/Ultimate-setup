// Handheld-style extras: per-game profiles, night mode, Steam downloads, capture actions.

import { t } from '../core/i18n.js';
import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, formatSize, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, push, render } from '../core/nav.js';
import { chooseDialog } from '../components/overlay.js';
import { detailPage } from './detail.js';
import { sound } from '../sound.js';
import { state } from '../core/state.js';
import { updateCount } from './updates.js';

// ---------------------------------------------------------------------------
// Game profiles: applied when the game starts, undone when it ends.

const KEEP = '__keep';

export function profilePage(item) {
  let data = null;
  const save = async (changes) => {
    const next = await attempt(() => api.setProfile(item.id, { ...data.profile, ...changes }), t('deck.profileSaveFailed'));
    if (next) data = next;
    render();
  };
  return listPage({
    id: 'profile',
    title: t('deck.profileTitle', { name: item.name }),
    intro: t('deck.profileIntro'),
    rows(page) {
      if (!data) {
        attempt(() => api.getProfile(item.id), t('deck.profileLoadFailed')).then((d) => {
          data = d ?? { profile: {}, modes: [], devices: [] };
          if (currentView() === page) render();
        });
        return null;
      }
      const p = data.profile;
      const resolution = p.width ? `${p.width} × ${p.height}${p.refreshRate ? ` · ${p.refreshRate} Hz` : ''}` : t('deck.asSet');
      const device = data.devices.find((d) => d.id === p.audioDevice);
      return [
        {
          icon: '🖵',
          label: t('deck.resolution'),
          value: resolution,
          run: async () => {
            const options = [{ label: t('deck.asSet'), value: KEEP }, ...data.modes.map((m) => ({ label: `${m.width} × ${m.height} · ${m.hz} Hz`, value: m }))];
            const mode = await chooseDialog(t('deck.resolutionTitle'), options);
            if (mode === undefined) return;
            await save(mode === KEEP ? { width: null, height: null, refreshRate: null } : { width: mode.width, height: mode.height, refreshRate: mode.hz });
          }
        },
        ...(data.hdrSupported ? [{
          icon: '◐',
          label: 'HDR',
          value: p.hdr == null ? t('deck.asSet') : p.hdr ? t('deck.on') : t('deck.off'),
          run: async () => {
            const hdr = await chooseDialog(t('deck.hdrTitle'), [{ label: t('deck.asSet'), value: KEEP }, { label: t('deck.on'), value: true }, { label: t('deck.off'), value: false }]);
            if (hdr !== undefined) await save({ hdr: hdr === KEEP ? null : hdr });
          }
        }] : []),
        {
          icon: '🔊',
          label: t('deck.audioOutput'),
          value: device?.name ?? t('deck.asSet'),
          run: async () => {
            const id = await chooseDialog(t('deck.audioTitle'), [{ label: t('deck.asSet'), value: KEEP }, ...data.devices.map((d) => ({ label: d.name, value: d.id }))]);
            if (id !== undefined) await save({ audioDevice: id === KEEP ? null : id });
          }
        },
        {
          icon: '☾',
          label: t('deck.nightMode'),
          value: p.nightMode === false ? t('deck.offInGame') : t('deck.asSet'),
          run: () => save({ nightMode: p.nightMode === false ? null : false })
        }
      ];
    }
  });
}

detailPage.extraActions.push((item) => (item.type === 'game' ? { label: t('deck.gameProfile'), icon: '⚙', run: () => push(profilePage(item)) } : null));

// ---------------------------------------------------------------------------
// Night mode settings rows

const nightModes = () => [
  { label: t('deck.off'), value: 'off' },
  { label: t('deck.on'), value: 'on' },
  { label: t('deck.evening'), value: 'evening' }
];

export function nightModeItems(settings, change) {
  const s = settings ?? {};
  const NIGHT_MODES = nightModes();
  return [
    {
      kind: 'choice',
      label: t('deck.nightMode'),
      desc: t('deck.nightModeDesc'),
      value: () => NIGHT_MODES.find((m) => m.value === (s.nightMode ?? 'off'))?.label ?? t('deck.off'),
      run: async () => {
        const mode = await chooseDialog(t('deck.nightMode'), NIGHT_MODES, s.nightMode ?? 'off');
        if (mode !== undefined) await change('nightMode', mode);
      }
    },
    {
      kind: 'choice',
      label: t('deck.strength'),
      desc: t('deck.strengthDesc'),
      value: () => t('deck.percent', { value: s.nightModeStrength ?? 50 }),
      run: async () => {
        const value = await chooseDialog(t('deck.strengthTitle'), [25, 50, 75, 100].map((v) => ({ label: t('deck.percent', { value: v }), value: v })), s.nightModeStrength ?? 50);
        if (value !== undefined) await change('nightModeStrength', value);
      }
    }
  ];
}

// ---------------------------------------------------------------------------
// Steam downloads

const downloadState = (state) => (state === 'downloading' ? t('deck.stateDownloading') : state === 'paused' ? t('deck.statePaused') : state === 'queued' ? t('deck.stateQueued') : state);

export function downloadsPage() {
  let data = null;
  let loading = false;
  const load = (self) => {
    if (loading) return;
    loading = true;
    attempt(() => api.getInstallers(), t('deck.downloadsLoadFailed')).then((d) => {
      loading = false;
      data = d ?? data ?? [];
      if (currentView() === self) render();
    });
  };
  const open = (kind) => async () => {
    if ((await attempt(() => api.openSystemUpdates(kind), t('deck.updatesFailed'))) !== undefined) toast(t(kind === 'windows' ? 'deck.windowsUpdate' : 'deck.appUpdates'), 3500);
  };
  return listPage({
    id: 'downloads',
    title: t('deck.downloads'),
    intro: t('deck.downloadsIntro'),
    rows(self) {
      if (!data) {
        load(self);
        return null;
      }
      return [
        { icon: '⟳', label: t('deck.windowsUpdate'), detail: t('deck.windowsUpdateDetail'), value: t('deck.open'), run: open('windows') },
        { icon: '⬆', label: t('deck.appUpdates'), detail: t('deck.appUpdatesDetail'), value: t('deck.open'), run: open('apps') },
        ...data.map((f) => ({
          icon: '⬇',
          label: f.name,
          detail: formatSize(f.bytes) ?? '',
          value: t('deck.run'),
          run: async () => {
            if ((await attempt(() => api.openInstaller(f.path), t('deck.installerFailed'))) !== undefined) toast(f.name, 3500);
          }
        })),
        ...(data.length ? [] : [{ icon: '✓', label: t('deck.noInstallers'), detail: t('deck.noInstallersDetail') }])
      ];
    }
  });
}

// ---------------------------------------------------------------------------
// Health check: is the PC (and its fans and lights, with CaseHUD) doing fine?

export function healthPage() {
  let data = null;
  let loading = false;
  let timer;
  const quiet = (fn) => Promise.resolve().then(fn).catch(() => null);
  const load = (self) => {
    if (loading) return;
    loading = true;
    Promise.all([quiet(() => api.getPerformance()), quiet(() => api.getPads()), quiet(() => api.getServiceStatus()), quiet(() => api.getDrives())]).then(([perf, pads, service, drives]) => {
      loading = false;
      data = { perf, pads, service, drives };
      if (currentView() === self) render();
    });
  };
  const ok = (label, detail) => ({ icon: '✓', label, detail });
  const warn = (label, detail) => ({ icon: '⚠', label, detail, highlight: true });
  const note = (label, detail) => ({ icon: 'ℹ', label, detail });
  return listPage({
    id: 'health',
    title: t('health.title'),
    intro: t('health.intro'),
    rows(self) {
      if (!data) {
        load(self);
        return null;
      }
      clearTimeout(timer);
      timer = setTimeout(() => currentView() === self && load(self), 5000);
      const { perf, pads, service, drives } = data;
      const rows = [];
      if (perf) {
        rows.push(perf.cpu >= 90 ? warn(t('health.cpu'), t('health.busy', { n: perf.cpu })) : ok(t('health.cpu'), t('health.load', { n: perf.cpu })));
        if (perf.gpu != null) rows.push(perf.gpu >= 98 ? warn(t('health.gpu'), t('health.busy', { n: perf.gpu })) : ok(t('health.gpu'), t('health.load', { n: perf.gpu })));
        const pct = perf.ramTotalGb ? (perf.ramUsedGb / perf.ramTotalGb) * 100 : 0;
        const mem = t('health.ramDetail', { used: perf.ramUsedGb.toFixed(1), total: perf.ramTotalGb.toFixed(1) });
        rows.push(pct >= 90 ? warn(t('health.ram'), mem) : ok(t('health.ram'), mem));
      }
      for (const d of drives?.drives ?? []) {
        const free = d.totalBytes ? d.freeBytes / d.totalBytes : 1;
        const text = t('health.driveFree', { name: d.name, free: formatSize(d.freeBytes) ?? '?', total: formatSize(d.totalBytes) ?? '?' });
        rows.push(free < 0.1 ? warn(t('health.drive'), text) : ok(t('health.drive'), text));
      }
      const disks = drives?.disks ?? [];
      if (disks.length) {
        const bad = disks.filter((x) => x.health !== 'Healthy');
        rows.push(bad.length ? warn(t('health.disks'), t('health.disksBad', { names: bad.map((x) => x.name).join(', ') })) : ok(t('health.disks'), t('health.disksOk')));
      }
      const waiting = updateCount();
      if (!state.updates) rows.push(note(t('health.updates'), t('health.updatesUnknown')));
      else rows.push(waiting ? warn(t('health.updates'), t('health.updatesWaiting', { count: waiting })) : ok(t('health.updates'), t('health.updatesNone')));
      if (Array.isArray(pads)) rows.push(pads.length ? ok(t('health.pad'), t('health.padCount', { count: pads.length })) : warn(t('health.pad'), t('health.padNone')));
      rows.push(navigator.onLine ? ok(t('health.net'), t('health.netOk')) : warn(t('health.net'), t('health.netOff')));
      if (service && service.state && service.state !== 'notInstalled') {
        rows.push(service.healthy === false || service.problem ? warn(t('health.service'), t('health.serviceBad')) : ok(t('health.service'), t('health.serviceOk')));
      }
      const c = state.caseHud;
      if (c) {
        if (!c.installed) rows.push(note(t('health.case'), t('health.caseNone')));
        else rows.push({ ...(c.running ? ok(t('health.case'), t('health.caseOk')) : warn(t('health.case'), t('health.caseStopped'))), run: () => import('./casehud.js').then((m) => push(m.caseHudPage())) });
      }
      return rows;
    }
  });
}

// ---------------------------------------------------------------------------
// Capture from the guide menu (through the Game Bar)

export async function capture(kind) {
  const labels = { screenshot: t('deck.captureScreenshot'), clip: t('deck.captureClip'), record: t('deck.captureRecord') };
  if ((await attempt(() => api.capture(kind), t('deck.captureFailed'))) !== undefined) toast(labels[kind], 3500);
}

// ---------------------------------------------------------------------------

/** Simple list page with optional progress bars. */
function listPage({ id, title, intro, rows }) {
  return {
    id,
    name: title,
    index: 0,
    build(root) {
      this.rows = rows(this);
      if (!this.rows) {
        root.append(el('p', { className: 'empty-note', textContent: t('deck.loading') }));
        this.items = [];
        return;
      }
      this.index = Math.min(this.index, Math.max(0, this.rows.length - 1));
      this.items = this.rows.map((r) => el('li', { className: `check-row update-row${r.highlight ? ' highlight' : ''}` },
        el('span', { className: 'check-box' }, iconEl(r.icon)),
        el('span', { className: 'check-main' },
          el('strong', { textContent: r.label }),
          r.detail ? el('small', { textContent: r.detail }) : null,
          r.progress != null ? el('span', { className: 'progress' }, el('i', { style: { width: `${Math.round(r.progress * 100)}%` } })) : null),
        r.value != null ? el('span', { className: 'setting-value', textContent: `${r.value} ›` }) : null));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: title }), intro ? el('p', { className: 'meta', textContent: intro }) : null),
        this.scroller));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      const count = this.rows?.length ?? 0;
      switch (action) {
        case 'up': return move(this, -1, count, true);
        case 'down': return move(this, 1, count, true);
        case 'a': {
          const r = this.rows?.[this.index];
          if (!r?.run) return sound.bump();
          sound.select();
          return r.run();
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return this.rows?.some((r) => r.run) ? [['A', t('deck.hintChange')], ['B', t('deck.hintBack')]] : [['B', t('deck.hintBack')]];
    }
  };
}
