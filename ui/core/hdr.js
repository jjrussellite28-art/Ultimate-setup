// HDR for the HUD: with Windows HDR on (or CouchHUD turning it on), artwork and
// trailers use the full brightness range and the skins switch to wide-gamut colors,
// glows and light sweeps (see the "HDR" section in styles.css and the skins).

import { api } from './api.js';
import { state } from './state.js';

const query = window.matchMedia?.('(dynamic-range: high)');

function update() {
  const mode = state.info.uiHdr ?? 'auto';
  const displayHdr = state.info.hdrOn || query?.matches;
  const active = mode === 'on' || (mode === 'auto' && displayHdr);
  if (active) document.documentElement.dataset.hdr = 'on';
  else delete document.documentElement.dataset.hdr;
}

export function startHdr() {
  update();
  query?.addEventListener?.('change', update);
  api.onHdr?.(({ mode, on }) => {
    state.info.uiHdr = mode;
    state.info.hdrOn = on;
    update();
  });
}

export function setHdrMode(mode) {
  state.info.uiHdr = mode;
  update();
}
