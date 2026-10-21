// Controller passcode, like the Xbox passkey: six buttons in a row instead of a PIN pad.

import { el } from '../core/dom.js';
import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { glyph } from '../core/skins.js';
import { render, removePage } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { confirmDialog } from './overlay.js';

export const PASSCODE_LENGTH = 6;
const CODE_BUTTONS = new Set(['a', 'b', 'x', 'y', 'lb', 'rb', 'up', 'down', 'left', 'right']);
const LABEL = { up: '↑', down: '↓', left: '←', right: '→' };

/** Asks for a passcode; resolves with the button list, or undefined when cancelled (View or Menu). */
export function enterPasscode({ title = t('passcode.enter'), hint = '', error = '' } = {}) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      resolve(result);
    };

    const page = {
      id: 'passcode',
      name: title,
      code: [],
      // Every code button belongs to the code here, B and the shoulders too.
      captures: (action) => action !== 'guide',
      onClose: () => finish(undefined),
      build(root) {
        this.dots = Array.from({ length: PASSCODE_LENGTH }, () => el('span', { className: 'passcode-dot' }));
        this.errorEl = el('p', { className: 'passcode-error', textContent: error });
        root.append(el('div', { className: 'passcode' },
          el('div', { className: 'passcode-lock', textContent: '🔒' }),
          el('h2', { textContent: title }),
          hint ? el('p', { className: 'meta', textContent: hint }) : null,
          el('div', { className: 'passcode-dots' }, this.dots),
          this.errorEl,
          el('p', { className: 'passcode-help', textContent: t('passcode.help', { count: PASSCODE_LENGTH }) })));
        this.update();
      },
      update() {
        this.dots.forEach((d, i) => {
          d.classList.toggle('filled', i < this.code.length);
          d.textContent = '';
        });
      },
      close(result) {
        removePage(this);
        render();
        finish(result);
      },
      handle(action) {
        if (action === 'back' || action === 'start') {
          sound.back();
          return this.close(undefined);
        }
        if (!CODE_BUTTONS.has(action)) return;
        this.code.push(action);
        this.errorEl.textContent = '';
        sound.move();
        this.update();
        // A short glimpse of the last button, then only the dot.
        const dot = this.dots[this.code.length - 1];
        dot.textContent = LABEL[action] ?? glyph(action.toUpperCase());
        setTimeout(() => (dot.textContent = ''), 350);
        if (this.code.length === PASSCODE_LENGTH) {
          const code = this.code;
          setTimeout(() => this.close(code), 200);
        }
      },
      hints() {
        return [['≡', t('passcode.hintCancel')]];
      }
    };
    state.stack.push(page);
    sound.open();
    render();
  });
}

/** Sets a new code: entered twice so a slip doesn't lock you out. */
export async function choosePasscode() {
  const first = await enterPasscode({ title: t('passcode.newTitle'), hint: t('passcode.newHint') });
  if (!first) return undefined;
  const second = await enterPasscode({ title: t('passcode.repeatTitle') });
  if (!second) return undefined;
  if (first.join() !== second.join()) {
    toast(t('passcode.mismatch'));
    sound.bump();
    return undefined;
  }
  return first;
}

// Everyone waiting for the Windows sign-in gets the answer, not only the last one asking.
let pendingWindowsUnlocks = [];

/**
 * Windows mode: the sign-in alone opens nothing (Windows Hello signs in in passing, the parents may have unlocked for another
 * reason). The host sends "confirm", we show what was asked for, and only A on that opens it. Hosts that do not ask
 * for a confirmation (the preview) resolve at once.
 */
async function confirmAfterSignIn(payload, reason) {
  if (!payload?.confirm) return true;
  const accepted = await confirmDialog(t('passcode.confirmTitle'), t('passcode.confirmButton'), { message: reason || t('passcode.confirmGeneric') });
  const result = await attempt(() => api.confirmWindowsUnlock(accepted));
  return !!(accepted && result?.ok);
}

api.onUnlocked?.(async (payload) => {
  const waiting = pendingWindowsUnlocks;
  pendingWindowsUnlocks = [];
  for (const entry of waiting) entry.resolve(await confirmAfterSignIn(payload, entry.reason));
});

/** True when the couch lock is closed right now. A read that fails counts as "not locked": the caller's own request then shows the truth. */
export async function isLocked() {
  try {
    const security = await api.getSecurity();
    return !!security && security.unlocked === false;
  } catch {
    return false;
  }
}

/**
 * True once the couch lock lets the user through: the controller code, or the
 * Windows lock screen (Windows Hello, PIN) in Windows mode.
 */
