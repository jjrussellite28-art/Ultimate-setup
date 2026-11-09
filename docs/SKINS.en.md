# Skins for Mega Hub

[Deutsch](SKINS.md) | **English**

A skin is a stylesheet plus optional button glyphs of its own. It changes colors, fonts, shapes, spacing and animations, however you like.

## Structure

```
%APPDATA%\MegaHub\skins\synthwave\
  skin.json
  skin.css
  (fonts, images …)
```

**skin.json**

```json
{
  "name": { "de": "Synthwave", "en": "Synthwave" },
  "description": { "de": "Neon, Sonnenuntergang, 1986", "en": "Neon, sunset, 1986" },
  "glyphs": { "A": "✕", "B": "○", "X": "□", "Y": "△", "LB": "L1", "RB": "R1", "≡": "MENU" },
  "soundSet": "nova",
  "sounds": { "move": "move.ogg", "select": "select.ogg", "back": "back.ogg", "notify": "notify.ogg" }
}
```

`name` and `description` are either plain text or an object with `de` and `en`; Mega Hub shows the one for the current language (the other one if it is missing). Names have at most 40 characters, descriptions 300; control characters, invisible format characters (such as text-direction overrides or zero-width characters) and `<` `>` are removed, so a name is always plain text. A skin without a name carries its folder name. `soundSet` is optional: one of the sound sets of the app (`default`, `deepsea`, `greenlight`, `neoncity`, `nova`) that plays while the skin is active; anything else is ignored.

`glyphs` is optional (at most 32 entries, each label at most 12 characters) and only styling. The key `≡` is the label of the menu button (for example `☰` or `MENU`): which symbols appear is decided by the controller (or *Settings › Controller › Button symbols*). An entry such as `✕`, `○`, `□`, `△`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `OPTIONS`, `CREATE`, `SHARE` or `PS` only applies with PlayStation symbols, any other only with Xbox symbols; otherwise Mega Hub shows the default symbol of that kind of controller. The button colors with PlayStation symbols hang on `:root[data-pad="playstation"]`. For own labels for both kinds use the optional lists `glyphsXbox` and `glyphsPlayStation` (same limits, text only); an entry there applies to exactly that kind of controller and wins over the entry in `glyphs`, which stays the fallback. Order: the list of the controller kind, then `glyphs` (filtered by kind), then the default symbol of the kind. A PlayStation pad that Steam Input or DS4Windows passes on as a virtual XInput pad counts as PlayStation as long as its native connection reports the presses first; otherwise Mega Hub shows the Xbox symbols.

`sounds` is optional. Possible events: `move`, `tab`, `select`, `back`, `bump`, `open`, `close`, `notify`, `toggle`, `boot` and `music`. `music` is a background track that loops when the user has chosen "Menu sounds and music"; without it, the skin's built-in music plays. Allowed are `.wav`, `.ogg` and `.mp3` from the skin folder. For missing events Mega Hub plays the built-in sound set.

**HDR**: when HDR mode is active, the root element carries `data-hdr="on"`. There you can set stronger colors, ideally in Display P3:

```css
@supports (color: color(display-p3 1 1 1)) {
  :root[data-skin="user:synthwave"][data-hdr] {
    --accent: color(display-p3 1 0.1 0.85);
  }
}
```

**skin.css**: all rules only apply while your skin is active:

```css
:root[data-skin="user:synthwave"] {
  --bg: #12001f;
  --fg: #ffe9ff;
  --muted: rgba(255, 233, 255, 0.6);
  --accent: #ff2bd6;
  --accent-glow: rgba(255, 43, 214, 0.55);
  --panel: rgba(40, 0, 60, 0.7);
  --font: "Orbitron", system-ui, sans-serif;
  --tile-radius: 0.2rem;
  --shelf-tile: 13rem;
  --focus-scale: 1.1;
}

[data-skin="user:synthwave"] .tile.focused {
  box-shadow: 0 0 0 0.15rem #fff, 0 0 2.5rem var(--accent-glow);
}
```

Put your own fonts and images in the skin folder and reference them relatively (`url("orbitron.woff2")`).

