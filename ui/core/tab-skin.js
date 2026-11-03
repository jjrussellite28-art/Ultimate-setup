// The skin API of the tab bar: what a skin may say about the bar, and the clamps every value goes through before it is applied.
// Pure (no DOM; tested by scripts/ui-logic-check.mjs). The bar lives in a closed shadow root (core/tab-bar.js), so a skin's selectors
// cannot reach a tab; the only way in is the small set of custom properties below, read from the host, cleaned here, and set inline inside
// the shadow root under other names (--s-*). A value that is missing, not understood or out of range becomes the default or the nearest
// allowed value; a colour that could not be read becomes a readable one (readableColor from tab-fit.js).
import { flatten, parseColor, readableColor, rgbaText } from './tab-fit.js';

/** The custom properties a skin may set (on :root[data-skin="..."]); everything else about the bar is the core's. */
export const BAR_SKIN_VARS = Object.freeze([
  '--tab-color', '--tab-color-active', '--tab-accent', '--tab-bg', '--tab-bg-active', '--tab-glow', '--tab-glow-color',
  '--tab-font', '--tab-size', '--tab-weight', '--tab-weight-active', '--tab-letter', '--tab-case',
  '--tab-gap', '--tab-pad-x', '--tab-pad-y', '--tab-radius', '--tab-cut', '--tab-underline', '--tab-min-width',
  '--bar-bg', '--bar-border', '--bar-radius', '--bar-pad', '--bar-glass', '--bar-pad-top', '--tab-glyph-bg', '--tab-glyph-color', '--tab-glyph-border', '--tab-glyph-radius', '--tab-glyph-size'
]);

/** Ranges in rem (em for the letter spacing), weight in hundreds, glow as a factor of 1rem of blur. */
export const BAR_RANGES = Object.freeze({
  size: [0.9, 1.9, 1.35], weight: [300, 800, 600], letter: [-0.02, 0.12, 0], gap: [0.2, 2.4, 1.8], padX: [0, 2, 0], padY: [0.1, 0.8, 0.3],
  glyphRadius: [0, 1, 0.4], glyphSize: [0.55, 1, 0.8], radius: [0, 2, 0], cut: [0, 0.9, 0], underline: [0, 0.3, 0.18], barPad: [0, 0.6, 0], barRadius: [0, 2.5, 0], padTop: [0, 3, 2], glow: [0, 1.2, 0]
});

/** The alpha of the core's backing behind the bar (the theme's --bg): the letters are checked against it over black and over white. */
export const SCRIM_ALPHA = 0.8;

/** The most blur (rem) the glow of the active tab gets. */
export const GLOW_MAX_REM = 1.2;

const DEFAULT_FONT = '"Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif';

/** Fonts a tab may be set in by name: installed with Windows or Office, so a skin cannot bring its own through @font-face (see fontStack). */
export const SYSTEM_FONTS = Object.freeze([
  'Segoe UI', 'Segoe UI Variable Display', 'Segoe UI Variable Text', 'Segoe UI Variable Small', 'Segoe UI Light', 'Segoe UI Semilight', 'Segoe UI Semibold',
  'Segoe UI Black', 'Segoe UI Emoji', 'Segoe UI Symbol', 'Bahnschrift', 'Arial', 'Arial Narrow', 'Arial Black', 'Calibri', 'Cambria', 'Candara', 'Consolas',
  'Constantia', 'Corbel', 'Courier New', 'Franklin Gothic Medium', 'Georgia', 'Impact', 'Lucida Console', 'Lucida Sans Unicode', 'Microsoft Sans Serif',
  'Palatino Linotype', 'Segoe Print', 'Segoe Script', 'Sylfaen', 'Tahoma', 'Times New Roman', 'Trebuchet MS', 'Verdana', 'Agency FB', 'Century Gothic',
  'Gill Sans MT', 'Rockwell', 'Bookman Old Style'
]);
const SYSTEM_BY_LOWER = new Map(SYSTEM_FONTS.map((n) => [n.toLowerCase(), n]));
/** Generic families: a keyword, never a name, so no @font-face can stand in for them. */
export const GENERIC_FONTS = Object.freeze(['system-ui', 'sans-serif', 'serif', 'monospace', 'ui-sans-serif', 'ui-serif', 'ui-monospace', 'ui-rounded']);

