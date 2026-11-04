'use strict';

// The same as SkinCss in Core (src/CouchHud.Core/Skins/SkinCss.cs), for the preview server and the checks. Keep the two identical: both read
// tests/skin-css-cases.json (scripts/ui-logic-check.mjs here, SkinCssLayerTests there).
//
// A skin's stylesheet may not use @import (a second stylesheet nobody checked, with layer() and supports()) and may not use @layer (a layer declared first
// beats the important rules of everything after it, so a skin could take the core's pins apart). Both are removed here, reading the text the way CSS
// Syntax Level 3 tokenizes it: comments, strings (which end at a newline, a carriage return or a form feed), unquoted url( ... ) tokens (which run to the
// closing bracket and ignore comment and quote characters inside), numbers with units, escapes. A @layer block is unwrapped into ordinary rules.
// Then a backstop: whatever is left is read again token by token for an at-keyword "@layer" or "@import" (escapes in the name resolved, any nesting); if one is
// there the whole stylesheet is refused. The words inside a comment, a string, a url( ) token or an identifier are not at-keywords and do not count. The text is read as UTF-8 only: the host sends
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

/**
 * Whether an at-keyword token "@layer" or "@import" (upper or lower case, escapes in the name resolved) is anywhere in the text, in any nesting.
 * The text is read again token by token, the way strip() reads it: what is inside a comment, a string, an unquoted url( ) token or an identifier
 * (a selector like .a\@layer, an attribute value, a custom property's string) is not an at-keyword to any parser and does not count. Text that two
 * removed statements joined into "@layer" is a token now and is found.
 */
function mentionsLayerOrImport(css) {
  let i = 0;
  while (i < css.length) {
    if (css[i] === '@' && startsIdent(css, i + 1)) {
      const e = identEnd(css, i + 1);
      const name = decodeIdent(css, i + 1, e).toLowerCase();
      if (name === 'layer' || name === 'import') return true;
      i = e;
    } else i = atomEnd(css, i);
  }
  return false;
}

/**
 * The text of a url or a string the way a URL parser reads it: CSS escapes decoded (a backslash and a line break is a continuation and vanishes), then TAB, LF and CR
 * deleted (the URL parser does that), a backslash made a slash (it does that for web addresses), leading control characters and spaces cut.
 */
function addressText(s) {
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c !== '\\') { out += c; continue; }
    const n = s[i + 1];
    if (n === undefined) break;
    if (isNl(n)) { if (n === '\r' && s[i + 2] === '\n') i++; i++; continue; }
    if (isHex(n)) {
      let j = i + 1;
      let hex = '';
      while (j < s.length && hex.length < 6 && isHex(s[j])) hex += s[j++];
      const code = parseInt(hex, 16);
      out += code > 0 && code <= 0x10ffff && (code < 0xd800 || code > 0xdfff) ? String.fromCodePoint(code) : '�';
      if (j < s.length && isWs(s[j])) { if (s[j] === '\r' && s[j + 1] === '\n') j++; j++; }
      i = j - 1;
    } else { out += n; i++; }
  }
  return out.replace(/[\t\n\r\u00AD\u034F\u180B-\u180D\u200B-\u200D\u2060\uFE00-\uFE0F\uFEFF]/g, '').replace(/\\/g, '/').replace(/^[\u0000- ]+/, '');
}

