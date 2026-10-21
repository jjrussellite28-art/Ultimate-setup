// Plugins and skins: what is installed, settings and pages of each plugin
// (rendered from the plugin's declarative description), install from .zip or from the catalog.

import { iconEl } from '../core/glyph-icons.js';
import { api } from '../core/api.js';
import { el, keepInView, setFocused } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { currentView, move, push, render } from '../core/nav.js';
import { chooseDialog, confirmDialog } from '../components/overlay.js';
import { ensureUnlocked } from '../components/passcode.js';
import { openKeyboard } from '../components/keyboard.js';
import { openFileBrowser } from '../components/file-browser.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t, fmtDateTime } from '../core/i18n.js';

/** Generic list page: rows of { icon, label, detail, value, on, run }. */
function rowsPage({ id, title, intro, rows, hints, tabs, onKey }) {
  return {
    id,
    name: title,
    index: 0,
    tab: 0,
    /** With tabs, LB/RB switch them here instead of the main tabs underneath. */
    captures: tabs ? (action) => action === 'lb' || action === 'rb' : undefined,
    build(root) {
      this.rows = rows(this);
      if (!this.rows) {
        if (tabs) root.append(el('div', { className: 'files' }, el('div', { className: 'files-head' }, el('h2', { textContent: typeof title === 'function' ? title(this) : title })),
          el('div', { className: 'tab-strip' }, tabs.map((tb, i) => el('span', { className: `tab-chip${i === this.tab ? ' active' : ''}`, textContent: typeof tb === 'function' ? tb(this) : tb })))));
        root.append(el('p', { className: 'empty-note', textContent: t('plugins.loading') }));
        this.items = [];
        return;
      }
      this.index = Math.min(this.index, Math.max(0, this.rows.length - 1));
      this.items = this.rows.map((r) => el('li', { className: `check-row update-row${r.highlight ? ' highlight' : ''}${r.dim ? ' dim' : ''}` },
        el('span', { className: 'check-box' }, iconEl(r.icon ?? '◆')),
        el('span', { className: 'check-main' }, el('strong', { textContent: r.label }), r.detail ? el('small', { textContent: r.detail }) : null),
        r.value != null ? el('span', { className: `pill ${r.on ? 'on' : 'off'}`, textContent: r.value }) : null));
      this.scroller = el('ul', { className: 'check-list list-scroll' }, this.items);
      root.append(el('div', { className: 'files' },
        el('div', { className: 'files-head' }, el('h2', { textContent: typeof title === 'function' ? title(this) : title }), intro ? el('p', { className: 'meta', textContent: typeof intro === 'function' ? intro(this) : intro }) : null),
        tabs ? el('div', { className: 'tab-strip' }, tabs.map((tb, i) => el('span', { className: `tab-chip${i === this.tab ? ' active' : ''}`, textContent: typeof tb === 'function' ? tb(this) : tb }))) : null,
        this.rows.length ? this.scroller : el('p', { className: 'empty-note', textContent: t('plugins.empty') })));
      this.update({ instant: true });
    },
    update({ instant = false } = {}) {
      if (!this.items?.length) return;
      setFocused(this.items, this.index);
      keepInView(this.scroller, this.items[this.index], { instant });
    },
    async handle(action) {
      const count = this.rows?.length ?? 0;
      if (tabs && (action === 'lb' || action === 'rb' || action === 'left' || action === 'right')) {
        const next = this.tab + (action === 'lb' || action === 'left' ? -1 : 1);
        if (next < 0 || next >= tabs.length) return sound.bump();
        this.tab = next;
        this.index = 0;
        sound.tab?.();
        return render();
      }
      if (onKey && (await onKey(this, action)) === true) return;
      switch (action) {
        case 'up': return move(this, -1, count, true);
        case 'down': return move(this, 1, count, true);
        case 'a': {
          const r = this.rows?.[this.index];
          if (!r?.run) return sound.bump();
          sound.select();
          return r.run(this);
        }
      }
    },
    pointer(node, index, dispatch) {
      this.index = index;
      this.update();
      dispatch('a');
    },
    hints() {
      return (typeof hints === 'function' ? hints(this) : hints) ?? [['A', t('plugins.hint.select')], ['B', t('plugins.hint.back')]];
    }
  };
}

