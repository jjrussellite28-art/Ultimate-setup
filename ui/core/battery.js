// Laptop battery next to the clock (nothing on a desktop PC) and Bluetooth device batteries.

import { api } from './api.js';
import { $ } from './dom.js';
import { state } from './state.js';

const REFRESH_MS = 60 * 1000;

state.batteries = { pc: null, devices: [] };

export const deviceIcon = (kind) => ({ controller: '🎮', headphones: '🎧', keyboard: '⌨', mouse: '🖱', speaker: '🔊', phone: '📱' })[kind] ?? 'ᛒ';

function show() {
  const node = $('#battery');
  if (!node) return;
  const pc = state.batteries?.pc;
  node.hidden = !pc;
  if (!pc) return;
  node.textContent = `${pc.charging ? '⚡' : '🔋'} ${pc.percent}%`;
  node.classList.toggle('low', !pc.charging && pc.percent <= 15);
}

/** Reads the batteries (also used by the guide menu); keeps the header current. */
export async function refreshBatteries() {
  try {
    const b = await api.getBatteries();
    if (b) state.batteries = { pc: b.pc ?? null, devices: b.devices ?? [] };
  } catch {
    // Keep the last numbers.
  }
  show();
  return state.batteries;
}

let timer;
export function startBattery() {
  clearInterval(timer);
  refreshBatteries();
  timer = setInterval(refreshBatteries, REFRESH_MS);
}