// The two virtual hosts of the app and the art host may be written out (the same exceptions as EXTERNAL).
const LOCAL_ADDRESS = /^(?:https?:)?\/\/(?:local|art|app)\.couchhud(?::\d+)?(?:[/?#]|$)/i;

// Schemes that reach out (or run code); a string that is no fetch (an attribute value like "user:nova", "a: b") may use any other scheme-like start, but not one of these.
const NETWORK_SCHEMES = new Set(['http', 'https', 'ftp', 'ftps', 'ws', 'wss', 'file', 'blob', 'filesystem', 'javascript', 'vbscript', 'view-source', 'chrome', 'chrome-extension', 'edge', 'ms-appx', 'ms-appx-web']);

/**
 * Whether a url token or string is an address that leaves the app, judged as the URL parser would read it (after addressText), whether or not it contains a literal "//":
 * it starts with "//", or with a scheme other than data: and about:blank (in a place that fetches: any scheme; elsewhere only a scheme of NETWORK_SCHEMES or one followed by a slash,
 * so "a: b" in a content string and "user:nova" in an attribute selector pass). The app's own hosts, written out, pass.
 */
function badAddress(raw, fetching) {
  const t = addressText(raw);
  // "//" and more slashes before a host; "// " (a decorative string) or "//" alone has no host.
  if (t.startsWith('//')) {
    const rest = t.replace(/^\/+/, '');
    return rest.length > 0 && rest[0] !== ' ' && !LOCAL_ADDRESS.test(t);
  }
  const m = /^([A-Za-z][A-Za-z0-9+.-]*):/.exec(t);
  if (!m) return false;
  const scheme = m[1].toLowerCase();
  if (scheme === 'data') return false;
  if (scheme === 'about') return !t.toLowerCase().startsWith('about:blank');
  if (LOCAL_ADDRESS.test(t)) return false;
  return fetching || NETWORK_SCHEMES.has(scheme) || t[m[0].length] === '/';
}

// Functions whose arguments (url tokens, strings) are fetched.
const URLISH = new Set(['url', 'src', 'image', 'image-set', '-webkit-image-set', 'cross-fade', '-webkit-cross-fade', 'element']);

/**
 * Whether any url token or any string in the sheet (custom properties included: var() carries a string into a fetch) is a bad address (badAddress). Fail closed: the whole
 * stylesheet is refused; an honest skin writes its paths plainly.
 */
function hasBadAddress(css) {
  const stack = [];
  let custom = false; // inside the value of a custom property (var() can carry its strings into a fetch): strings are judged as strictly as a url
  let braces = 0; // { } nested inside that value
  let last = ''; // the last character that was not white space or a comment
  let i = 0;
  while (i < css.length) {
    const c = css[i];
    if (c === '"' || c === "'") {
      const e = skipString(css, i);
      const body = css.slice(i + 1, e);
      if (badAddress(body.endsWith(c) ? body.slice(0, -1) : body, custom || (stack.length > 0 && stack[stack.length - 1]))) return true;
      i = e;
      last = c;
    } else if (startsIdent(css, i)) {
      const e = identEnd(css, i);
      const name = decodeIdent(css, i, e).toLowerCase();
      if (css[e] === '(') {
        if (name === 'attr') return true; // attr() can hand an attribute's text to url() or image-set()
        let j = e + 1;
        while (j < css.length && isWs(css[j])) j++;
        if (name === 'url' && css[j] !== '"' && css[j] !== "'") {
          const end = skipUrlBody(css, j);
          if (badAddress(css.slice(j, end).replace(/\s*\)?$/, ''), true)) return true;
          i = end;
          last = ')';
        } else { stack.push(URLISH.has(name)); i = e + 1; last = '('; }
      } else {
        if (name.startsWith('--') && stack.length === 0 && (last === '' || last === '{' || last === ';' || last === '}')) {
          let j = e;
          while (j < css.length && isWs(css[j])) j++;
          if (css[j] === ':') custom = true;
        }
        i = e;
        last = 'a';
      }
    } else if (c === '(') { stack.push(false); i++; last = c; }
    else if (c === ')') { stack.pop(); i++; last = c; }
    else if (c === '{') { if (custom) braces++; i++; last = c; }
    else if (c === '}') { if (custom) { if (braces > 0) braces--; else custom = false; } i++; last = c; }
    else if (c === ';') { if (custom && braces === 0 && stack.length === 0) custom = false; i++; last = c; }
    else {
      const e = atomEnd(css, i);
      if (!(c === '/' && css[i + 1] === '*') && !isWs(c)) last = c;
      i = e;
    }
  }
  return false;
}

// An absolute or protocol-relative web address, not preceded by a letter or digit (the same pattern as SkinCss.External in Core).
const EXTERNAL = /(?<![A-Za-z0-9_-])(?:https?:)?\/\/(?!(?:local|art|app)\.couchhud(?:[/:"') \t\n\r\f]|$)|www\.w3\.org\/)[^ \t\n\r\f"')]+/gi;

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
  // The text that is served is the text that was scanned: the address replacement can swallow a backslash, a quote or a comment mark and so change
  // what is a string or a comment, so strip and scan run again on its result until it changes nothing (a few rounds at most, else refused).
  let text = css;
  for (let round = 0; round < 6; round++) {
    const { css: clean, badCharset } = strip(text);
    if (badCharset || mentionsLayerOrImport(clean)) return { css: '', refused: true };
    const replaced = clean.replace(EXTERNAL, 'about:blank');
    // What the replacement leaves is judged as the URL parser reads it (escapes, deleted tabs and line breaks, backslashes): an address it could not see is refused.
    if (replaced === clean) return hasBadAddress(clean) ? { css: '', refused: true } : { css: clean, refused: false };
    text = replaced;
  }
  return { css: '', refused: true };
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