**Local files only:** Mega Hub replaces internet addresses (`http://`, `https://`, `//host/…`) in `skin.css` with `about:blank` before it serves the stylesheet. A skin should not be able to contact a foreign server for every image and so tell it when someone is sitting on the couch. Images, fonts and sounds therefore belong in the skin folder (or in the CSS as a `data:` address). The skin folder may only point to local drives, never to network drives or shares (`\\server\…`). When installing from a `.zip`, Mega Hub points out that a skin can load content.

## Important variables

| Variable | Effect |
|---|---|
| `--bg`, `--fg`, `--muted` | Background, text, muted text |
| `--accent`, `--accent-glow`, `--accent-contrast` | Accent color, its glow, text color on the accent |
| `--panel` | Background of cards, lists, menus |
| `--font` | Font family |
| `--tile-radius`, `--shelf-tile`, `--focus-scale` | Corners, tile size on Home, zoom of the focus |

For everything else you can override the selectors from `ui/styles.css`. The skins in the catalog (Deep Sea, Greenlight, Neon City, Nova, Stardust) show how far that goes: Deep Sea, for example, turns the whole Home layout around. Only the Mega Hub skin is part of the app (`ui/skins/`); all others are skins like yours, installed into the skins folder, and their rules begin with `:root[data-skin="user:<folder>"]`.

## The tab bar

Since 0.7 the tab bar is no longer a part of the page that a skin reaches with selectors. It is built so that a skin can style it but cannot take it apart:

- **Closed shadow DOM.** The bar is the `<header id="top">`, the host of a closed shadow root. Selectors of the page (`#tab-list li`, `#tabs`, `.tab-more`, `.glyph.shoulder`, `li::first-letter` …) do not reach into it: they still load, but do nothing. A skin cannot hide, shrink, move, recolour or touch a tab with `::first-letter`/`::first-line`.
- **Top layer.** The bar stands as a popover (`popover="manual"`, `showPopover()`) in the browser's top layer. No layer of the page, not a `body::after` with `z-index: 9999` and not any `position: fixed`, can paint over it. Only parts of the program that cover the whole screen or never need room stand over it: the boot screen, the screensaver, the launch screen, the "website in full screen" mark (a shadow root of its own) and the mouse pointer (also one); they are popovers themselves, shown after the bar, and only while the program has them open. **The Guide menu (`#quick`), dialogs (`#overlay`), toasts and notifications stand under the bar**: the Guide menu starts below it, and the bar dims while one is open. So no rule of a skin for the size, children, `::before`/`::after`, transforms or 3D of these parts ever reaches a tab; that closes the whole class instead of pinning properties one by one. In addition the program holds `contain: paint`, `overflow: clip`, `perspective: none`, `transform-style: flat` and `rotate`/`scale`/`translate: none` inline on the Guide menu and the dialog, because a 3D context can be painted out of order by the browser.
- **No inheritance.** The tab area starts with `all: initial`: font, colour, `text-shadow`, outline (`-webkit-text-stroke`), size, `font-size-adjust`, spacing and `text-indent` of `body` and `html` do not reach the letters.
- **The host is pinned from inside.** `#top` itself starts with `all: initial !important` (so properties nobody thought of, such as anchor positioning, `aspect-ratio` or container queries, are at their initial value too) and then gets fixed values: display, visibility, opacity, position, size, `translate`, and `::before`, `::after` and `::backdrop` off. A rule in the shadow root with `!important` beats a rule of the page, important or not. The burn-in shift (at most 4 and 3 pixels) follows the screensaver. On `html` and `body` the program only holds `zoom`, `content-visibility`, `display` and the font size of `html` inline; opacity, filter, mask, clip and transform of `body`/`html` do not reach an element in the top layer. The bar measures in pixels derived from the program's rem (a 54th of the screen height), never in the document's: another font size on `html` does not change it.
- **The status area** (`#status`, bell, downloads, clock) stays in the page and is slotted into the bar. It can still be styled with selectors but is limited to a box of its own (at most 40 % of the width, a fixed height of 4 rem, `contain: paint`): whatever a skin tries there with `position: fixed`, `z-index`, huge width or height or big shadows stays in that box; the tabs keep at least 60 %, and the status area never lies over them.
- **A backing of its own.** Behind the bar there is always the theme: `--bg` at 80 % opacity, fading out downwards (a soft, dark stripe over the background picture and the page). A skin tints it only through `--bar-bg`; it cannot take the 80 % away. The readability of the text colours is checked against that backing, over black and over white behind it: whatever the page or a layer of the skin paints there, the letters hold. For the catalog skins this is the closest solution: their `--bg` is dark and the stripe is lighter than the picture under it; a light skin gets a light stripe.
- **No cascade layers, no `@import`.** Where the program serves a skin's CSS it removes every `@import` (with `layer()` and `supports()` too) and every `@layer` statement; the content of an `@layer` block stays, but as ordinary rules without a layer (dropping it would silently lose an honest skin's rules; without a layer the same plain cascade applies as for the rest of the skin). Reading follows the CSS syntax (an unquoted `url(` runs to its closing bracket, strings end at a newline, a carriage return and a form feed). As a backstop the whole text afterwards (strings, comments and `url()` too, escapes resolved) is searched for `@layer` and `@import`: if one is still in it, the program does not serve the stylesheet at all and writes a warning to the log (an honest skin has no reason to; a false alarm is acceptable). The stylesheet is always read as UTF-8: the program sends `charset=utf-8` in the header (which beats an `@charset` in the text), an `@charset` is removed, and a skin is not served at all (with a warning in the log) if an `@charset` names anything but `utf-8`, if the file is not valid UTF-8 or starts with the byte order mark of UTF-16, or if a control character other than tab, line feed, form feed and carriage return is in it (in ISO-2022-JP, for example, ESC `(B` stays invisible and could hide `@layer` or a web address from the check). The program relies on cascade layers nowhere to protect the bar. The structural solution for the tab bar (closed shadow DOM, top layer, skin variables) ships in 0.7; it is no longer just planned. The mouse pointer and the full screen mark are shadow hosts themselves (`all: initial`, `::backdrop` off, a fixed box; the pointer at most 8 rem) and take only `--pointer`, `--pointer-size` and the hotspot from the skin.
- **What is left:** a minimal check (`barSane` in core/tab-bar.js) looks whether the bar is in the top layer, has a size and every tab has room. If not, the program shows it again once and falls back to the default skin if need be (the skin is then not saved). Where a WebView2 without popovers runs (before version 114; the runtime updates itself and the setup brings a current one), only the shadow DOM with the highest `z-index` is left.

