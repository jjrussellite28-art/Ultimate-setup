# Catalog skins and the tab bar of 0.7

Since 0.7 a skin cannot reach the tab bar with selectors (closed shadow root in the top layer); it styles the bar through the `--tab-*` and `--bar-*` custom properties, see [SKINS.en.md](SKINS.en.md), section "The tab bar". Old rules for the bar still load but do nothing, so the six skins of the catalog look **plain** in the bar until they carry the variables below. (The catalog repository is not changed by CouchHUD's repository; this is the note for whoever updates it.) Each block goes onto the skin's own `:root[data-skin="user:<folder>"]` rule (or a new one); the old tab rules can be removed afterwards. `skin-check.mjs` lists them as notes.

The blocks were checked against the 0.6.4 look in the preview at 1920x1080 (the top strip of each skin, old build above, new build below): sizes, gaps, weights, colours, pills, cut corners, glow and the LB/RB symbols match; what differs is noted per skin.

## Deep Sea (deepsea)

Light type (300, 400 when active), a thin white line under the active tab and a soft blue glow; the LB/RB symbols are outlined boxes. The old rules `#tab-list li`, `#tab-list li.active` and `.glyph.shoulder` can go.

```css
:root[data-skin="user:deepsea"] {
  --tab-size: 1.2rem;
  --tab-weight: 300;
  --tab-weight-active: 400;
  --tab-letter: 0.04em;
  --tab-underline: 0.14rem;
  --tab-glow: 1;
  --tab-glow-color: rgba(160, 210, 255, 0.9);
  --tab-glyph-bg: transparent;
  --tab-glyph-border: rgba(255, 255, 255, 0.7);
  --tab-glyph-radius: 0.3rem;
  --tab-glyph-size: 0.62rem;
}
```

## Green Light (greenlight)

Pill tabs on a faint white fill, the active one filled white with dark type; round LB/RB symbols with a faint fill. The old rules `#tab-list`, `#tab-list li`, `#tab-list li.active` and `.glyph.shoulder` can go.

```css
:root[data-skin="user:greenlight"] {
  --tab-gap: 0.5rem;
  --tab-pad-x: 1.3rem;
  --tab-pad-y: 0.4rem;
  --tab-radius: 2rem;
  --tab-size: 1.1rem;
  --tab-underline: 0;
  --tab-bg: rgba(255, 255, 255, 0.08);
  --tab-bg-active: #fff;
  --tab-color-active: #111;
  --tab-glyph-bg: rgba(255, 255, 255, 0.08);
  --tab-glyph-radius: 0.5rem;
}
```

## Neon City (neoncity)

Upper case with wide letters (this came from `body { text-transform: uppercase; letter-spacing }` and now has its own variables), cyan type, a cut corner (`--tab-cut` replaces the `clip-path`), the active tab filled with the accent colour; the LB/RB symbols are square outlined boxes in the accent colour. The scanlines (`body::after`, multiply) no longer lie over the bar (that is the point of the top layer): the bar is clean, the rest of the page keeps them.

```css
:root[data-skin="user:neoncity"] {
  --tab-gap: 0.3rem;
  --tab-pad-x: 0.75rem;
  --tab-pad-y: 0.35rem;
  --tab-size: 1rem;
  --tab-color: var(--cyan);
  --tab-cut: 0.7rem;
  --tab-underline: 0;
  --tab-case: uppercase;
  --tab-letter: 0.06em;
  --tab-bg-active: var(--accent);
  --tab-color-active: #000;
  --tab-glyph-bg: transparent;
  --tab-glyph-border: var(--accent);
  --tab-glyph-color: var(--accent);
  --tab-glyph-radius: 0;
}
```

## Nova (nova)

The glass pill around the list (`--bar-*`, with `--bar-glass: 1` for the backdrop blur and the inner highlight), segmented pill tabs, the active one a lighter pill; the LB/RB symbols are outlined discs. The `transition` on the tabs is gone (the bar sets none).

```css
:root[data-skin="user:nova"] {
  --bar-bg: var(--panel);
  --bar-border: var(--glass-border);
  --bar-glass: 1;
  --bar-radius: 2rem;
  --bar-pad: 0.3rem;
  --tab-gap: 0.2rem;
  --tab-pad-x: 1.4rem;
  --tab-pad-y: 0.45rem;
  --tab-radius: 2rem;
  --tab-size: 1.1rem;
  --tab-weight: 500;
  --tab-underline: 0;
  --tab-bg-active: rgba(255, 255, 255, 0.16);
  --tab-glyph-bg: transparent;
  --tab-glyph-border: var(--glass-border);
  --tab-glyph-radius: 1rem;
}
```

## Stardust (stardust)

Light type, wide letters, a thin white line under the active tab, a little less air above the bar (`--bar-pad-top`). The old `#top { padding-top }`, `#tabs { gap }` (1 rem between the symbol and the list; the bar keeps 1.2 rem), `#tab-list`, `#tab-list li` and `li.active` rules can go.

```css
:root[data-skin="user:stardust"] {
  --bar-pad-top: 1.8rem;
  --tab-gap: 1.5rem;
  --tab-size: 1.2rem;
  --tab-weight: 300;
  --tab-weight-active: 400;
  --tab-letter: 0.06em;
  --tab-color-active: #fff;
  --tab-accent: #fff;
  --tab-underline: 0.14rem;
}
```

## Synthwave (synthwave)

Bold upper case type, the active tab white with a cyan line and a cyan glow; the LB/RB symbols are cyan outlined boxes. The old `#tab-list li` and `li.active` rules can go.

```css
:root[data-skin="user:synthwave"] {
  --tab-size: 1.25rem;
  --tab-weight: 700;
  --tab-letter: 0.04em;
  --tab-case: uppercase;
  --tab-color-active: #fff;
  --tab-accent: var(--cyan);
  --tab-glow: 0.9;
  --tab-glow-color: rgba(56, 232, 255, 0.8);
  --tab-glyph-bg: transparent;
  --tab-glyph-border: var(--cyan);
  --tab-glyph-color: var(--cyan);
  --tab-glyph-radius: 0.3rem;
}
```

## What does not carry over

- Decoration that lay over the bar from the page (scanlines, vignettes, grain in `body::after` or on `#top`, a background or border on `#top`, a `transition` on the tabs): the bar is drawn on its own layer on purpose.
- Fonts of the skin itself (`@font-face`) in the tabs: the tabs use the system fonts of the list in SKINS.en.md (the tabs follow the skin's `--font` as far as it names allowed fonts: the Segoe UI family, Bahnschrift and Agency FB work; "Rajdhani" is not an installed system font and drops out of the stack).
- Wide-gamut colours (`color(display-p3 …)`, HDR) in the bar: the program converts them to sRGB there.
- Per-tab rules (`li:nth-child`, `li[data-tab]`): all tabs look alike; a plugin cannot get a look of its own.
