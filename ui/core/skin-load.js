// When is a skin "styled": its stylesheet has loaded (or failed, then the app's own rules apply), so what it declares can be read.
// Kept apart from skins.js so it can be tested with a fake <link>: only the link given and queueMicrotask are used.

/**
 * Makes a styler for one <link>. `style(href, onStyled)` points the link at `href` (null: no skin stylesheet) and calls onStyled once,
 * when that stylesheet is in; only for the skin asked for last. Asking again for the stylesheet that is still loading does not start
 * over: the load already under way reports (to the newest onStyled). Listeners of an abandoned switch are removed.
 */
export function skinStyler(link) {
  let token = 0;
  let wanted = null; // { href, done, onStyled, off }

  const finish = (t) => {
    if (t !== token || !wanted) return;
    wanted.done = true;
    wanted.off?.();
    wanted.off = null;
    wanted.onStyled();
  };

  const later = (t) => queueMicrotask(() => finish(t));

  const listen = (t) => {
    const done = () => finish(t);
    link.addEventListener('load', done);
    link.addEventListener('error', done);
    wanted.off = () => {
      link.removeEventListener('load', done);
      link.removeEventListener('error', done);
    };
  };

  return function style(href, onStyled) {
    href = href || null;
    if (href && link.getAttribute('href') === href) {
      if (wanted?.href === href) {
        // The same stylesheet again: still loading, the load under way reports (to this caller); loaded, report again.
        wanted.onStyled = onStyled;
        if (wanted.done) later(++token);
        return;
      }
      // Set before this styler knew it: loaded when the browser has the sheet, else wait for it.
      wanted?.off?.();
      const t = ++token;
      wanted = { href, done: false, onStyled, off: null };
      if (link.sheet) later(t);
      else listen(t);
      return;
    }
    wanted?.off?.();
    const t = ++token;
    wanted = { href, done: false, onStyled, off: null };
    if (!href) {
      link.removeAttribute('href');
      later(t);
      return;
    }
    listen(t);
    link.setAttribute('href', href);
  };
}
