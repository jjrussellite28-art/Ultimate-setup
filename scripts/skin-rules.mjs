// Rules for a skin's stylesheet that still matter for the tab bar (pure, no files; the tests are in scripts/ui-logic-check.mjs, the check over
// real skin folders is scripts/skin-check.mjs).
//
// Since 0.7 the bar is a closed shadow root in the top layer (ui/core/tab-bar.js): no selector of a skin reaches a tab, and what a skin may
// say about the bar goes through a small set of custom properties that are cleaned and clamped in JS (ui/core/tab-skin.js). So the old
// lint for hiding, squashing, covering and recolouring tabs is gone; what is left is
//   - problems (these fail the check): what still reaches the rest of the page and could be abused: a @font-face that shrinks or re-measures
//     letters (it hits the status area and every other text) or replaces an installed font under its own name, and html/body rules that
//     fade, hide or mask the whole page;
//   - notes (printed, never failing): rules that name the old bar (#tab-list, #tabs, .tab-more, .shoulder, #top as a box) and so do nothing
//     any more (the skin should say it with --tab-*), and --tab-* / --bar-* values that the core will clamp or ignore.
import { SYSTEM_FONTS } from '../ui/core/tab-skin.js';

/** CSS escapes undone (\74 ab-list, \#, \-): a selector spelled with escapes is the selector it names. */
const unescape = (css) => css.replace(/\\([0-9a-fA-F]{1,6})\s?/g, (_, hex) => String.fromCodePoint(Math.min(parseInt(hex, 16) || 0xfffd, 0x10ffff))).replace(/\\(.)/gs, '$1');
const strip = (css) => unescape(css.replace(/\/\*[\s\S]*?\*\//g, ''));

/** The declarations of a rule body as [property, value] pairs (important flags dropped). */
function declarations(body) {
  return body.split(';').map((d) => {
    const at = d.indexOf(':');
    return at < 0 ? null : [d.slice(0, at).trim().toLowerCase(), d.slice(at + 1).replace(/!important/i, '').trim()];
  }).filter(Boolean);
}

/** Whether a selector ends in html, body or :root (with attributes or classes), the ancestors of everything. */
const reachesPage = (sel) => {
  const last = sel.split(/[\s>+~]+/).filter(Boolean).pop() ?? '';
  return /^(html|body|:root)(?![\w-])/i.test(last) && !/::|:(before|after)\b/i.test(last);
};

/** What a declaration on html or body does to the whole page: hides, fades or masks it (the bar stays, the rest of the screen does not). */
function pageProblem(prop, value) {
  const v = value.trim();
  if (prop === 'opacity' && Number(v) < 0.5) return 'fades the whole page';
  if (prop === 'visibility' && /^(hidden|collapse)\b/i.test(v)) return 'hides the whole page';
  if (prop === 'display' && /^(none|contents)\b/i.test(v)) return 'removes the whole page';
  if (prop === 'content-visibility' && /^hidden\b/i.test(v)) return 'hides the whole page';
  if (prop === 'filter' && /(opacity|brightness|contrast)\(\s*(0(\.[0-4]\d*)?|\.[0-4]\d*)\s*(%|\))/i.test(v)) return 'blacks out or fades the whole page';
  if (/^(clip-path|(-webkit-)?mask(-image)?)$/.test(prop) && !/^none$/i.test(v)) return 'cuts or masks the whole page';
  if (prop === 'mix-blend-mode' && !/^normal\b/i.test(v)) return 'blends the whole page away';
  return null;
}

const SYSTEM_LOWER = new Set(SYSTEM_FONTS.map((n) => n.toLowerCase()));
const familyOf = (v) => v.trim().replace(/^(["'])(.*)\1$/, '$2').trim().replace(/\s+/g, ' ').toLowerCase();

/** @font-face rules that shrink or re-measure letters (size-adjust under 80%, ascent, descent or line gap overrides) or stand in for an installed font. */
function fontFaceProblems(body) {
  const out = [];
  for (const [prop, value] of declarations(body)) {
    const pct = /^(\d*\.?\d+)%$/.exec(value);
    if (prop === 'size-adjust' && pct && Number(pct[1]) < 80) out.push(`@font-face: size-adjust: ${value} makes letters small (below 80% is flagged; the tab bar ignores fonts a skin declares, the status area and the rest do not)`);
    if (/^(ascent|descent|line-gap)-override$/.test(prop) && !/^normal$/i.test(value)) out.push(`@font-face: ${prop}: ${value} changes the measures of the font (flagged)`);
    if (prop === 'font-family' && SYSTEM_LOWER.has(familyOf(value))) out.push(`@font-face: "${familyOf(value)}" is an installed system font (a skin brings its own font under its own name; the tab bar drops a system name a skin declares)`);
  }
  return out;
}

/** The problems of one stylesheet (an empty list is fine): the rules that still matter, see the top. */
export function tabProblems(css) {
  const problems = [];
  const text = strip(String(css));
  for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    if (/^@font-face(?![\w-])/i.test(selector)) problems.push(...fontFaceProblems(m[2]));
    if (selector.startsWith('@')) continue;
    if (selector.split(',').some((x) => reachesPage(x.trim()))) {
      for (const [prop, value] of declarations(m[2])) {
        const why = pageProblem(prop, value);
        if (why) problems.push(`"${selector}": ${prop}: ${value} ${why}`);
      }
    }
  }
  return problems;
}

// The old bar: ids, classes and the attribute selectors that named them.
const OLD_BAR = /#tab-list|#tabs(?![\w-])|\.tab-more\b|\.shoulder\b|\[data-tab\b|\.pinned\b/i;
const OLD_IDS = ['tab-list', 'tabs'];
// The values that are a plain number of rem, px or em (anything else, calc() included, is not read).
const NUMERIC = ['--tab-size', '--tab-weight', '--tab-weight-active', '--tab-letter', '--tab-gap', '--tab-pad-x', '--tab-pad-y', '--tab-radius', '--tab-cut', '--tab-underline',
  '--tab-glow', '--bar-radius', '--bar-pad', '--bar-pad-top', '--tab-glyph-radius', '--tab-glyph-size'];
const NUMBER = /^\s*-?\d*\.?\d+\s*(rem|px|em)?\s*$/i;

/** Whether a selector names the old bar (its tabs, arrows or LB/RB glyphs), or styles #top as a box (its padding, background, position). */
function namesOldBar(raw) {
  // :not(.shoulder) and the like name what the rule leaves out, not the bar.
  const sel = raw.replace(/:not\([^)]*\)/gi, '');
  if (OLD_BAR.test(sel)) return true;
  for (const m of sel.matchAll(/\[\s*id\s*([~|^$*]?=)\s*["']?([^"'\]\s]*)/gi)) {
    const value = m[2].toLowerCase();
    if (OLD_IDS.some((id) => (m[1] === '=' ? id === value : id.includes(value)))) return true;
  }
  const last = sel.split(/[\s>+~]+/).filter(Boolean).pop() ?? '';
  return /^#top(?![\w-])/i.test(last) && !/::/.test(last);
}

/**
 * Notes for the review and for the skin's author (never failing): rules that name the old bar and do nothing since 0.7, and --tab-* / --bar-*
 * values the core will clamp or ignore.
 */
export function tabNotes(css) {
  const notes = [];
  const text = strip(String(css));
  for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    if (selector.startsWith('@')) continue;
    const old = selector.split(',').map((s) => s.trim()).filter(namesOldBar);
    if (old.length) notes.push(`"${old.join(', ')}": no effect since 0.7 (the tab bar is not reachable by selectors; use the --tab-* and --bar-* custom properties, see docs/SKINS.md)`);
    for (const [prop, value] of declarations(m[2])) {
      if (NUMERIC.includes(prop) && !NUMBER.test(value)) notes.push(`${prop}: ${value} is not a plain number (with rem, px or em): the default is used`);
    }
  }
  return notes;
}
