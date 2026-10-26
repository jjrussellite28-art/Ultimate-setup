# CouchHUD Branding

The logo: a sofa seen from behind in front of a glowing TV showing CouchHUD's row of tiles. The backrest carries the four face buttons, so sofa and armrests also read as a controller. Tile, glow frame and lettering come from the same family as [CaseHUD](https://github.com/kewe87/CaseHUD); the colors are our own (blue → violet instead of cyan → lavender).

| File | Purpose |
|---|---|
| [`../../docs/logo.svg`](../../docs/logo.svg) | Logo and app icon from 48 px (source; `ui/assets/icon.svg` is a copy for the boot screen and favicon) |
| `icon-small.svg` | Simplified icon for 16–32 px (tray, taskbar, window title): borderless tile, thicker frame, no buttons, no glow |
| [`../../docs/banner.svg`](../../docs/banner.svg) | Banner at the top of the README (1520×400) |
| [`../../docs/social-preview.svg`](../../docs/social-preview.svg) | Social preview for GitHub (1280×640), shows three screenshots from `docs/` |
| `png/` | Pre-rendered icon sizes 16–512 px |

Everything is rendered with `node scripts/brand-render.mjs` (headless Edge, no further packages); the script also builds `src/CouchHud.Shell/couchhud.ico` (16, 20, 24, 32, 40, 48, 64, 128 and 256 px, up to 32 px from `icon-small.svg`).

Colors: `#3d8bff` → `#6a4cff` → `#c04dff` (frame and "HUD"), tile `#171c2e` → `#0b0d16`, sofa `#f4f6fb`. Font: Segoe UI ExtraBold (fallback Inter, Arial).

**Setting the social preview** (only possible on the web): GitHub › repository › *Settings* › *General* › *Social preview* › *Edit* › upload `docs/social-preview.png`.

Like the code, the logo is under the GPL-3.0. For forks please use your own logo so users can tell the projects apart.
