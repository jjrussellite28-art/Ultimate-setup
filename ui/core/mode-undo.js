// What a step of the mode switch records before it runs and how it is undone (see core/mode-transaction.js). A factory so the real tables can
// be run against a fake host in scripts/ui-logic-check.mjs: `api` is the host bridge, `state` the page state (state.info).

import { manualUndo } from './mode-transaction.js';

// The way back's suppress step: already off 'done'; on with the shell setup's mark (AppConfig.SuppressBySetup) 'todo', it goes off;
// on without the mark (the user's own choice, or on before the setup) 'na', it stays on.
export const suppressBackStatus = (settings) => (!settings?.suppressCompetitors ? 'done' : settings.suppressBySetup ? 'todo' : 'na');

export function createUndoTables({ api, state, wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) }) {
  // What a step records before it runs and how it is undone (the mode switch is a transaction, see core/mode-transaction.js).
  // snapshot(env) returns plain JSON of what the step is about to change; undo(before) looks at the state NOW and puts back only what
  // differs from the record: 'undone', or 'unchanged' when it found the state as recorded. A step only the person can reverse has no undo.

  const settingBack = (key) => ({
    snapshot: (env) => ({ v: !!env.settings[key] }),
    undo: async (before) => {
      if (!before) throw new Error('no record');
      const now = await api.getSettings();
      if (!!now?.[key] === before.v) return 'unchanged';
      await api.setSetting(key, before.v);
      return 'undone';
    }
  });

  // "Mega Hub stays in front" with the mark of the shell setup (b: the setup switched it on, see AppConfig.SuppressBySetup). The host
  // clears the mark with every change of the setting, so the mark goes back after it. A record of 0.6.3 has no b: only the setting.
  const suppressBack = {
    snapshot: (env) => ({ v: !!env.settings.suppressCompetitors, b: !!env.settings.suppressBySetup }),
    undo: async (before) => {
      if (!before) throw new Error('no record');
      const now = await api.getSettings();
      const mark = typeof before.b === 'boolean' ? before.b && before.v : null;
      const settingDiffers = !!now?.suppressCompetitors !== before.v;
      if (!settingDiffers && (mark === null || !!now?.suppressBySetup === mark)) return 'unchanged';
      if (settingDiffers) await api.setSetting('suppressCompetitors', before.v);
      if (mark !== null) await api.setSetting('suppressBySetup', mark);
      return 'undone';
    }
  };

  const startupModes = async () => (await api.getStartup()) ?? [];

  // The services as the host applied them (`installed`), for the record: plain names only, bounded like every list of a record. Null when
  // the host did not say (the undo then falls back to the selection alone).
  const installedOf = (data, max) => (Array.isArray(data?.installed) ? data.installed.filter((n) => typeof n === 'string' && n.length > 0 && n.length <= 96).slice(0, max) : null);
  const sameSet = (a, b) => a.size === b.size && [...a].every((x) => b.has(x));
  // Ticks the list until exactly `wanted` is selected (only names the host lists; the host checks each against its fixed list).
  const tick = async (wanted) => {
    let changed = false;
    for (const entry of (await api.getServices())?.services ?? []) {
      if (!!entry.selected !== wanted.has(entry.name)) {
        await api.toggleCouchService(entry.name);
        changed = true;
      }
    }
    return changed;
  };
  /**
   * Puts the services back to the record { s: selected, i: applied }. The applied list goes first (selected and applied by the host, which
   * pauses and resumes accordingly), then the selection is ticked back WITHOUT applying: a selection that was pending before (chosen, not
   * applied) is pending again instead of being applied by the undo. `apply` false: the host could not apply before either (not healthy), so
   * only the selection is put back.
   */
  const servicesBack = async (before, apply = true) => {
    const data = await api.getServices();
    const listed = new Set((data?.services ?? []).map((x) => x.name));
    // Only what the list can still tick counts (a service that is gone from this PC cannot be selected again).
    const selected = new Set((before.s ?? []).filter((n) => listed.has(n)));
    const applied = new Set(before.i.filter((n) => listed.has(n)));
    const nowApplied = new Set((installedOf(data, 64) ?? []).filter((n) => listed.has(n)));
    const nowSelected = new Set((data?.services ?? []).filter((x) => x.selected).map((x) => x.name));
    let changed = false;
    if (apply && !sameSet(applied, nowApplied)) {
      await tick(applied);
      await api.setupServiceTask(true);
      changed = true;
    }
    if (!changed && sameSet(selected, nowSelected)) return 'unchanged';
    await tick(selected);
    return 'undone';
  };

  // Whether the service held a parents' code when the setup's lock step was recorded (see SETUP_UNDO.lock).
  let parentHeldBefore = false;

  const SETUP_UNDO = {
    integration: {
      snapshot: (env) => ({ m: env.settings.integrationMode ?? 'light' }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        if (now?.integrationMode === before.m) return 'unchanged';
        const result = await api.setIntegrationMode(before.m);
        state.info.integrationMode = result?.mode ?? before.m;
        return 'undone';
      }
    },
    autostart: {
      // Only the entries the suggestions would move are recorded (the record stays small).
      snapshot: async (env) => ({ r: !!env.settings.runStartupApps, e: (await startupModes()).filter((e) => !e.disabled && e.mode !== e.suggested).slice(0, 24).map((e) => [e.id, e.mode]) }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        let changed = false;
        const now = await startupModes();
        for (const [id, mode] of before.e ?? []) {
          if (now.find((e) => e.id === id)?.mode !== mode && now.some((e) => e.id === id)) {
            await api.setStartupMode(id, mode);
            changed = true;
          }
        }
        if (!!(await api.getSettings())?.runStartupApps !== before.r) {
          await api.setSetting('runStartupApps', before.r);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    services: {
      // `taskInstalled` of the host only says the service is healthy; it is true before and after, so it tells nothing about what the setup
      // changed. What counts: which services were selected (k: that selection was applied, nothing pending), which ones the host had applied
      // (i, so a selection that was pending before stays pending after the undo) and then the host's own apply.
      snapshot: async () => {
        const data = await api.getServices();
        const installed = installedOf(data, 24);
        return { k: !data?.pending, s: (data?.services ?? []).filter((x) => x.selected).slice(0, 24).map((x) => x.name), ...(installed ? { i: installed } : {}) };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        if (Array.isArray(before.i)) return servicesBack(before);
        // A record of 0.6.3 (no applied list): the selection, applied again when it was applied before.
        const data = await api.getServices();
        const wanted = new Set(before.s ?? []);
        let changed = false;
        for (const entry of data?.services ?? []) {
          if (!!entry.selected !== wanted.has(entry.name)) {
            await api.toggleCouchService(entry.name);
            changed = true;
          }
        }
        // Applied again by the host (it resumes what is no longer selected and pauses what was selected before; with an empty selection it
        // resumes all and empties the installed list), not only ticked in the list.
        if (changed || (data?.pending && before.k)) {
          await api.setupServiceTask(true);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    xbox: settingBack('guideTapInGame'),
    suppress: suppressBack,
    lock: {
      // c: a couch code was held before. Switching the lock off removes the service's couch code, which cannot be put back.
      // The parents' code the service held before is kept here in the page (the host-checked record has no key for it): only a code that was
      // there can be lost. After a restart in the middle of a switch this is not known, and the undo then says nothing about it.
      snapshot: (env) => {
        parentHeldBefore = env.security.serviceCodes?.parent === true;
        return { m: env.security.couchLock ?? 'off', c: !!env.security.hasPasscode };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSecurity();
        if (now?.couchLock === before.m) return 'unchanged';
        await api.setCouchLock(before.m);
        // Not "back as it was": the old code is gone, a new one has to be set (reported as a leftover, never as done).
        if (before.c && before.m === 'off' && !(await api.getSecurity())?.hasPasscode) throw manualUndo('lockCode');
        // Raising the lock from "off" clears a parents' code that was set at the open couch (the service never lets such a code become the
        // lock), and nothing here can put it back. With parental controls on and the service holding no parents' code any more that is a
        // leftover to be named, never a quiet loss. Only a look: nothing is set or skipped.
        if (before.m === 'off' && parentHeldBefore) {
          let parental;
          try {
            parental = await api.getParental?.();
          } catch {
            // Not readable: neither "fine" nor "lost", the person is asked to look.
            throw manualUndo('parentUnclear');
          }
          if (parental?.enabled && (await api.getSecurity())?.serviceCodes?.parent === false) throw manualUndo('parentCode');
        }
        return 'undone';
      }
    },
    shell: {
      snapshot: (env) => ({ s: !!env.settings.isShell }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        if (!!now?.isShell === before.s) return 'unchanged';
        state.info.isShell = !!(await api.setShell(before.s));
        return 'undone';
      }
    },
    autologon: {
      snapshot: (env) => ({ on: !!env.security.autoLogon }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSecurity();
        if (before.on || !now?.autoLogon || now.autoLogonState !== 'megahub') return 'unchanged';
        await api.setAutoLogon('off');
        return 'undone';
      }
    }
  };

  const REVERT_UNDO = {
    // The way back to the shell, step by step. Only the automatic sign-in (a password) has no undo: it is named as a leftover and the text says
    // how to set it up again. What a step did NOT change is found unchanged and never listed: the undo compares with the record first.
    release: {
      // The lock's Windows policies are set by the host only while the couch is locked (Hud.SyncLockState: Hardening.Set(locked)); the setup's
      // hold keeps the couch open, so they cannot come back before the hold ends. This undo therefore puts back what they depend on (the lock,
      // the full mode and the helper tasks, by the undos of the steps after it) and checks that it is in place; the host sets the policies
      // again when the hold ends and the couch locks. If the lock is not back they are a leftover.
      snapshot: async () => ({ p: !!(await api.getServices())?.policiesSet }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        if (!before.p || (await api.getServices())?.policiesSet) return 'unchanged';
        const lock = (await api.getSecurity())?.couchLock;
        // taskInstalled is the service's health: without the service the host cannot set the policies at all.
        if ((await api.getServices())?.taskInstalled === false || (await api.getSettings())?.integrationMode !== 'full' || !lock || lock === 'off') throw new Error('policies not back');
        // Not "undone": the policies are set later, when the couch locks.
        return 'later';
      }
    },
    services: {
      // The paused services and the helper tasks of the shell setup. The tasks need the full mode (the undo of the last step ran first).
      // i: what the host had applied, so a selection that was pending before the way back is pending again, not applied by the undo.
      snapshot: async () => {
        const data = await api.getServices();
        const installed = installedOf(data, 12);
        return { k: !!data?.taskInstalled, s: (data?.services ?? []).filter((x) => x.selected).slice(0, 12).map((x) => x.name), ...(installed ? { i: installed } : {}) };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        // Without a healthy service before (k false) the host could not apply anything then either: only the selection comes back.
        if (Array.isArray(before.i)) return servicesBack(before, before.k);
        // A record of 0.6.3 (no applied list).
        const data = await api.getServices();
        let changed = false;
        for (const name of before.s ?? []) {
          const entry = (data?.services ?? []).find((x) => x.name === name);
          if (entry && !entry.selected) {
            await api.toggleCouchService(name);
            changed = true;
          }
        }
        // `taskInstalled` only says the service is healthy: it stays true after the way back. What counts is the selection that was applied
        // (data.pending: it differs from the selection now), so the host applies it again (pauses the services, records them as installed).
        if (before.k && (changed || data?.pending)) {
          await api.setupServiceTask(true);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    profiles: {
      snapshot: async () => ({ p: (await startupModes()).filter((e) => e.mode === 'pc').slice(0, 24).map((e) => e.id) }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await startupModes();
        let changed = false;
        for (const id of before.p ?? []) {
          const entry = now.find((e) => e.id === id);
          if (entry && entry.mode !== 'pc') {
            await api.setStartupMode(id, 'pc');
            changed = true;
          }
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    suppress: suppressBack,
    light: {
      snapshot: (env) => {
        // A lock that could not be read is not "off": no record, and the undo then refuses instead of guessing.
        const lock = env.security?.couchLock;
        if (!['off', 'code', 'windows'].includes(lock)) throw new Error('lock not read');
        return { m: env.settings.integrationMode ?? 'light', s: !!env.settings.isShell, l: lock };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        let changed = false;
        if (now?.integrationMode !== before.m) {
          const result = await api.setIntegrationMode(before.m);
          state.info.integrationMode = result?.mode ?? before.m;
          changed = true;
        }
        if (before.s && !(await api.getSettings())?.isShell) {
          state.info.isShell = !!(await api.setShell(true));
          changed = true;
        }
        // The switch to the light mode drops the couch lock with it: it goes back too, and when it cannot (the old code is gone) that is a
        // leftover, never a quiet loss of the protection.
        if (before.l && before.l !== 'off' && (await api.getSecurity())?.couchLock !== before.l) {
          try {
            await api.setCouchLock(before.l);
          } catch {
            // judged by the state below
          }
          if ((await api.getSecurity())?.couchLock !== before.l) throw manualUndo(before.l === 'code' ? 'lockCode' : 'lockMode');
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    }
  };
  return { SETUP_UNDO, REVERT_UNDO };
}
