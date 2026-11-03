// Rules for settings that change from outside the page (the phone remote, a tool); no DOM, checked in scripts/ui-logic-check.mjs.

const LANGUAGES = ['auto', 'de', 'en'];

/**
 * May the page reload for a new language now? Not while a code is being typed, a dialog or menu (overlay) is open or the profile picker
 * waits: the reload would throw away what the person is doing (a code half typed, a question unanswered). It is then put off and made up.
 */
export function languageReloadWaits({ codeEntry, overlay, pickerHold } = {}) {
  return !!(codeEntry || overlay || pickerHold);
}

/**
 * Did the language setting change under the page? Only between known values: the page was started with `current` (the host's
 * languageSetting), the host now says `next`. Unknown or missing values never count (no reload on a half answer).
 */
export function languageChanged(current, next) {
  return LANGUAGES.includes(current) && LANGUAGES.includes(next) && current !== next;
}