async function installZip(kind) {
  const path = await openFileBrowser({ title: kind === 'skin' ? t('plugins.zip.chooseSkin') : t('plugins.zip.choosePlugin'), extensions: ['.zip'] });
  if (!path) return;
  // A skin is a stylesheet, not a program, but it can still load pictures from the internet and so tell a server when you are on the couch.
  if (!(await confirmDialog(kind === 'plugin' ? t('plugins.zip.warning') : t('plugins.zip.skinWarning'), t('plugins.install'), { danger: kind === 'plugin' }))) return;
  const name = await attempt(() => api.installPackage(path, kind), t('plugins.installFailed'));
  if (!name) return;
  toast(kind === 'skin' ? t('plugins.zip.skinInstalled', { name }) : t('plugins.zip.pluginInstalled', { name }), 5000);
}

/** The host sends a code for network errors; the UI words them. */
const catalogError = (c) => (c.errorCode === 'notFound' ? t('plugins.catalog.notFound') : c.errorCode === 'unreachable' ? t('plugins.catalog.unreachable') : c.error ?? '');

const STATUS = () => ({
  update: { icon: '⬆', value: t('plugins.status.update'), on: true },
  installed: { icon: '✓', value: t('plugins.status.installed'), on: false },
  notInstalled: { icon: '⬇', value: t('plugins.status.new'), on: false },
  unavailable: { icon: '⛔', value: t('plugins.status.unavailable'), on: false }
});

/** Update row of the plugin list: the catalog says a newer version exists. */
const updateFor = (p) => (state.catalogUpdates ?? []).find((u) => u.kind === 'plugin' && (u.id.toLowerCase() === p.id.toLowerCase() || u.id.toLowerCase() === p.folder.toLowerCase()));

/**
 * Installs (or updates) a catalog entry after the user confirmed. Plugins are code that runs with the
 * user's rights, so they get a clear warning first; the host checks https, SHA-256 and minCouchHud.
 * Resolves to the host's result or undefined when cancelled or failed.
 */
export async function installFromCatalog(entry) {
  const what = entry.author ? t('plugins.catalog.whatBy', { name: entry.name, version: entry.version, author: entry.author }) : t('plugins.catalog.what', { name: entry.name, version: entry.version });
  const source = entry.unsigned ? ` ${t('plugins.catalog.unsigned')}` : '';
  const question = entry.kind === 'plugin'
    ? `${t('plugins.catalog.askPlugin', { what })}${source}`
    : `${t('plugins.catalog.askSkin', { what })}${source}`;
  if (!(await confirmDialog(question, entry.status === 'update' ? t('plugins.update') : t('plugins.install'), { danger: entry.kind === 'plugin' }))) return undefined;
  toast(t('plugins.catalog.checking', { name: entry.name }), 4000);
  const result = await attempt(() => api.installCatalogEntry(entry.id, entry.kind), t('plugins.installFailed'));
  if (!result) return undefined;
  toast(result.restartNeeded ? t('plugins.catalog.restartNeeded', { message: result.message }) : result.message, 7000);
  return result;
}