### Styling: custom properties

A skin styles the bar only with these variables (on `:root[data-skin="…"]`; a numeric value is a length in `rem`, `px` or `em`, no `calc()`; `rem` is the program's rem, a 54th of the screen height, even if a skin changes the root font size). The program reads them, cleans and clamps every value in JavaScript and only then sets a clean value inside the shadow root. A value that is missing, invalid, too large or too small becomes the default or the nearest allowed value.

| Variable | Effect | Default | Limits |
|---|---|---|---|
| `--tab-color` | text of a tab | `--muted` | readable, see below |
| `--tab-color-active` | text of the active tab and the LB/RB symbols | `--fg` | readable |
| `--tab-accent` | the line under the active tab | `--accent` | readable |
| `--tab-bg`, `--tab-bg-active` | fill of a tab, of the active one | none | any colour |
| `--tab-glow`, `--tab-glow-color` | glow (one `text-shadow`) on the active tab: strength, colour | 0 (off), `--accent-glow` | strength 0 to 1.2, blur at most 1.2 rem |
| `--tab-font` | font family | `--font` | the list below only |
| `--tab-size` | font size | 1.35 rem | 0.9 to 1.9 rem |
| `--tab-weight`, `--tab-weight-active` | weight, of the active tab | 600 (active: the same) | 300 to 800, in hundreds |
| `--tab-letter` | letter spacing | 0 | −0.02 to 0.12 em |
| `--tab-case` | `uppercase` or `none` | `none` | only these two |
| `--tab-gap` | gap between tabs (the bar closes it when room is short) | 1.8 rem | 0.2 to 2.4 rem |
| `--tab-pad-x`, `--tab-pad-y` | padding of a tab, sideways and top/bottom | 0 and 0.3 rem | 0 to 2 and 0.1 to 0.8 rem |
| `--tab-radius` | rounding (pill) | 0 | 0 to 2 rem |
| `--tab-cut` | cut corner at the top right | 0 | 0 to 0.9 rem |
| `--tab-underline` | thickness of the line under the active tab | 0.18 rem | 0 to 0.3 rem |
| `--tab-min-width` | smallest width of a tab that has to shrink | 6.5 rem | 4 to 10 rem |
| `--bar-bg`, `--bar-border` | fill and border of the pill around the list | none | any colour |
| `--bar-radius`, `--bar-pad` | rounding and padding of that pill | 0 and 0 | 0 to 2.5 and 0 to 0.6 rem |
| `--bar-glass` | `1`: frosted glass (the program's backdrop blur and shadow) behind the pill | `0` | only `1` or `0` |
| `--bar-pad-top` | air above the bar | 2 rem | 0 to 3 rem |
| `--tab-glyph-bg`, `--tab-glyph-border` | fill and border of the LB/RB symbols | light grey, no border | any colour |
| `--tab-glyph-radius`, `--tab-glyph-size` | rounding and size of those symbols | 0.4 and 0.8 rem | 0 to 1 and 0.55 to 1 rem |

**Colours** may be written in any syntax (`#rgb`, `rgb()`, `hsl()`, `oklch()`, `color()`, `color-mix()`; the program converts them to sRGB, so a wide colour space such as Display-P3 does not apply in the bar). Every text colour is checked against the ground it stands on: the tab's fill over the pill's fill over `--bg`. If it is see-through (alpha under 0.3), cannot be read (contrast under 1.5 to the ground) or is not a colour, the program uses the theme's text colour (`--fg`) and, if that does not stand out either, black or white. Any other readable colour stays as it is.

**Fonts** come from a list only: the generic families (`system-ui`, `sans-serif`, `serif`, `monospace`, `ui-sans-serif`, `ui-serif`, `ui-monospace`, `ui-rounded`) and these names installed with Windows or Office: Segoe UI (also Variable Display/Text/Small, Light, Semilight, Semibold, Black, Emoji, Symbol), Bahnschrift, Arial (also Narrow, Black), Calibri, Cambria, Candara, Consolas, Constantia, Corbel, Courier New, Franklin Gothic Medium, Georgia, Impact, Lucida Console, Lucida Sans Unicode, Microsoft Sans Serif, Palatino Linotype, Segoe Print, Segoe Script, Sylfaen, Tahoma, Times New Roman, Trebuchet MS, Verdana, Agency FB, Century Gothic, Gill Sans MT, Rockwell, Bookman Old Style. Anything else in `--tab-font` or `--font` (a skin's own font, a quoted `"sans-serif"`) stays out for the tabs; the rest of the interface keeps using `--font` as it likes. If a skin declares an `@font-face` itself under the name of a system font, the program takes that name out of the tabs' list (`document.fonts` tells): an `@font-face` with `size-adjust`, `ascent-override` or `descent-override` can never get at the letters. The font is right when the bar measures next (on a skin or window change and every 30 seconds).

Other rules of the bar that cannot be changed: word spacing, position and order of the tabs (the plugins' too), fitting to the room (core/nav.js: if all fit, all stay; otherwise the gaps close first, then the wide tabs get narrower, never below the minimum width; if that is not enough the bar scrolls with arrows at its edges and the active tab stays whole), a long name that runs through as a marquee, the setup assistant that hides the tabs (not the status area), and the burn-in shift. The tabs keep their full name for screen readers (`aria-label`); controller, LB/RB and mouse go through the bar as before.

### Moving a skin from 0.6

Old selectors for the bar still load but do nothing (`scripts/skin-check.mjs` reports them as a note, not as an error). This is how an old rule becomes a variable:

| Old (0.6) | New (0.7) |
|---|---|
| `#tab-list li { font-size: 1.2rem; font-weight: 300; letter-spacing: 0.04em }` | `--tab-size: 1.2rem; --tab-weight: 300; --tab-letter: 0.04em` |
| `#tab-list li { color: … }`, `li.active { color: … }` | `--tab-color`, `--tab-color-active` |
| `li.active { border-color: … }`, `border-bottom: 0.14rem` | `--tab-accent`, `--tab-underline: 0.14rem` |
| `li.active { text-shadow: 0 0 1rem … }` | `--tab-glow: 1; --tab-glow-color: …` |
| `#tab-list { gap: … }`, `li { padding: … }` | `--tab-gap`, `--tab-pad-x`, `--tab-pad-y` |
| `li { border-radius: 2rem; background: … }`, `li.active { background: … }` | `--tab-radius`, `--tab-bg`, `--tab-bg-active` |
| `li { clip-path: polygon(… 0.7rem …) }` (cut corner) | `--tab-cut: 0.7rem` |
| `body { text-transform: uppercase; letter-spacing: … }` (inherited into the tabs) | `--tab-case: uppercase; --tab-letter: …` |
| `#tab-list { background: …; border; backdrop-filter; border-radius; padding }` (glass pill) | `--bar-bg`, `--bar-border`, `--bar-glass: 1`, `--bar-radius`, `--bar-pad` |
| `#top { padding-top: … }` | `--bar-pad-top` |
| `.glyph.shoulder { … }` | `--tab-glyph-bg`, `--tab-glyph-border`, `--tab-glyph-radius`, `--tab-glyph-size` |
| `--tab-min-width` | as before |

The skins in the catalog (Deep Sea, Green Light, Neon City, Nova, Stardust, Synthwave) show how far this goes: a frosted glass pill (Nova), cut corners (Neon City), glow (Deep Sea, Synthwave). The values for these six skins are in [SKINS-0.7-catalog.md](SKINS-0.7-catalog.md).

### Residual risk

- With this structure the bypasses that the 0.6.4 runtime check could not catch are **no longer possible**: slits in a layer between the checked places (layers of the page never lie over the bar), `@font-face` with `size-adjust` and `ascent-`/`descent-override` (names a skin declares itself do not apply to the tabs), `::first-letter` and `::first-line` on the tabs, many `text-shadow` (the bar sets at most one, with a capped blur) and layers of `body`/`html` over the bar.
- **The ground behind the bar** is the program's: `--bg` at 80 % opacity, over the page that skins style freely. The readability check computes the text colours against that backing over black and over white; what still shows through (up to 20 %) can only lower the contrast a little. A skin whose `--bg` does not suit its own `--fg` gets the replacement colour.
- **The rest of the page** (views, the hint bar at the bottom, the status area) can still be styled, also for the worse: a skin can cover a view or hide chips in the status area. The tabs stay, and `skin-check.mjs` reports rules that hide, fade or mask `html` and `body`.
- An `@font-face` that only applies through a media query after a measurement is caught at the next one (size and skin changes, every 30 seconds); until then another font may stand for a moment.
- The bar is only as strong as the browser under it: a WebView2 before version 114 has no popovers (see above).
- The review of the catalog remains: we review and sign the skins in the catalog ourselves.

### Lint

`scripts/skin-check.mjs` (in the Mega Hub repository; `scripts/catalog-entry.ps1` calls it when it packs a skin folder) is a **lint for the catalog review**. It reports as errors what still reaches the page: an `@font-face` with `size-adjust` under 80 % or with `ascent`/`descent`/`line-gap` overrides, an `@font-face` under the name of a system font, and rules on `html`/`body` that hide, fade or mask the page. As notes (not failing) it reports rules for the old bar that do nothing now, and values of the `--tab-*`/`--bar-*` variables that are not a plain number. Give the catalog folder as an argument (a git worktree has no `../couchhud-catalog-src`), or in the environment variable `MEGAHUB_CATALOG_SKINS`.

## Square tiles

A skin says which picture a tile shows with the CSS variable `--tile-art`, set **where it sets the tile's shape** (`aspect-ratio`): on the tile itself (`.shelf .cover`, `.grid .cover`, `.shelf .hero-art`), on its row, or on `:root[data-skin="…"]` for all game tiles. Values:

| Value | Picture first | Stand-in when it is missing |
|---|---|---|
| `square` | square picture (SteamGridDB: square grids, then icons; Steam itself has none) | cover |
| `cover` (default for game and app tiles) | cover | square picture |
| `hero` (default for the "Continue playing" card) | hero picture | cover, then square picture |

Any other or missing value means the tile's default; the default skin does not change. The app tiles (16:9) and the "Continue playing" card set their own value, so a `square` on `:root` does not reach them; a skin that makes the card square sets `--tile-art: square` on `.shelf .hero-art`.

When a picture does not suit the tile's shape (a cover standing in on a square tile, a store header on a cover tile), Mega Hub shows it **whole and centred over a blurred copy** of itself (class `art-fit`, plus `art-wide` when the picture is wider than the tile), never stretched and never with its title cut off. A square or hero picture on a tile that asks for it fills the tile. Mega Hub reads the tile's shape from its `aspect-ratio`. Every tile carries `data-tile-art` with the picture it shows, and the class `art-square` for `square`.

Switching skins switches the pictures **at once**, both ways, without a restart and without a new scan: as soon as the new skin's stylesheet has loaded, every tile asks again. Only pictures the library already knows are loaded; missing square pictures come only from the normal artwork lookup (with a SteamGridDB key, and only while the privacy switches allow it). Users choose the square picture under *Change artwork* in the "Square" tab.

```css
/* Square tiles in the Home row, the library keeps its covers. */
[data-skin="user:synthwave"] .shelf .cover {
  aspect-ratio: 1;
  --tile-art: square;
}
```

## Mouse pointer

When the user moves a mouse, Mega Hub draws its own pointer. It comes from the skin: every skin may set it, and a skin without a pointer of its own automatically shows Mega Hub's default arrow. With the setting "Real mouse pointer" (*Settings › Look*) the user sees the normal Windows pointer instead, and your pointer does not apply.

| Variable | Effect |
|---|---|
| `--pointer` | Image of the pointer, ideally a small SVG as a `data:` address: `url("data:image/svg+xml,…")` (write `#` as `%23`) |
| `--pointer-size` | Edge length of the square image, default `4.6rem` |
| `--pointer-hx`, `--pointer-hy` | Hotspot as a fraction of the size; `0 0` is the top left corner, `0.5 0.5` the center. The mouse position sits exactly there |

```css
:root[data-skin="user:synthwave"] {
  --pointer: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><path d='M5 3 L5 35 L13 27.5 L19 41 L25.5 38.2 L19.5 25 L31 25 Z' fill='%23ff2bd6' stroke='%2312141c' stroke-width='3.2' stroke-linejoin='round'/></svg>");
  --pointer-hx: 0.104;  /* tip of the arrow at (5, 3) of 48 */
  --pointer-hy: 0.0625;
}
```

Tips: a pointer has to stand out on the TV from the armchair. A dark outline (here `#12141c`) keeps it visible on a light background, a light fill on a dark one. The pointer rests while the mouse is still. Since 0.7 the pointer is a closed shadow root in the top layer: no selector reaches `#cursor` or `#cursor .cp` any more, and the skin steers it only through `--pointer`, `--pointer-size` (1 to 8 rem) and `--pointer-hx`/`--pointer-hy` (0 to 1); the squish on click and the walking animation are the program's (off with "Reduce animations"). This is only the pointer inside Mega Hub. In other windows it stays the Windows pointer.

## Trying it out

Start Mega Hub with `--dev` and pick your skin under *Settings › Look › Skin*. You see changes to the CSS after selecting it again.

It also works without Windows: `node scripts/preview-server.js` starts the interface with sample data in the browser. There, serve your skin folders with `CATALOG_SKINS=<folder with your skin folders> node scripts/preview-server.js` and look at a skin with `node scripts/ui-shot.mjs --catalog <folder> skin:user:<folder name> shot:home`. The stylesheet is cleaned of web addresses there just as the app does it.

## Sharing

Pack the folder as a `.zip` (with or without the parent folder) and publish it on GitHub with the topic `megahub-skin`. Others install it in Mega Hub under *Settings › Look › Install new skins*.

For the catalog: give the skin a name of its own, not someone else's brand (no console, manufacturer or game name), and use no third-party logos. A style may remind you of something, but must not be named after it.