export async function ensureUnlocked(reason = '') {
  const security = await attempt(() => api.getSecurity());
  if (!security || security.unlocked) return true;

  if (security.couchLock === 'windows') {
    toast(t('passcode.signInWindows'), 4000);
    const unlocked = new Promise((resolve) => {
      pendingWindowsUnlocks.push({ resolve, reason });
      // The host drops the request after three minutes.
      setTimeout(() => resolve(false), 3 * 60_000);
    });
    const asked = await attempt(() => api.unlock(null));
    // Windows could not be locked: nothing will come.
    if (asked?.pending === false) pendingWindowsUnlocks = pendingWindowsUnlocks.filter((e) => e.reason !== reason);
    return asked?.pending === false ? false : unlocked;
  }

  // One code entry at a time: a second request that arrives while the first one is open (a page that loads twice, a protected
  // entry and the page behind it) waits for that entry instead of opening a second one on top.
  codeEntry ??= askForCode(reason).finally(() => { codeEntry = null; });
  return codeEntry;
}

let codeEntry = null;

async function askForCode(reason) {
  let error = '';
  for (;;) {
    const code = await enterPasscode({ title: t('passcode.enter'), hint: reason, error });
    if (!code) return false;
    const result = await attempt(() => api.unlock(code));
    if (result?.ok) {
      sound.select();
      return true;
    }
    sound.bump();
    // The host refuses to open while the CouchHUD service is missing or blocked (fail-closed): its reason, not "wrong code".
    error = result?.blocked ? result.blocked : result?.waitSeconds ? t('passcode.wrongWait', { count: result.waitSeconds }) : t('passcode.wrong');
  }
}

// Before Windows' UAC prompt for an administrator's approval: what exactly is approved (the prompt itself only names sc.exe).
// i18n-used: passcode.approve.autoLogon passcode.approve.lockMode passcode.approve.removeCode passcode.approve.changeCode passcode.approve.release passcode.approve.firewall passcode.approve.update
const APPROVAL_TEXT = {
  SetAutoLogon: 'passcode.approve.autoLogon',
  SetLockMode: 'passcode.approve.lockMode',
  RemovePasscode: 'passcode.approve.removeCode',
  ChangePasscode: 'passcode.approve.changeCode',
  EnrollPasscode: 'passcode.approve.changeCode',
  ReleaseLockPolicies: 'passcode.approve.release',
  CleanupPolicyKeys: 'passcode.approve.release',
  SetRemoteFirewall: 'passcode.approve.firewall',
  InstallCouchHudUpdate: 'passcode.approve.update'
};
api.onAdminApproval?.((payload) => {
  const what = [...new Set((payload?.what ?? []).map((op) => APPROVAL_TEXT[op]).filter(Boolean))].map((key) => t(key));
  toast(t('passcode.approve.notice', { what: what.length ? what.join(', ') : t('passcode.approve.confirmOnly') }), 12000);
});

// Same for the parents' code: a one-time release for one action, never the 2-minute unlock.
let pendingParentAuth = [];
api.onParentalAuth?.(async (payload) => {
  const waiting = pendingParentAuth;
  pendingParentAuth = [];
  for (const entry of waiting) entry.resolve(await confirmAfterSignIn(payload, entry.reason));
});

/**
 * The parents' code for exactly one action (extend time, release a locked game, change the profile). Asks every
 * time, even right after another unlock; the host uses it up with the action. Windows mode: lock screen.
 */
export async function ensureParentCode(reason = '', scope = {}) {
  const security = await attempt(() => api.getSecurity());
  const { action = null, item = null } = scope;
  if (security?.couchLock === 'windows') {
    toast(t('passcode.signInWindows'), 4000);
    const done = new Promise((resolve) => {
      pendingParentAuth.push({ resolve, reason });
      setTimeout(() => resolve(false), 3 * 60_000);
    });
    const asked = await attempt(() => api.parentalAuth(null, action, item));
    if (asked?.pending === false) {
      pendingParentAuth = pendingParentAuth.filter((e) => e.reason !== reason);
      return false;
    }
    return done;
  }
  let error = '';
  for (;;) {
    const code = await enterPasscode({ title: t('passcode.parentCode'), hint: reason, error });
    if (!code) return false;
    const result = await attempt(() => api.parentalAuth(code, action, item));
    if (result?.ok) {
      sound.select();
      return true;
    }
    sound.bump();
    // Locked out, or no parents' code in the CouchHUD service yet: an administrator of this PC can approve instead (Windows' own UAC
    // prompt), so a forgotten, missing or planted parents' code never shuts the parents out.
    const fallback = result?.admin === 'noCode' ? t('passcode.adminFallbackNoCode') : t('passcode.adminFallback');
    if (result?.admin && (await confirmDialog(fallback, t('passcode.adminFallbackButton')))) {
      const viaAdmin = await attempt(() => api.parentalAuth(null, action, item));
      if (viaAdmin?.ok) {
        sound.select();
        return true;
      }
      if (viaAdmin?.blocked) toast(viaAdmin.blocked, 6000);
    }
    // The CouchHUD service checks the parents' code too: when it cannot, its reason, not "wrong code".
    error = result?.blocked ? result.blocked : result?.waitSeconds ? t('passcode.wrongWait', { count: result.waitSeconds }) : t('passcode.wrong');
  }
}

/** Wraps an action that leaves the couch (PC mode, quit …) in the lock. */
export const guarded = (reason, run) => async () => (await ensureUnlocked(reason)) ? run() : undefined;
