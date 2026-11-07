// Start choice: Couch (stay in Mega Hub) or PC (the normal Windows desktop). It only asks; PC mode itself is the host's existing
// enterPcMode, with its lock and second-confirmation checks.

import { api } from '../core/api.js';
import { el, setFocused } from '../core/dom.js';
import { attempt } from '../core/feedback.js';
import { move, push, removePage, render } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';
import { t } from '../core/i18n.js';
import { chooseScreen } from '../views/display.js';
import { ensureUnlocked } from './passcode.js';

// i18n-used: modeask.couch modeask.couchHint modeask.pc modeask.pcHint
const CHOICES = [
  { id: 'couch', icon: '🛋️', label: 'modeask.couch', hint: 'modeask.couchHint' },
  { id: 'pc', icon: '🖥️', label: 'modeask.pc', hint: 'modeask.pcHint' }
];

export function showModeChooser() {
  // A protected profile waiting at the picker, or another page already open: stay out of the way.
  if (state.pickerHold || state.stack.length || state.overlay) return;
  const page = {
    id: 'mode-chooser',
    name: t('modeask.title'),
    index: 0,
    busy: false,
    build(root) {
      this.tiles = CHOICES.map((c) => el('li', { className: 'profile-tile' },
        el('span', { className: 'profile-avatar big', textContent: c.icon }),
        el('strong', { className: 'profile-name', textContent: t(c.label) }),
        el('small', { className: 'profile-lock', textContent: t(c.hint) })));
      root.append(el('div', { className: 'profile-picker' },
        el('h2', { textContent: t('modeask.title') }),
        el('ul', { className: 'profile-tiles' }, this.tiles)));
      this.update();
    },
    update() {
      if (this.tiles?.length) setFocused(this.tiles, this.index);
    },
    async handle(action) {
      if (this.busy) return;
      switch (action) {
        case 'left': return move(this, -1, CHOICES.length, true);
        case 'right': return move(this, 1, CHOICES.length, true);
        case 'b':
          return sound.bump();
        case 'y':
          // Which screen Mega Hub is on (two or more screens), before choosing.
          sound.select();
          return chooseScreen();
        case 'a': {
          sound.select();
          if (CHOICES[this.index].id === 'couch') {
            removePage(this);
            return render();
          }
          // PC at the start: Mega Hub quits completely (no RAM used, gone from Task Manager) and the normal Windows desktop is there.
          // The Power menu's PC mode (from the couch) stays as it was.
          this.busy = true;
          try {
            if (!(await ensureUnlocked(t('power.quit.label')))) return;
            removePage(this);
            render();
            await attempt(() => api.quit());
          } finally {
            this.busy = false;
          }
          return;
        }
        default:
      }
    },
    hints() {
      return [['A', t('modeask.hintChoose')], ['Y', t('modeask.hintScreen')]];
    }
  };
  push(page);
}
