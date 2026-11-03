'use strict';

// The same as SkinCss in Core (src/CouchHud.Core/Skins/SkinCss.cs), for the preview server and the checks. Keep the two identical: both read
// tests/skin-css-cases.json (scripts/ui-logic-check.mjs here, SkinCssLayerTests there).
//
// A skin's stylesheet may not use @import (a second stylesheet nobody checked, with layer() and supports()) and may not use @layer (a layer declared first
// beats the important rules of everything after it, so a skin could take the core's pins apart). Both are removed here, reading the text the way CSS
// Syntax Level 3 tokenizes it: comments, strings (which end at a newline, a carriage return or a form feed), unquoted url( ... ) tokens (which run to the
// closing bracket and ignore comment and quote characters inside), numbers with units, escapes. A @layer block is unwrapped into ordinary rules.
// Then a backstop: whatever is left is searched, escapes resolved, anywhere (strings, comments, url(), anything) for "@layer" or "@import"; if one is there the
// whole stylesheet is refused (an honest skin has no reason to contain those words, so a false positive is fine). The text is read as UTF-8 only: the host sends
// charset=utf-8, an @charset naming another charset refuses the stylesheet, and so do control characters (ESC, NUL ...) that another charset could use to hide text.

const isNl = (c) => c === '\n' || c === '\r' || c === '\f';
const isWs = (c) => c === ' ' || c === '\t' || isNl(c);
const isHex = (c) => c !== undefined && /^[0-9a-fA-F]$/.test(c);
const isDigit = (c) => c !== undefined && c >= '0' && c <= '9';
const isNameStart = (c) => c !== undefined && (/^[A-Za-z_]$/.test(c) || c.charCodeAt(0) > 127 || c === '\0');
const isNameChar = (c) => isNameStart(c) || isDigit(c) || c === '-';
const validEscape = (css, i) => css[i] === '\\' && i + 1 < css.length && !isNl(css[i + 1]);

/** The index after an escape whose backslash is at i - 1 (i is the first character after it). */
function escapeEnd(css, i) {
  if (i >= css.length) return i;
  let j = i;
  let d = 0;
  while (j < css.length && d < 6 && isHex(css[j])) { j++; d++; }
  if (d > 0) {
    if (j < css.length && isWs(css[j])) { if (css[j] === '\r' && css[j + 1] === '\n') j++; j++; }
    return j;
  }
  return i + 1;
}

function startsIdent(css, i) {
  const c = css[i];
  if (c === '-') {
    const n = css[i + 1];
    return n === '-' || isNameStart(n) || validEscape(css, i + 1);
  }
  return isNameStart(c) || validEscape(css, i);
}

/** The index after a maximal identifier that starts at i. */
function identEnd(css, i) {
  let j = i;
  while (j < css.length) {
    if (isNameChar(css[j])) j++;
    else if (validEscape(css, j)) j = escapeEnd(css, j + 1);
    else break;
  }
  return j;
}

/** The identifier text with escapes resolved. */
function decodeIdent(css, start, end) {
  let out = '';
  let i = start;
  while (i < end) {
    if (css[i] === '\\' && i + 1 < end) {
      let j = i + 1;
      let hex = '';
      while (j < end && hex.length < 6 && isHex(css[j])) hex += css[j++];
      if (hex) {
        const n = parseInt(hex, 16);
        out += n > 0 && n <= 0x10ffff && (n < 0xd800 || n > 0xdfff) ? String.fromCodePoint(n) : '�';
        if (j < end && isWs(css[j])) { if (css[j] === '\r' && css[j + 1] === '\n') j++; j++; }
      } else { out += css[i + 1]; j = i + 2; }
      i = j;
    } else { out += css[i]; i++; }
  }
  return out;
}

/** The index after a string that starts at i (the quote). A newline, carriage return or form feed ends it (a bad string); that character is not part of it. */
function skipString(css, i) {
  const q = css[i];
  let j = i + 1;
  while (j < css.length) {
    const c = css[j];
    if (c === q) return j + 1;
    if (isNl(c)) return j;
    if (c === '\\') {
      if (j + 1 >= css.length) return css.length;
      if (isNl(css[j + 1])) j += css[j + 1] === '\r' && css[j + 2] === '\n' ? 3 : 2;
      else j = escapeEnd(css, j + 1);
    } else j++;
  }
  return css.length;
}

