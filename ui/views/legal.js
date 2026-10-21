// Disclaimer and privacy notice: shown once at the first start (accept to continue),
// readable any time under Settings › Info. The full texts live in docs/DISCLAIMER.md
// and docs/PRIVACY.md; keep both in sync when changing anything here.

import { api } from '../core/api.js';
import { lang, t } from '../core/i18n.js';
import { el } from '../core/dom.js';
import { attempt, toast } from '../core/feedback.js';
import { render, removePage } from '../core/nav.js';
import { state } from '../core/state.js';
import { sound } from '../sound.js';

/** Raise when the texts change in a way users should confirm again. */
export const TERMS_VERSION = 1;

const REPO = 'github.com/kewe87/CouchHUD';

export const DISCLAIMER = () => [
  [t('legal.disclaimer.warrantyTitle'), t('legal.disclaimer.warrantyText')],
  [t('legal.disclaimer.windowsTitle'), t('legal.disclaimer.windowsText')],
  [t('legal.disclaimer.autologonTitle'), t('legal.disclaimer.autologonText')],
  [t('legal.disclaimer.pauseTitle'), t('legal.disclaimer.pauseText')],
  [t('legal.disclaimer.pluginsTitle'), t('legal.disclaimer.pluginsText')],
  [t('legal.disclaimer.brandsTitle'), t('legal.disclaimer.brandsText')],
  [t('legal.disclaimer.fullTitle'), t('legal.disclaimer.fullText', { repo: REPO })]
];

export const PRIVACY = () => [
  [t('legal.privacy.telemetryTitle'), t('legal.privacy.telemetryText')],
  [t('legal.privacy.storedTitle'), t('legal.privacy.storedText')],
  [t('legal.privacy.connectionsTitle'), t('legal.privacy.connectionsText')],
  [t('legal.privacy.assistantTitle'), t('legal.privacy.assistantText')],
  [t('legal.privacy.homeNetworkTitle'), t('legal.privacy.homeNetworkText')],
  [t('legal.privacy.notificationsTitle'), t('legal.privacy.notificationsText')],
  [t('legal.privacy.fullTitle'), t('legal.privacy.fullText', { repo: REPO })]
];

function textPage({ id, title, sections, firstRun, onBack }) {
  // Only the German texts are binding; say so in the other language.
  const prevails = () => (lang() === 'de' ? null : el('p', { className: 'legal-intro', textContent: t('legal.germanPrevails') }));
  return {
    id,
    name: title,
    // Host navigation (guide, returning from a game) must not skip the first-start notice.
    sticky: !!firstRun,
    // No notification cards over the first-start notice (views/notifications.js).
    quietNotifications: !!firstRun,
    // On the first start every button belongs to this page (B must not just close it).
    captures: firstRun ? () => true : undefined,
    build(root) {
      this.scroller = el('div', { className: 'legal-text list-scroll' },
        firstRun ? el('p', { className: 'legal-intro', textContent: t('legal.intro') }) : null,
        prevails(),
        ...(firstRun ? [[t('legal.disclaimerTitle'), DISCLAIMER()], [t('legal.privacyTitle'), PRIVACY()]] : [[title, sections]]).flatMap(([heading, list]) => [
          firstRun ? el('h3', { className: 'legal-heading', textContent: heading }) : null,
          ...list.map(([h, text]) => el('section', {}, el('h4', { textContent: h }), el('p', { textContent: text })))
        ]));
      root.append(el('div', { className: 'files legal' },
        el('div', { className: 'files-head' }, el('h2', { textContent: firstRun ? t('legal.termsTitle') : title })),
        this.scroller));
    },
    scroll(delta) {
      this.scroller.scrollBy({ top: delta * this.scroller.clientHeight * 0.4, behavior: 'smooth' });
    },
    async handle(action) {
      switch (action) {
        case 'b':
          // Inside the setup assistant: back to its first step (language). Declining stays on the menu button.
          if (!firstRun || !onBack) return;
          removePage(this);
          sound.back();
          return onBack();
        case 'up': return this.scroll(-1);
        case 'down': return this.scroll(1);
        case 'a':
          if (!firstRun) return;
          await attempt(() => api.setSetting('acceptedTerms', TERMS_VERSION), t('legal.notSaved'));
          state.info.acceptedTerms = TERMS_VERSION;
          removePage(this);
          sound.select();
          toast(t('legal.accepted'));
          return render();
        case 'start':
          if (!firstRun) return;
          // Declined: CouchHUD steps aside. As the shell it hands over to the normal desktop for good.
          if (state.info.isShell) {
            await attempt(() => api.setShell(false));
            toast(t('legal.declinedShell'));
            return attempt(() => api.enterPcMode());
          }
          toast(t('legal.declinedQuit'));
          return attempt(() => api.quit());
      }
    },
    hints() {
      return firstRun ? [['↕', t('legal.hint.read')], ['A', t('legal.hint.accept')], ...(onBack ? [['B', t('legal.hint.back')]] : []), ['≡', t('legal.hint.decline')]] : [['↕', t('legal.hint.read')], ['B', t('legal.hint.back')]];
    }
  };
}

export const disclaimerPage = () => textPage({ id: 'disclaimer', title: t('legal.disclaimerTitle'), sections: DISCLAIMER() });
export const privacyPage = () => textPage({ id: 'privacy', title: t('legal.privacyTitle'), sections: PRIVACY() });

export const termsAccepted = () => (state.info.acceptedTerms ?? 0) >= TERMS_VERSION;

/** The first-start notice as a page of its own; the setup assistant opens it as its terms step (same text, same accept and decline). */
export const termsPage = ({ onBack } = {}) => textPage({ id: 'terms', title: t('legal.termsTitle'), firstRun: true, onBack });

/** First start (or changed texts): the user confirms before using CouchHUD. */
export function showTermsIfNeeded() {
  if (termsAccepted()) return;
  state.stack.push(termsPage());
  render();
}