export function pluginsPage(startTab = 0) {
  let plugins = null;
  let catalog = null;
  let loadingCatalog = false;
  const loadCatalog = (page, refresh) => {
    if (loadingCatalog) return;
    loadingCatalog = true;
    attempt(() => api.getCatalog(refresh), t('plugins.catalog.notLoaded')).then((c) => {
      loadingCatalog = false;
      catalog = c ?? { entries: [], error: t('plugins.catalog.unreachableShort') };
      if (!catalog.entries?.length && !catalog.error) catalog.error = t('plugins.catalog.isEmpty');
      if (currentView() === page) render();
    });
  };

  const installedRows = () => [
    { icon: '+', label: t('plugins.installed.pluginZip'), detail: t('plugins.installed.pluginZipDetail'), highlight: true, run: () => installZip('plugin') },
    { icon: '🎨', label: t('plugins.installed.skinZip'), detail: t('plugins.installed.skinZipDetail'), highlight: true, run: () => installZip('skin') },
    ...plugins.map((p) => {
      const update = updateFor(p);
      return {
        icon: p.loaded ? '◆' : p.enabled ? '⚠' : '○',
        label: `${p.name}${p.version ? ` ${p.version}` : ''}`,
        detail: p.error ? t('plugins.installed.error', { error: p.error }) : [update ? t('plugins.installed.updateAvailable', { version: update.version }) : null, p.description, p.author ? t('plugins.by', { author: p.author }) : null].filter(Boolean).join(' · ') || p.folder,
        value: p.needsApproval ? t('plugins.value.approval') : update ? t('plugins.value.update') : p.loaded ? t('plugins.value.active') : p.enabled ? t('plugins.value.error') : t('plugins.value.off'),
        on: !!update || p.loaded,
        dim: !p.enabled,
        run: () => push(pluginPage(p, (next) => (plugins = next)))
      };
    })
  ];

  const catalogRows = (page) => {
    const list = [];
    const pending = catalog.entries.filter((e) => e.restartPending);
    if (pending.length) {
      list.push({ icon: '↻', label: t('plugins.catalog.restartTitle'), detail: t('plugins.catalog.restartDetail', { names: pending.map((e) => e.name).join(', ') }), highlight: true });
    }
    list.push({
      icon: '⟳',
      label: t('plugins.catalog.refresh'),
      hintLabel: t('plugins.catalog.reload'),
      detail: (catalog.error ? catalogError(catalog) : null) ?? (catalog.fetchedAt ? t('plugins.catalog.asOf', { date: fmtDateTime(catalog.fetchedAt, { dateStyle: 'short', timeStyle: 'short' }) }) : t('plugins.catalog.notLoadedYet')),
      run: () => {
        catalog = null;
        loadCatalog(page, true);
        toast(t('plugins.catalog.refreshing'));
        render();
      }
    });
    if (catalog.fromCache) list.push({ icon: '⚠', label: t('plugins.catalog.offline'), detail: t('plugins.catalog.offlineDetail'), dim: true });
    for (const e of [...catalog.entries].sort((a, b) => (a.status === 'update' ? 0 : 1) - (b.status === 'update' ? 0 : 1) || a.name.localeCompare(b.name))) {
      const st = STATUS()[e.status] ?? STATUS().installed;
      list.push({
        icon: st.icon,
        label: `${e.name} ${e.status === 'update' ? `${e.installedVersion} → ${e.version}` : e.version}`,
        detail: [e.unsigned ? t('plugins.catalog.unsignedBadge') : null, e.kind === 'skin' ? t('plugins.kind.skin') : t('plugins.kind.plugin'), e.author ? t('plugins.by', { author: e.author }) : null, e.reason ?? e.description].filter(Boolean).join(' · '),
        value: e.restartPending ? t('plugins.status.restart') : st.value,
        on: st.on,
        hintLabel: e.status === 'installed' || e.status === 'unavailable' || e.restartPending ? t('plugins.catalog.info') : e.status === 'update' ? t('plugins.update') : t('plugins.install'),
        dim: e.status === 'unavailable',
        run: async () => {
          if (e.status === 'unavailable') return toast(e.reason ?? t('plugins.catalog.notInstallable'), 4000);
          if (e.restartPending) return toast(t('plugins.catalog.alreadyApplied'), 4000);
          if (e.status === 'installed') return toast(t('plugins.catalog.upToDate', { name: e.name }));
          const result = await installFromCatalog(e);
          if (result?.state) catalog = result.state;
          render();
        }
      });
    }
    return list;
  };

  const page = rowsPage({
    id: 'plugins',
    title: t('plugins.title'),
    intro: t('plugins.intro'),
    tabs: [t('plugins.tab.installed'), t('plugins.tab.catalog')],
    hints: (page) => [['A', page.tab ? page.rows?.[page.index]?.hintLabel ?? t('plugins.hint.select') : t('plugins.hint.select')], ...(page.tab && page.rows?.[page.index]?.hintLabel !== t('plugins.catalog.reload') ? [['X', t('plugins.hint.reloadCatalog')]] : []), page.tab ? ['LB', t('plugins.tab.installed')] : ['RB', t('plugins.tab.catalog')], ['B', t('plugins.hint.back')]],
    onKey(page, action) {
      if (action === 'x' && page.tab === 1) {
        catalog = null;
        loadCatalog(page, true);
        toast(t('plugins.catalog.refreshing'));
        render();
        return true;
      }
      return false;
    },
    rows(page) {
      if (page.tab === 1) {
        if (!catalog) {
          loadCatalog(page, false);
          return null;
        }
        return catalogRows(page);
      }
      if (!plugins) {
        attempt(() => api.getPlugins(), t('plugins.notLoaded')).then((p) => {
          plugins = p ?? [];
          if (currentView() === page) render();
        });
        return null;
      }
      return installedRows();
    }
  });
  page.tab = startTab;
  return page;
}

