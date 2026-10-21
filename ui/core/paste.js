// "Paste from PC" in the on-screen keyboard: pure rules (tested by scripts/ui-logic-check.mjs).

/** The pasted text as the field takes it: cut to the field's limit (0: none). */
export function clampPaste(text, maxLength = 0) {
  const value = String(text ?? '');
  return maxLength > 0 && value.length > maxLength ? value.slice(0, maxLength) : value;
}

/**
 * What the remote and the phone may see of the open field: nothing of a secret one, and nothing of a pasted one until it was edited by hand
 * (a pasted value is often a token even where the field is not marked secret).
 */
export function remoteView(page) {
  const hidden = !!(page?.secret || page?.pasted);
  return { secret: hidden, text: hidden ? '' : (page?.text ?? '') };
}
