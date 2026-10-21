import { $ } from './dom.js';
import { state } from './state.js';
import { setSkinSounds } from '../sound.js';

export const glyph = (btn) => state.skin.glyphs?.[btn] ?? btn;

export function applySkin(skin) {
  const link = $('#skin-css');
  if (skin.css) link.href = skin.css;
  else link.removeAttribute('href');
  document.documentElement.dataset.skin = skin.id;
  state.skin = skin;
  setSkinSounds(skin.id, skin.sounds);
  for (const node of document.querySelectorAll('[data-glyph]')) node.textContent = glyph(node.dataset.glyph);
  for (const node of document.querySelectorAll('.hint[data-btn] .glyph')) {
    node.textContent = glyph(node.closest('.hint').dataset.btn);
  }
}