/** The family name as the font loader sees it: quotes off, spaces collapsed, lower case. */
export const familyKey = (name) => String(name ?? '').trim().replace(/^(["'])(.*)\1$/, '$2').trim().replace(/\s+/g, ' ').toLowerCase();

/**
 * A font stack for the tabs from whatever the skin wrote: generic keywords and the names in SYSTEM_FONTS stay, everything else goes (a
 * skin's own font, a quoted "sans-serif"). A name the skin declares itself with @font-face (`taken`: the lower-case family names of
 * document.fonts) goes as well: such a face would replace the installed font, with its own size-adjust and ascent-override. At most six
 * entries; the stack always ends in system-ui and sans-serif; an empty result is the core's default stack (cleaned the same way).
 */
export function fontStack(raw, taken = new Set(), fallback = DEFAULT_FONT) {
  const out = [];
  for (const part of String(raw ?? '').split(',')) {
    const text = part.trim();
    const key = familyKey(text);
    if (!key) continue;
    const quoted = /^["']/.test(text);
    if (!quoted && GENERIC_FONTS.includes(key)) out.push(key);
    else if (SYSTEM_BY_LOWER.has(key) && !taken.has(key)) out.push(`"${SYSTEM_BY_LOWER.get(key)}"`);
    if (out.length >= 6) break;
  }
  // Nothing usable: the core's own stack, with the same rules (a name the skin declared is not in it either).
  if (!out.length) return fallback === null ? '' : fontStack(fallback, taken, null) || 'system-ui, sans-serif';
  for (const g of ['system-ui', 'sans-serif']) if (!out.includes(g)) out.push(g);
  return out.join(', ');
}

/** A number of rem/px/em (or unitless) as a number clamped to [min, max]; `fallback` for anything else (calc(), text, nothing). */
export function lengthIn(raw, [min, max, fallback], remPx, { bare = 'rem' } = {}) {
  const m = /^\s*(-?\d*\.?\d+)\s*(rem|px|em)?\s*$/i.exec(String(raw ?? ''));
  if (!m) return fallback;
  const unit = (m[2] ?? bare).toLowerCase();
  let v = Number(m[1]);
  if (unit === 'px') v /= remPx > 0 ? remPx : 20;
  return Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : fallback;
}

const round = (n) => Math.round(n * 1000) / 1000;

/**
 * Reads the skin's bar values. `get(name)` returns the computed value of a custom property ("" when not set); `ctx.toRgba(text)` turns
 * any CSS colour into rgba text (null when it is none); `ctx.remPx` is the core's rem in px; `ctx.taken` the fonts a skin declared itself.
 * Returns the cleaned values as the custom properties the shadow root's stylesheet reads (--s-*), all of them, always.
 */
export function readBarSkin(get, ctx) {
  const remPx = ctx.remPx > 0 ? ctx.remPx : 20;
  const rgba = (name) => {
    const text = String(get(name) ?? '').trim();
    return text ? parseColor(ctx.toRgba(text)) : null;
  };
  const fg = rgba('--fg') ?? [242, 245, 250, 1];
  // The core's own backing of the bar: the theme's --bg at SCRIM_ALPHA, always there (the page under the bar is anybody's, a skin's layer of the same colour as
  // the letters included). Skins tint the bar through --bar-bg; the backing is not theirs to take away.
  const bg = rgba('--bg') ?? [7, 9, 15, 1];
  const scrim = [bg[0], bg[1], bg[2], SCRIM_ALPHA];
  const barBg = rgba('--bar-bg');
  const tabBg = rgba('--tab-bg');
  const tabBgActive = rgba('--tab-bg-active');
  // What the letters stand on: the tab's fill over the bar's fill over the backing over whatever the page paints, which can be black or white at worst.
  const under = (fill, base) => rgbaText(flatten([fill ?? [0, 0, 0, 0], barBg ?? [0, 0, 0, 0], scrim, base]));
  const fallback = rgbaText(fg);
  const text = (c, fill) => {
    const fix = readableColor(rgbaText(c), under(fill, [0, 0, 0, 1]), fallback) ?? readableColor(rgbaText(c), under(fill, [255, 255, 255, 1]), fallback);
    return fix ?? rgbaText(c);
  };
  const muted = rgba('--muted') ?? [fg[0], fg[1], fg[2], 0.7];
  const color = text(rgba('--tab-color') ?? muted, tabBg);
  const active = text(rgba('--tab-color-active') ?? fg, tabBgActive);
  const accent = text(rgba('--tab-accent') ?? rgba('--accent') ?? fg, tabBg);
  const size = lengthIn(get('--tab-size'), BAR_RANGES.size, remPx);
  const weight = Math.round(lengthIn(get('--tab-weight'), BAR_RANGES.weight, remPx, { bare: 'rem' }) / 100) * 100;
  const weightActiveRaw = String(get('--tab-weight-active') ?? '').trim();
  const weightActive = weightActiveRaw ? Math.round(lengthIn(weightActiveRaw, BAR_RANGES.weight, remPx) / 100) * 100 : weight;
  const glow = lengthIn(get('--tab-glow'), BAR_RANGES.glow, remPx);
  const glowColor = rgba('--tab-glow-color') ?? rgba('--accent-glow') ?? rgba('--accent') ?? fg;
  const letterPx = String(get('--tab-letter') ?? '');
  const letter = lengthIn(/px\s*$/i.test(letterPx) ? `${parseFloat(letterPx) / (size * remPx)}` : letterPx, BAR_RANGES.letter, remPx, { bare: 'em' });
  const rem = (n) => `${round(n)}rem`;
  const border = rgba('--bar-border');
  const glyph = rgba('--tab-glyph-bg') ?? [255, 255, 255, 0.14];
  const glyphBorder = rgba('--tab-glyph-border');
  const glyphColor = text(rgba('--tab-glyph-color') ?? fg, glyph);
  return {
    '--s-scrim': rgbaText(scrim),
    '--s-font': fontStack(get('--tab-font') || get('--font'), ctx.taken),
    '--s-size': rem(size),
    '--s-weight': String(weight),
    '--s-weight-active': String(weightActive),
    '--s-letter': `${round(letter)}em`,
    '--s-case': /^\s*uppercase\s*$/i.test(String(get('--tab-case') ?? '')) ? 'uppercase' : 'none',
    '--s-color': color,
    '--s-color-active': active,
    '--s-accent': accent,
    '--s-bg': rgbaText(tabBg ?? [0, 0, 0, 0]),
    '--s-bg-active': rgbaText(tabBgActive ?? [0, 0, 0, 0]),
    '--s-glow': glow > 0.01 ? `0 0 ${rem(Math.min(GLOW_MAX_REM, glow))} ${rgbaText(glowColor)}` : 'none',
    '--s-gap': rem(lengthIn(get('--tab-gap'), BAR_RANGES.gap, remPx)),
    '--s-pad-x': rem(lengthIn(get('--tab-pad-x'), BAR_RANGES.padX, remPx)),
    '--s-pad-y': rem(lengthIn(get('--tab-pad-y'), BAR_RANGES.padY, remPx)),
    '--s-radius': rem(lengthIn(get('--tab-radius'), BAR_RANGES.radius, remPx)),
    '--s-cut': rem(lengthIn(get('--tab-cut'), BAR_RANGES.cut, remPx)),
    '--s-underline': rem(lengthIn(get('--tab-underline'), BAR_RANGES.underline, remPx)),
    '--s-bar-bg': rgbaText(barBg ?? [0, 0, 0, 0]),
    '--s-bar-border': rgbaText(border ?? [0, 0, 0, 0]),
    '--s-bar-border-w': border && border[3] > 0 ? '0.06rem' : '0rem',
    '--s-bar-radius': rem(lengthIn(get('--bar-radius'), BAR_RANGES.barRadius, remPx)),
    '--s-bar-pad': rem(lengthIn(get('--bar-pad'), BAR_RANGES.barPad, remPx)),
    '--s-pad-top': rem(lengthIn(get('--bar-pad-top'), BAR_RANGES.padTop, remPx)),
    '--s-glyph-bg': rgbaText(glyph),
    '--s-glyph-color': glyphColor,
    '--s-glyph-border': rgbaText(glyphBorder ?? [0, 0, 0, 0]),
    '--s-glyph-border-w': glyphBorder && glyphBorder[3] > 0 ? '0.1rem' : '0rem',
    '--s-glyph-radius': rem(lengthIn(get('--tab-glyph-radius'), BAR_RANGES.glyphRadius, remPx)),
    '--s-glyph-size': rem(lengthIn(get('--tab-glyph-size'), BAR_RANGES.glyphSize, remPx)),
    glass: /^\s*(1|on|true)\s*$/i.test(String(get('--bar-glass') ?? ''))
  };
}
