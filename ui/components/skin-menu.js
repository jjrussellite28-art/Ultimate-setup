import { api } from '../core/api.js';
import { state } from '../core/state.js';
import { applySkin } from '../core/skins.js';
import { render, skinRejected, skinVerdict } from '../core/nav.js';
import { toast } from '../core/feedback.js';
import { t } from '../core/i18n.js';
import { BUILTIN_SKINS } from '../skins/index.js';

/**
 * The skins as the host knows them now: a skin copied into %APPDATA%\CouchHud\skins while Mega Hub runs shows up without a restart
 * (the host reads the folder on every call). A failed read keeps the list from before.
 */
export async function reloadSkins() {
  try {
    const user = await api.listSkins();
    if (Array.isArray(user)) state.skins = [...BUILTIN_SKINS, ...user];
  } catch (err) {
    console.error(err);
  }
  return state.skins;
}

/** A skin was uninstalled: the list is read again, and when the skin on screen is gone the Couch skin takes its place (the host saved that too). */
export async function afterSkinUninstalled() {
  await reloadSkins();
  if (!state.skins.some((s) => s.id === state.skin?.id)) {
    applySkin(BUILTIN_SKINS[0]);
    state.info.skin = BUILTIN_SKINS[0].id;
  }
}

/** Skin picker with live preview while browsing; B reverts. The list is read again first (see reloadSkins). */
export async function skinMenu() {
  await reloadSkins();
  const original = state.skin;
  return {
    title: t('skinMenu.title'),
    // The screen behind stays visible: moving through the list shows each skin live.
    preview: true,
    hints: () => [['↕', t('skinMenu.hintPreview')], ['A', t('skinMenu.hintApply')], ['B', t('skinMenu.hintBack')]],
    // By id: a skin read again is a new object.
    index: Math.max(0, state.skins.findIndex((s) => s.id === original?.id)),
    onMove: (i) => applySkin(state.skins[i]),
    onCancel: () => applySkin(original),
    entries: state.skins.map((skin) => ({
      label: skin.name,
      detail: skin.description,
      run: async () => {
        applySkin(skin);
        // The bar is looked at with the skin first: one that covers or squashes the tabs is replaced by the default skin and is not saved
        // (it would come back at every start); the screen and the setting stay what they were.
        if (!(await skinVerdict(skin.id))) {
          if (original && !skinRejected(original.id)) applySkin(original);
          toast(t('skinMenu.rejected', { name: skin.name }), 6000, { long: true });
          render();
          return;
        }
        await api.setSkin(skin.id);
        render();
      }
    }))
  };
}
