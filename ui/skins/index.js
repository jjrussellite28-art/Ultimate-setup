// Built-in skins. A skin is a stylesheet scoped to :root[data-skin="<id>"]
// plus optional button glyph labels. User skins in %APPDATA%\CouchHud\skins
// use the same shape (skin.json + skin.css), see docs/SKINS.md.

import { t } from '../core/i18n.js';

export const BUILTIN_SKINS = [
  {
    id: 'default',
    name: 'CouchHUD',
    get description() { return t('skins.default'); },
    css: null,
    glyphs: {}
  },
  {
    id: 'ps4',
    name: 'PS4 Classic',
    get description() { return t('skins.ps4'); },
    css: 'skins/ps4.css',
    glyphs: { A: '✕', B: '○', X: '□', Y: '△', LB: 'L1', RB: 'R1', '≡': 'OPTIONS' }
  },
  {
    id: 'xbox',
    name: 'Xbox',
    get description() { return t('skins.xbox'); },
    css: 'skins/xbox.css',
    glyphs: { '≡': '☰' }
  },
  {
    id: 'cyberpunk',
    name: 'Cyberpunk',
    get description() { return t('skins.cyberpunk'); },
    css: 'skins/cyberpunk.css',
    glyphs: { '≡': 'MENU' }
  },
  {
    id: 'nova',
    name: 'Nova',
    get description() { return t('skins.nova'); },
    css: 'skins/nova.css',
    glyphs: {}
  }
];