/** The rest of a bad url: to the closing bracket (escapes honoured) or the end. */
function badUrlRest(css, i) {
  let j = i;
  while (j < css.length) {
    if (css[j] === ')') return j + 1;
    if (validEscape(css, j)) j = escapeEnd(css, j + 1);
    else j++;
  }
  return css.length;
}

/** The index after an unquoted url token whose body starts at i (after "url(" and the white space). */
function skipUrlBody(css, i) {
  let j = i;
  while (j < css.length) {
    const c = css[j];
    if (c === ')') return j + 1;
    if (isWs(c)) {
      while (j < css.length && isWs(css[j])) j++;
      if (j >= css.length) return j;
      return css[j] === ')' ? j + 1 : badUrlRest(css, j);
    }
    const code = c.charCodeAt(0);
    if (c === '"' || c === "'" || c === '(' || code <= 8 || code === 0x0b || (code >= 0x0e && code <= 0x1f) || code === 0x7f) return badUrlRest(css, j + 1);
    if (c === '\\') {
      if (!validEscape(css, j)) return badUrlRest(css, j + 1);
      j = escapeEnd(css, j + 1);
    } else j++;
  }
  return css.length;
}

/** One lexical unit that is not structure: a comment, a string, an identifier (with a url token when it is url( ), a number with its unit, an escape, or one character. Returns the end. */
function atomEnd(css, i) {
  const c = css[i];
  if (c === '/' && css[i + 1] === '*') {
    const e = css.indexOf('*/', i + 2);
    return e < 0 ? css.length : e + 2;
  }
  if (c === '"' || c === "'") return skipString(css, i);
  if (startsIdent(css, i)) {
    const e = identEnd(css, i);
    if (css[e] === '(' && decodeIdent(css, i, e).toLowerCase() === 'url') {
      let j = e + 1;
      while (j < css.length && isWs(css[j])) j++;
      if (css[j] === '"' || css[j] === "'") return e + 1; // url( "...": an ordinary function with a string
      return skipUrlBody(css, j);
    }
    return e;
  }
  if (isDigit(c) || ((c === '+' || c === '-') && (isDigit(css[i + 1]) || (css[i + 1] === '.' && isDigit(css[i + 2])))) || (c === '.' && isDigit(css[i + 1]))) {
    let j = i;
    if (css[j] === '+' || css[j] === '-') j++;
    while (isDigit(css[j])) j++;
    if (css[j] === '.' && isDigit(css[j + 1])) { j++; while (isDigit(css[j])) j++; }
    if ((css[j] === 'e' || css[j] === 'E') && (isDigit(css[j + 1]) || ((css[j + 1] === '+' || css[j + 1] === '-') && isDigit(css[j + 2])))) { j += 2; while (isDigit(css[j])) j++; }
    if (startsIdent(css, j)) j = identEnd(css, j);
    else if (css[j] === '%') j++;
    return j;
  }
  if (c === '\\') return validEscape(css, i) ? escapeEnd(css, i + 1) : i + 1;
  return i + 1;
}

/** Skips an at-rule's prelude the way CSS does: to the semicolon (consumed) or to the opening brace (consumed, reported); brackets and nested blocks are balanced. */
function skipStatement(css, start) {
  const closers = [];
  let i = start;
  while (i < css.length) {
    const c = css[i];
    if (c === '(') { closers.push(')'); i++; }
    else if (c === '[') { closers.push(']'); i++; }
    else if (c === '{') {
      if (!closers.length) return { end: i + 1, block: true };
      closers.push('}');
      i++;
    } else if (c === ')' || c === ']' || c === '}') {
      if (closers.length && closers[closers.length - 1] === c) closers.pop();
      else if (!closers.length && c === '}') return { end: i, block: false };
      i++;
    } else if (c === ';' && !closers.length) return { end: i + 1, block: false };
    else i = atomEnd(css, i);
  }
  return { end: css.length, block: false };
}

