// "Search artwork" in the edit menu: when an entry shows the pictures of another game (or none), the user searches the Steam store and
// SteamGridDB and picks the right game. The pick is pinned on the host (ArtworkMatches) and survives rescans and "Reload artwork".

import { api } from '../core/api.js';
import { attempt, toast } from '../core/feedback.js';
import { render } from '../core/nav.js';
import { chooseDialog, openOverlay } from './overlay.js';
import { openKeyboard } from './keyboard.js';
import { ensureUnlocked } from './passcode.js';
import { t } from '../core/i18n.js';
import { MAX_QUERY, artworkHitDetail, artworkMatchOk, canChooseArtwork, queryOk } from '../core/item-info-rules.js';

async function savePin(item, match, name, message) {
  const view = await attempt(() => api.setArtworkMatch(item.id, match, name ?? null), t('artworkSearch.failed'));
  if (!view) return;
  toast(message);
  // The host sends the new library with the pictures as they arrive; the page draws what is known now.
  render();
}

/** The edit menu's row: search, and back to automatic when a game is pinned. */
export async function artworkSearchMenu(item) {
  if (!canChooseArtwork(item)) {
    toast(t('artworkSearch.steam'));
    return;
  }
  if (!(await ensureUnlocked(t('artworkSearch.guard')))) return;
  const view = await attempt(() => api.getArtworkMatch(item.id), t('artworkSearch.failed'));
  if (!view) return;
  const pinned = view.mode === 'pinned';
  const entries = [{ label: t('artworkSearch.search'), detail: t('artworkSearch.searchDetail'), icon: '🔍', run: () => searchFlow(item) }];
  if (pinned) entries.push({ label: t('artworkSearch.auto'), detail: t('artworkSearch.autoDetail'), icon: '↻', run: () => savePin(item, null, null, t('artworkSearch.autoDone')) });
  entries.push({ label: t('artworkSearch.close'), run: () => {} });
  const current = pinned ? t('artworkSearch.pinned', { name: view.name || view.match }) : t('artworkSearch.automatic');
  openOverlay({ title: t('artworkSearch.title', { name: item.name }), message: current, entries });
}

/** Search term (the entry's name to start with, editable), then the hits with a small cover each; A pins the game. */
export async function searchFlow(item) {
  const query = await openKeyboard({ title: t('artworkSearch.searchTitle'), value: item.name, maxLength: MAX_QUERY });
  if (query === undefined) return;
  if (!queryOk(query)) {
    toast(t('artworkSearch.queryBad'));
    return;
  }
  toast(t('artworkSearch.running'));
  const result = await attempt(() => api.searchArtwork(item.id, query.trim()), t('artworkSearch.failed'));
  if (!result) return;
  const hits = (result.hits ?? []).filter((h) => artworkMatchOk(h.match));
  if (hits.length === 0) {
    toast(result.note || t('artworkSearch.noHits'));
    return;
  }
  // What is missing (no key, a service down) is said, but the hits that came are shown anyway.
  if (result.note) toast(result.note);
  const pick = await chooseDialog(t('artworkSearch.results', { query: query.trim() }), hits.map((h) => ({
    label: h.name,
    detail: artworkHitDetail(h, t),
    thumb: h.thumb ?? null,
    value: h
  })));
  if (pick) await savePin(item, pick.match, pick.name, t('artworkSearch.pickedDone', { name: pick.name }));
}
