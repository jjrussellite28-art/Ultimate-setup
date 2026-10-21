// The one way the UI shows a web address: in CouchHUD's own browser, never in the system's.
// Only http(s) goes that way; the host checks again and refuses everything else.

import { api } from './api.js';
import { t } from './i18n.js';
import { attempt, toast } from './feedback.js';
import { ensureUnlocked } from '../components/passcode.js';

export const isWebUrl = (url) => /^https?:\/\/[^\s/@]+/i.test(String(url ?? '').trim());

/** `source` tells the host where the link came from (news, detail, plugin …), for its log. */
export async function openLink(url, source) {
  if (!isWebUrl(url)) {
    toast(t('browser.notALink'));
    return;
  }
  // Web pages can open Windows dialogs: with the couch lock on, the browser needs the code.
  if (!(await ensureUnlocked(t('browser.openLock')))) return;
  return attempt(() => api.openUrl(url.trim(), source), t('browser.openFailed'));
}