/** @import statements dropped, @layer statements dropped, @layer blocks unwrapped into ordinary rules; any @charset dropped (the host forces UTF-8). */
function stripLayersAndImports(css) {
  return strip(css).css;
}

/** The same with the finding that an @charset named something other than utf-8: { css, badCharset }. */
function strip(css) {
  let out = '';
  let badCharset = false;
  const blocks = [];
  let i = 0;
  while (i < css.length) {
    const c = css[i];
    if (c === '@' && startsIdent(css, i + 1)) {
      const e = identEnd(css, i + 1);
      const name = decodeIdent(css, i + 1, e).toLowerCase();
      if (name === 'import') i = skipStatement(css, e).end;
      else if (name === 'charset') {
        const st = skipStatement(css, e);
        if (css.slice(e, st.end).replace(/;$/, '').trim().toLowerCase() !== '"utf-8"') badCharset = true;
        i = st.end;
      }
      else if (name === 'layer') {
        const s = skipStatement(css, e);
        if (s.block) blocks.push(true);
        i = s.end;
      } else { out += css.slice(i, e); i = e; }
    } else if (c === '{') { blocks.push(false); out += c; i++; }
    else if (c === '}') { if (blocks.length && blocks.pop()) { i++; continue; } out += c; i++; }
    else { const e = atomEnd(css, i); out += css.slice(i, e); i = e; }
  }
  return { css: out, badCharset };
}

/** Whether "@layer" or "@import" is anywhere in the text, escapes resolved, upper or lower case: in a string, a comment, a url, anywhere. */
function mentionsLayerOrImport(css) {
  let flat = '';
  let i = 0;
  while (i < css.length) {
    if (css[i] === '\\' && i + 1 < css.length) {
      if (isNl(css[i + 1])) { i += css[i + 1] === '\r' && css[i + 2] === '\n' ? 3 : 2; continue; }
      const e = escapeEnd(css, i + 1);
      flat += decodeIdent(css, i, e);
      i = e;
    } else { flat += css[i]; i++; }
  }
  const low = flat.toLowerCase();
  return low.includes('@layer') || low.includes('@import');
}

// An absolute or protocol-relative web address, not preceded by a letter or digit (the same pattern as SkinCss.External in Core).
const EXTERNAL = /(?<![A-Za-z0-9_-])(?:https?:)?\/\/(?!(?:local|art|app)\.couchhud(?:[/:"')\s]|$)|www\.w3\.org\/)[^\s"')]+/gi;

// C0 control characters other than tab, line feed, form feed and carriage return. In a stylesheet decoded from another charset (iso-2022-jp: ESC ( B
// produces no character at all) they would hide "@layer" or "https://" from this scan while the browser reads them; an honest skin has none.
const FORBIDDEN_CONTROL = /[\u0000-\u0008\u000b\u000e-\u001f]/;

/**
 * The stylesheet as the host serves it: no @import, no @layer, no @charset, no web addresses. Refused whole ({ css: '', refused: true }) when a control
 * character other than tab, LF, FF and CR is in the text, when an @charset names anything but utf-8, or when a mention of @layer or @import survives.
 * The host also sends "charset=utf-8" in the header, which beats an @charset, so the text read here is the text the browser reads.
 */
function sanitize(css) {
  if (FORBIDDEN_CONTROL.test(css)) return { css: '', refused: true };
  const { css: clean, badCharset } = strip(css);
  if (badCharset || mentionsLayerOrImport(clean)) return { css: '', refused: true };
  return { css: clean.replace(EXTERNAL, 'about:blank'), refused: false };
}

/** The same for the bytes of the file: a UTF-16 byte order mark or bytes that are not valid UTF-8 refuse the stylesheet (a UTF-8 byte order mark is dropped). */
function sanitizeBytes(buf) {
  const b = Buffer.from(buf);
  if (b.length >= 2 && ((b[0] === 0xff && b[1] === 0xfe) || (b[0] === 0xfe && b[1] === 0xff))) return { css: '', refused: true };
  let text;
  try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(b); } catch { return { css: '', refused: true }; }
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  return sanitize(text);
}

module.exports = { stripLayersAndImports, mentionsLayerOrImport, sanitize, sanitizeBytes };
