// Weather in the header and the guide menu (Open-Meteo through the host).

import { api } from './api.js';
import { $ } from './dom.js';
import { t } from './i18n.js';
import { state } from './state.js';

const REFRESH_MS = 15 * 60 * 1000;

/** WMO weather codes as icons and words. */
export function describe(code, isDay = true) {
  if (code === 0) return isDay ? ['☀️', t('weather.clear')] : ['🌙', t('weather.clear')];
  if (code <= 2) return isDay ? ['🌤️', t('weather.partlyCloudy')] : ['☁️', t('weather.partlyCloudy')];
  if (code === 3) return ['☁️', t('weather.overcast')];
  if (code === 45 || code === 48) return ['🌫️', t('weather.fog')];
  if (code >= 51 && code <= 57) return ['🌦️', t('weather.drizzle')];
  if (code >= 61 && code <= 67) return ['🌧️', t('weather.rain')];
  if (code >= 71 && code <= 77) return ['🌨️', t('weather.snow')];
  if (code >= 80 && code <= 82) return ['🌦️', t('weather.showers')];
  if (code === 85 || code === 86) return ['🌨️', t('weather.snowShowers')];
  if (code >= 95) return ['⛈️', t('weather.thunderstorm')];
  return ['🌡️', ''];
}

// Temperature unit: "auto" follows the language of the PC (Fahrenheit for en-US), or °C / °F fixed. Kept on this device.
const UNIT_KEY = 'megahub.weather.unit';
export const weatherUnit = () => {
  try { const v = localStorage.getItem(UNIT_KEY); if (v === 'c' || v === 'f') return v; } catch { /* storage blocked: auto */ }
  return 'auto';
};
export function setWeatherUnit(unit) {
  try { localStorage.setItem(UNIT_KEY, unit); } catch { /* not kept this time */ }
  show();
}
const useFahrenheit = () => (weatherUnit() === 'auto' ? /^en-(US|LR|MM)\b/i.test(navigator.language || '') : weatherUnit() === 'f');
/** Open-Meteo reports °C. */
export const temp = (c) => `${Math.round(useFahrenheit() ? c * 9 / 5 + 32 : c)}°${useFahrenheit() ? 'F' : 'C'}`;

/** Short form for header and guide menu, e.g. "🌤️ 12° Berlin". */
export function weatherLine(withPlace = false) {
  const w = state.weather;
  if (!w) return '';
  const [icon] = describe(w.code, w.isDay);
  return `${icon} ${temp(w.temperature)}${withPlace ? ` ${w.place}` : ''}`;
}

export function show() {
  const node = $('#weather');
  if (!node) return;
  node.hidden = !state.weather;
  node.textContent = weatherLine();
  node.title = state.weather ? `${state.weather.place}: ${describe(state.weather.code, state.weather.isDay)[1]}` : '';
}

export async function refreshWeather() {
  try {
    state.weather = await api.getWeather();
  } catch {
    // Offline: keep the last report rather than flashing an error in the header.
  }
  show();
}

let timer;
export function startWeather() {
  clearInterval(timer);
  refreshWeather();
  timer = setInterval(refreshWeather, REFRESH_MS);
}
