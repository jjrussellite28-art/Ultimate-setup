import { api } from '../core/api.js';
import { state } from '../core/state.js';
import { applySkin } from '../core/skins.js';
import { render } from '../core/nav.js';
import { t } from '../core/i18n.js';

/** Skin picker with live preview while browsing; B reverts. */
export function skinMenu() {
  const original = state.skin;
  return {
    title: t('skinMenu.title'),
    // The screen behind stays visible: moving through the list shows each skin live.
    preview: true,
    hints: () => [['↕', t('skinMenu.hintPreview')], ['A', t('skinMenu.hintApply')], ['B', t('skinMenu.hintBack')]],
    index: Math.max(0, state.skins.indexOf(original)),
    onMove: (i) => applySkin(state.skins[i]),
    onCancel: () => applySkin(original),
    entries: state.skins.map((skin) => ({
      label: skin.name,
      detail: skin.description,
      run: async () => {
        applySkin(skin);
        await api.setSkin(skin.id);
        render();
      }
    }))
  };
}
