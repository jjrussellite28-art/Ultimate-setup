# CouchHUD Branding

Das Logo: ein Sofa von hinten vor einem leuchtenden Fernseher, auf dem die Kachelreihe von CouchHUD läuft. Die Rückenlehne trägt die vier Face-Buttons, Sofa und Armlehnen lesen sich so zugleich als Controller. Kachel, Leuchtrahmen und Schrift stammen aus derselben Familie wie [CaseHUD](https://github.com/kewe87/CaseHUD), die Farben sind eigene (Blau → Violett statt Cyan → Lavendel).

| Datei | Wofür |
|---|---|
| [`../../docs/logo.svg`](../../docs/logo.svg) | Logo und App-Icon ab 48 px (Quelle; `ui/assets/icon.svg` ist eine Kopie für Boot-Screen und Favicon) |
| `icon-small.svg` | Vereinfachtes Icon für 16–32 px (Tray, Taskleiste, Fenstertitel): randlose Kachel, dickerer Rahmen, keine Buttons, kein Leuchten |
| [`../../docs/banner.svg`](../../docs/banner.svg) | Banner oben im README (1520×400) |
| [`../../docs/social-preview.svg`](../../docs/social-preview.svg) | Social Preview für GitHub (1280×640), zeigt drei Screenshots aus `docs/` |
| `png/` | Fertig gerenderte Icon-Größen 16–512 px |

Alles wird mit `node scripts/brand-render.mjs` (headless Edge, keine weiteren Pakete) gerendert; das Skript baut auch `src/CouchHud.Shell/couchhud.ico` (16, 20, 24, 32, 40, 48, 64, 128 und 256 px, bis 32 px aus `icon-small.svg`).

Farben: `#3d8bff` → `#6a4cff` → `#c04dff` (Rahmen und „HUD“), Kachel `#171c2e` → `#0b0d16`, Sofa `#f4f6fb`. Schrift: Segoe UI ExtraBold (Fallback Inter, Arial).

**Social Preview setzen** (nur im Web möglich): GitHub › Repository › *Settings* › *General* › *Social preview* › *Edit* › `docs/social-preview.png` hochladen.

Das Logo steht wie der Code unter der GPL-3.0. Für Forks bitte ein eigenes Logo verwenden, damit Nutzer die Projekte auseinanderhalten können.