function pluginPage(plugin, onChange) {
  let p = plugin;
  const refresh = (list) => {
    const next = list?.find((x) => x.folder === p.folder && x.id === p.id);
    if (next) p = next;
    if (list) onChange(list);
    render();
  };
  return rowsPage({
    id: 'plugin',
    title: () => p.name,
    intro: () => [p.description, p.homepage].filter(Boolean).join(' · '),
    rows: () => [
      ...(p.needsApproval ? [{
        icon: '🔒',
        label: t('plugins.page.approve'),
        detail: p.changed ? t('plugins.page.approveChanged', { code: p.approvalCode || '–' }) : t('plugins.page.approveNew', { code: p.approvalCode || '–' }),
        highlight: true,
        run: async () => {
          if (!(await confirmDialog(p.changed ? t('plugins.approve.askChanged', { name: p.name, code: p.approvalCode || '–' }) : t('plugins.approve.ask', { name: p.name, code: p.approvalCode || '–' }), t('plugins.approve.yes'), { danger: true }))) return;
          if (!(await ensureUnlocked(t('plugins.approve.reason')))) return;
          const list = await attempt(() => api.approvePlugin(p.folder), t('plugins.notSaved'));
          if (!list) return;
          toast(t('plugins.approve.done', { name: p.name }), 6000);
          refresh(list);
        }
      }] : []),
      {
        icon: '⏻',
        label: t('plugins.page.active'),
        detail: p.restartNeeded ? t('plugins.page.restartPending') : t('plugins.page.restartHint'),
        value: p.enabled ? t('plugins.value.on') : t('plugins.value.off'),
        on: p.enabled,
        run: async () => refresh(await attempt(() => api.setPluginEnabled(p.folder, !p.enabled), t('plugins.notSaved')))
      },
      ...(p.pages ?? []).map((pg) => ({ icon: pg.icon, label: pg.title, detail: pg.description, highlight: true, run: () => push(declarativePage(p, pg)) })),
      ...(p.settings ?? []).map((s) => ({
        icon: '⚙',
        label: s.label,
        detail: s.description,
        value: s.kind === 'toggle' ? (s.value === 'true' ? t('plugins.value.on') : t('plugins.value.off')) : s.kind === 'secret' ? (s.value ? t('plugins.value.saved') : t('plugins.value.notSet')) : s.value || '–',
        on: s.kind === 'toggle' && s.value === 'true',
        run: async () => {
          let value;
          if (s.kind === 'toggle') value = s.value === 'true' ? 'false' : 'true';
          else if (s.kind === 'choice') value = await chooseDialog(s.label, s.choices.map((c) => ({ label: c, value: c })), s.value);
          else value = await openKeyboard({ title: s.label, value: s.kind === 'secret' ? '' : s.value ?? '', secret: s.kind === 'secret' });
          if (value === undefined) return;
          refresh(await attempt(() => api.setPluginSetting(p.id, s.key, value), t('plugins.notSaved')));
        }
      })),
      ...(p.tools?.length ? [{ icon: '✦', label: t('plugins.page.forAssistant'), detail: p.tools.join(', ') }] : []),
      { icon: '📁', label: t('plugins.page.folder'), detail: `${p.userInstalled ? '%APPDATA%\\CouchHud\\plugins\\' : 'plugins\\'}${p.folder}` }
    ]
  });
}

/** A page a plugin describes as rows; A runs the plugin's action for that row. */
export function declarativePage(plugin, pageInfo) {
  let data = null;
  return rowsPage({
    id: 'plugin-page',
    title: () => data?.title ?? pageInfo.title,
    intro: () => data?.description ?? pageInfo.description,
    rows(page) {
      if (!data) {
        attempt(() => api.getPluginPage(plugin.id, pageInfo.id), plugin.name).then((d) => {
          data = d ?? { title: pageInfo.title, rows: [] };
          if (currentView() === page) render();
        });
        return null;
      }
      return data.rows.map((r) => ({
        icon: r.icon,
        label: r.label,
        detail: r.detail,
        value: r.value,
        on: r.on,
        run: async () => {
          const result = await attempt(() => api.activatePluginRow(plugin.id, pageInfo.id, r.id), plugin.name);
          if (!result) return;
          if (result.message) toast(result.message);
          data = { ...data, rows: result.rows };
          render();
        }
      }));
    }
  });
}
