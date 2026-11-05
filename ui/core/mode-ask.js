// "Ask at start": when on, Mega Hub opens with a Couch or PC choice. Kept per browser profile of the app; off by default.
const KEY = 'megahub.askMode';

export function askModeAtStart() {
  try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}

export function setAskModeAtStart(on) {
  try { localStorage.setItem(KEY, on ? '1' : '0'); } catch { /* storage blocked: the choice is not kept */ }
}
