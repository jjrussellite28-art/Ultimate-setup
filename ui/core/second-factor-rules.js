// The second factor's question on the TV (0.7): the text of the dialog. Pure, so a check pins it.

/**
 * @param {{ label?: string, number?: number, reachable?: boolean, lockedOutSeconds?: number }} info the host's "twofa" answer
 * @param {number} left seconds until the question ends
 * @param {(key: string, vars?: object) => string} t
 */
export function secondFactorText(info, left, t) {
  // Too many wrong answers: the phone way is shut for a while, only the Windows administrator works (the question stays for that).
  if ((info?.lockedOutSeconds ?? 0) > 0) return t('twofa.locked', { minutes: Math.max(1, Math.ceil(info.lockedOutSeconds / 60)) });
  const what = info?.detail ? `${info?.label ?? ''}: ${info.detail}` : (info?.label ?? '');
  const base = t('twofa.message', { label: what, number: info?.number ?? '', seconds: Math.max(0, left) });
  return info?.reachable ? base : `${base} ${t('twofa.noPhone')}`;
}

/** Whether the number is two digits (the host makes 10 to 99; anything else is shown as nothing rather than as a wrong number). */
export const isQuestionNumber = (n) => Number.isInteger(n) && n >= 10 && n <= 99;
