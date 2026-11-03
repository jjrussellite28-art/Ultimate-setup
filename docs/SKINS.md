# Skins für CouchHUD

**Deutsch** | [English](SKINS.en.md)

Ein Skin ist ein Stylesheet plus optional eigene Button-Symbole. Er ändert Farben, Schriften, Formen, Abstände, Animationen, wie du willst.

## Aufbau

```
%APPDATA%\CouchHud\skins\synthwave\
  skin.json
  skin.css
  (Schriften, Bilder …)
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

`name` und `description` sind entweder einfacher Text oder ein Objekt mit `de` und `en`; CouchHUD zeigt den Text der aktuellen Sprache (fehlt er, den anderen). Namen haben höchstens 40 Zeichen, Beschreibungen 300; Steuerzeichen, unsichtbare Formatzeichen (etwa Umkehr der Schreibrichtung oder Zeichen ohne Breite) und `<` `>` werden entfernt, ein Name ist also immer einfacher Text. Ein Skin ohne Namen trägt seinen Ordnernamen. `soundSet` ist optional: eines der Klangsets der App (`default`, `deepsea`, `greenlight`, `neoncity`, `nova`), das spielt, solange der Skin aktiv ist; alles andere wird ignoriert.

`glyphs` ist optional (höchstens 32 Einträge, jede Beschriftung höchstens 12 Zeichen) und nur Gestaltung. Der Schlüssel `≡` ist die Beschriftung der Menütaste (zum Beispiel `☰` oder `MENU`): Welche Symbole erscheinen, bestimmt der Controller (bzw. *Einstellungen › Controller › Tastensymbole*). Ein Eintrag wie `✕`, `○`, `□`, `△`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `OPTIONS`, `CREATE`, `SHARE` oder `PS` gilt nur mit PlayStation-Symbolen, jeder andere nur mit Xbox-Symbolen; sonst zeigt CouchHUD das Standardsymbol der Controller-Art. Die Farben der Tasten bei PlayStation-Symbolen hängen an `:root[data-pad="playstation"]`. Wer für beide Arten eigene Beschriftungen will, nimmt die optionalen Listen `glyphsXbox` und `glyphsPlayStation` (gleiche Grenzen, nur Text); ein Eintrag dort gilt genau für diese Art von Controller und geht dem Eintrag in `glyphs` vor, `glyphs` bleibt die Rückfallebene. Rangfolge: Liste der Controller-Art, dann `glyphs` (nach Art gefiltert), dann das Standardsymbol der Art. Ein PlayStation-Controller, den Steam Input oder DS4Windows als virtuellen XInput-Controller weiterreicht, zählt als PlayStation, solange sein nativer Anschluss die Tasten zuerst meldet; sonst zeigt CouchHUD die Xbox-Symbole.

`sounds` ist optional. Mögliche Ereignisse: `move`, `tab`, `select`, `back`, `bump`, `open`, `close`, `notify`, `toggle`, `boot` und `music`. `music` ist ein Hintergrund-Track, der in Schleife läuft, wenn der Nutzer „UI-Sounds und Musik“ gewählt hat; ohne ihn spielt die eingebaute Musik des Skins. Erlaubt sind `.wav`, `.ogg` und `.mp3` aus dem Skin-Ordner. Für fehlende Ereignisse spielt CouchHUD das eingebaute Klangset.

**HDR**: Ist der HDR-Modus aktiv, steht `data-hdr="on"` am Root-Element. Dort kannst du kräftigere Farben setzen, am besten in Display-P3:

```css
@supports (color: color(display-p3 1 1 1)) {
  :root[data-skin="user:synthwave"][data-hdr] {
    --accent: color(display-p3 1 0.1 0.85);
  }
}
```

**skin.css**: Alle Regeln gelten nur, wenn dein Skin aktiv ist:

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

Eigene Schriften und Bilder legst du in den Skin-Ordner und bindest sie relativ ein (`url("orbitron.woff2")`).

**Nur lokale Dateien:** CouchHUD ersetzt Internet-Adressen (`http://`, `https://`, `//host/…`) in `skin.css` durch `about:blank`, bevor es das Stylesheet ausliefert. Ein Skin soll nicht bei jedem Bild bei einem fremden Server anfragen können und ihm so verraten, wann jemand auf der Couch sitzt. Bilder, Schriften und Klänge gehören deshalb in den Skin-Ordner (oder als `data:`-Adresse ins CSS). Der Skin-Ordner darf nur auf lokale Laufwerke zeigen, nie auf Netzlaufwerke oder Freigaben (`\\server\…`). Beim Installieren per `.zip` weist CouchHUD darauf hin, dass ein Skin Inhalte laden kann.

## Wichtige Variablen

| Variable | Wirkung |
|---|---|
| `--bg`, `--fg`, `--muted` | Hintergrund, Text, gedämpfter Text |
| `--accent`, `--accent-glow`, `--accent-contrast` | Akzentfarbe, ihr Leuchten, Textfarbe auf dem Akzent |
| `--panel` | Hintergrund von Karten, Listen, Menüs |
| `--font` | Schriftfamilie |
| `--tile-radius`, `--shelf-tile`, `--focus-scale` | Ecken, Kachelgröße auf Home, Zoom des Fokus |

Für alles andere kannst du die Selektoren aus `ui/styles.css` überschreiben. Die Skins im Katalog (Tiefsee, Grünlicht, Neonstadt, Nova, Sternenstaub) zeigen, wie weit das geht: Tiefsee dreht z.B. das ganze Home-Layout um. Nur der CouchHUD-Skin gehört zum Programm (`ui/skins/`); alle anderen sind Skins wie deiner, im Skin-Ordner installiert, und ihre Regeln beginnen mit `:root[data-skin="user:<Ordner>"]`.

## Die Tab-Leiste

Seit 0.7 ist die Tab-Leiste kein Teil der Seite mehr, den ein Skin mit Selektoren erreicht. Sie ist gebaut, dass ein Skin sie gestalten, aber nicht auseinandernehmen kann:

- **Geschlossenes Shadow DOM.** Die Leiste ist der `<header id="top">`, Host eines geschlossenen Shadow-Roots. Selektoren der Seite (`#tab-list li`, `#tabs`, `.tab-more`, `.glyph.shoulder`, `li::first-letter` …) reichen nicht hinein: Sie laden weiter, tun aber nichts mehr. Ein Skin kann also keinen Tab ausblenden, verkleinern, verschieben, umfärben oder mit `::first-letter`/`::first-line` anfassen.
- **Oberste Ebene.** Die Leiste steht als Popover (`popover="manual"`, `showPopover()`) in der obersten Ebene des Browsers. Keine Ebene der Seite, auch kein `body::after` mit `z-index: 9999` und kein `position: fixed`, kann darüber malen. Über der Leiste stehen nur Teile des Programms, die den ganzen Bildschirm bedecken oder nie Platz brauchen: Startbild, Ruhebildschirm, Startübergang, der Hinweis „Website im Vollbild“ (ein eigener Shadow-Root) und der Mauszeiger (ebenfalls); sie sind selbst Popover, kommen nach der Leiste und nur, solange das Programm sie offen hat. **Das Guide-Menü (`#quick`), Dialoge (`#overlay`), Hinweise und Benachrichtigungen stehen unter der Leiste**: Das Guide-Menü beginnt unter ihr, die Leiste wird gedimmt, solange eines offen ist. Darum erreicht keine Regel eines Skins für Größe, Kinder, `::before`/`::after`, Transformationen oder 3D dieser Teile je einen Tab; das schließt die ganze Klasse, statt Eigenschaften einzeln festzunageln. Zusätzlich hält das Programm am Guide-Menü und am Dialog inline `contain: paint`, `overflow: clip`, `perspective: none`, `transform-style: flat`, `rotate`/`scale`/`translate: none`, weil ein 3D-Kontext vom Browser außer der Reihe gezeichnet werden kann.
- **Kein Erbe.** Der Tab-Bereich beginnt mit `all: initial`: Schrift, Farbe, `text-shadow`, Kontur (`-webkit-text-stroke`), Größe, `font-size-adjust`, Abstände und `text-indent` von `body` und `html` kommen an den Buchstaben nicht an.
- **Der Host ist von innen festgenagelt.** `#top` selbst beginnt mit `all: initial !important` (auch Eigenschaften, an die niemand gedacht hat, etwa Anker-Positionierung, `aspect-ratio` oder Container-Abfragen, sind damit auf dem Anfangswert) und bekommt dann feste Werte: Anzeige, Sichtbarkeit, Deckkraft, Position, Größe, `translate`, `::before`, `::after` und `::backdrop` aus. Eine Regel im Shadow-Root mit `!important` schlägt auch eine `!important`-Regel der Seite. Die Verschiebung gegen Einbrennen (höchstens 4 und 3 Pixel) folgt dem Bildschirmschoner. Bei `html` und `body` hält das Programm nur noch `zoom`, `content-visibility`, `display` und die Schriftgröße von `html` inline fest; Deckkraft, Filter, Maske, Ausschnitt und Transformation von `body`/`html` erreichen ein Element der obersten Ebene nicht. Die Leiste misst in Pixeln, abgeleitet vom Rem des Programms (ein 54stel der Bildschirmhöhe), nie in dem des Dokuments: Eine andere Schriftgröße an `html` ändert sie nicht.
- **Der Statusbereich** (`#status`, Glocke, Downloads, Uhr) bleibt in der Seite und wird in die Leiste hineingesteckt. Er ist weiter mit Selektoren gestaltbar, aber auf einen eigenen Kasten begrenzt (höchstens 40 % der Breite, feste Höhe von 4 rem, `contain: paint`): Was ein Skin dort mit `position: fixed`, `z-index`, riesiger Breite oder Höhe oder großen Schatten versucht, bleibt in diesem Kasten; die Tabs behalten mindestens 60 %, und der Statusbereich liegt nie über ihnen.
- **Ein eigener Untergrund.** Hinter der Leiste liegt immer das Theme: `--bg` mit 80 % Deckung, nach unten ausblendend (ein weicher, dunkler Streifen über Hintergrundbild und Seite). Ein Skin färbt ihn nur über `--bar-bg` an; die 80 % kann er nicht wegnehmen. Die Lesbarkeit der Schriftfarben wird gegen diesen Untergrund geprüft, und zwar über Schwarz und über Weiß dahinter: Egal was die Seite oder eine Ebene des Skins dort malt, die Buchstaben halten. Für die Katalog-Skins ist das die nächste Lösung: Ihr `--bg` ist dunkel, und der Streifen ist leichter als das Bild darunter; ein heller Skin bekommt einen hellen Streifen.
- **Keine Kaskadenebenen, kein `@import`.** Wo das Programm CSS eines Skins ausliefert, entfernt es jeden `@import` (auch mit `layer()` und `supports()`) und jede `@layer`-Anweisung; der Inhalt eines `@layer`-Blocks bleibt, aber als gewöhnliche Regeln ohne Ebene (ihn zu streichen würde Regeln eines ehrlichen Skins stillschweigend verlieren; ohne Ebene gilt dieselbe einfache Kaskade wie für den Rest des Skins). Das Lesen folgt der CSS-Syntax (ein `url(` ohne Anführungszeichen läuft bis zur schließenden Klammer, Zeichenketten enden an Zeilenumbruch, Wagenrücklauf und Seitenvorschub). Als Rückhalt wird danach der ganze Text (auch Zeichenketten, Kommentare und `url()`, Escapes aufgelöst) nach `@layer` und `@import` durchsucht: Steht eines noch darin, liefert das Programm das ganze Stylesheet nicht aus und schreibt eine Warnung ins Protokoll (ein ehrlicher Skin hat dafür keinen Grund; ein Fehlalarm ist hinnehmbar). Das Stylesheet wird immer als UTF-8 gelesen: Das Programm sendet `charset=utf-8` im Header (der schlägt ein `@charset` im Text), ein `@charset` wird entfernt, und ein Skin wird als Ganzes nicht ausgeliefert (mit Warnung im Protokoll), wenn ein `@charset` etwas anderes als `utf-8` nennt, wenn die Datei kein gültiges UTF-8 ist oder mit der Markierung (BOM) von UTF-16 beginnt oder wenn ein Steuerzeichen außer Tabulator, Zeilenumbruch, Seitenvorschub und Wagenrücklauf darin steht (in ISO-2022-JP etwa bleibt ESC `(B` unsichtbar und könnte `@layer` oder eine Internet-Adresse vor der Prüfung verstecken). Das Programm verlässt sich nirgends auf Kaskadenebenen, um die Leiste zu schützen. Die strukturelle Lösung für die Tab-Leiste (geschlossenes Shadow DOM, oberste Ebene, Skin-Variablen) ist Teil von 0.7, nicht mehr nur geplant. Der Mauszeiger und der Vollbild-Hinweis sind selbst Shadow-Hosts (`all: initial`, `::backdrop` aus, feste Box; der Zeiger höchstens 8 rem) und nehmen vom Skin nur `--pointer`, `--pointer-size` und den Hotspot.
- **Was bleibt:** Eine Mindestprüfung (`barSane` in core/tab-bar.js) schaut, ob die Leiste in der obersten Ebene steht, eine Größe hat und jeder Tab Platz. Sonst zeigt das Programm sie noch einmal und wechselt notfalls zum Standard-Skin (der Skin wird dann nicht gespeichert). Wo ein WebView2 ohne Popover läuft (vor Version 114; die Runtime aktualisiert sich selbst und das Setup bringt eine aktuelle mit), bleibt nur das Shadow DOM mit dem höchsten `z-index`.

### Die Gestaltung: Custom Properties

Ein Skin gestaltet die Leiste nur mit diesen Variablen (auf `:root[data-skin="…"]`, ein Zahlenwert ist eine Länge mit `rem`, `px` oder `em`, ohne `calc()`; `rem` ist das Rem des Programms, ein 54stel der Bildschirmhöhe, auch wenn ein Skin die Schriftgröße von `html` ändert). Das Programm liest sie, bereinigt und begrenzt jeden Wert in JavaScript und setzt erst dann einen sauberen Wert im Shadow-Root. Fehlt ein Wert, ist er ungültig oder zu groß oder zu klein, gilt der Standard oder der nächste erlaubte Wert.

| Variable | Wirkung | Standard | Grenzen |
|---|---|---|---|
| `--tab-color` | Schrift eines Tabs | `--muted` | lesbar, siehe unten |
| `--tab-color-active` | Schrift des aktiven Tabs und der LB/RB-Symbole | `--fg` | lesbar |
| `--tab-accent` | Strich unter dem aktiven Tab | `--accent` | lesbar |
| `--tab-bg`, `--tab-bg-active` | Füllung eines Tabs, des aktiven | keine | jede Farbe |
| `--tab-glow`, `--tab-glow-color` | Leuchten (ein `text-shadow`) am aktiven Tab: Stärke, Farbe | 0 (aus), `--accent-glow` | Stärke 0 bis 1,2, Unschärfe höchstens 1,2 rem |
| `--tab-font` | Schriftfamilie | `--font` | nur die Liste unten |
| `--tab-size` | Schriftgröße | 1,35 rem | 0,9 bis 1,9 rem |
| `--tab-weight`, `--tab-weight-active` | Schriftstärke, des aktiven Tabs | 600 (aktiv: wie sonst) | 300 bis 800, in Hunderterschritten |
| `--tab-letter` | Buchstabenabstand | 0 | −0,02 bis 0,12 em |
| `--tab-case` | `uppercase` (Großbuchstaben) oder `none` | `none` | nur diese beiden |
| `--tab-gap` | Abstand zwischen Tabs (die Leiste rückt sie bei Platzmangel enger) | 1,8 rem | 0,2 bis 2,4 rem |
| `--tab-pad-x`, `--tab-pad-y` | Polster eines Tabs, seitlich und oben/unten | 0 und 0,3 rem | 0 bis 2 und 0,1 bis 0,8 rem |
| `--tab-radius` | Rundung (Pille) | 0 | 0 bis 2 rem |
| `--tab-cut` | abgeschnittene Ecke oben rechts | 0 | 0 bis 0,9 rem |
| `--tab-underline` | Dicke des Strichs unter dem aktiven Tab | 0,18 rem | 0 bis 0,3 rem |
| `--tab-min-width` | kleinste Breite eines Tabs, der schrumpfen muss | 6,5 rem | 4 bis 10 rem |
| `--bar-bg`, `--bar-border` | Füllung und Rand der Pille um die Liste | keine | jede Farbe |
| `--bar-radius`, `--bar-pad` | Rundung und Polster dieser Pille | 0 und 0 | 0 bis 2,5 und 0 bis 0,6 rem |
| `--bar-glass` | `1`: Milchglas (Hintergrundunschärfe und Schatten des Programms) hinter der Pille | `0` | nur `1` oder `0` |
| `--bar-pad-top` | Luft über der Leiste | 2 rem | 0 bis 3 rem |
| `--tab-glyph-bg`, `--tab-glyph-border` | Füllung und Rand der Symbole LB/RB | helles Grau, kein Rand | jede Farbe |
| `--tab-glyph-radius`, `--tab-glyph-size` | Rundung und Größe dieser Symbole | 0,4 und 0,8 rem | 0 bis 1 und 0,55 bis 1 rem |

**Farben** dürfen in jeder Schreibweise stehen (`#rgb`, `rgb()`, `hsl()`, `oklch()`, `color()`, `color-mix()`; das Programm rechnet sie nach sRGB um, ein weiter Farbraum wie Display-P3 gilt in der Leiste also nicht). Jede Schriftfarbe wird gegen den Untergrund geprüft, auf dem sie steht: die Füllung des Tabs über der Füllung der Pille über `--bg`. Ist sie durchsichtig (Alpha unter 0,3), nicht lesbar (Kontrast unter 1,5 zum Untergrund) oder keine Farbe, nimmt das Programm die Textfarbe des Themes (`--fg`) und, wenn auch die nicht steht, Schwarz oder Weiß. Jede andere lesbare Farbe bleibt, wie sie ist.

**Schriften** kommen nur aus einer Liste: die allgemeinen Familien (`system-ui`, `sans-serif`, `serif`, `monospace`, `ui-sans-serif`, `ui-serif`, `ui-monospace`, `ui-rounded`) und diese mit Windows oder Office installierten Namen: Segoe UI (auch Variable Display/Text/Small, Light, Semilight, Semibold, Black, Emoji, Symbol), Bahnschrift, Arial (auch Narrow, Black), Calibri, Cambria, Candara, Consolas, Constantia, Corbel, Courier New, Franklin Gothic Medium, Georgia, Impact, Lucida Console, Lucida Sans Unicode, Microsoft Sans Serif, Palatino Linotype, Segoe Print, Segoe Script, Sylfaen, Tahoma, Times New Roman, Trebuchet MS, Verdana, Agency FB, Century Gothic, Gill Sans MT, Rockwell, Bookman Old Style. Alles andere in `--tab-font` oder `--font` (die eigene Schrift eines Skins, ein in Anführungszeichen gesetztes `"sans-serif"`) bleibt für die Tabs außen vor; der Rest der Oberfläche nutzt `--font` weiter, wie er will. Deklariert ein Skin selbst ein `@font-face` mit dem Namen einer Systemschrift, nimmt das Programm diesen Namen aus der Liste der Tabs (`document.fonts` verrät es): Ein `@font-face` mit `size-adjust`, `ascent-override` oder `descent-override` kommt so nie an die Buchstaben. Die Schrift stimmt, wenn die Leiste das nächste Mal misst (bei Skin- und Fensterwechsel und alle 30 Sekunden).

Weitere Regeln der Leiste, nicht änderbar: Wortabstand, Position und Reihenfolge der Tabs (auch die der Plugins), das Anpassen an den Platz (core/nav.js: passt alles, bleibt alles; sonst werden zuerst die Abstände enger, dann die breiten Tabs schmaler, nie unter die Mindestbreite; reicht das nicht, scrollt die Leiste mit Pfeilen an den Rändern, der aktive Tab bleibt ganz), ein langer Name, der als Lauftext läuft, der Setup-Assistent, der die Tabs (nicht den Statusbereich) ausblendet, und die Verschiebung gegen Einbrennen. Die Tabs behalten ihren vollen Namen für Screenreader (`aria-label`); Controller, LB/RB und Maus gehen durch die Leiste wie vorher.

### Umstellen eines Skins von 0.6

Alte Selektoren für die Leiste laden weiter, wirken aber nicht mehr (`scripts/skin-check.mjs` meldet sie als Hinweis, nicht als Fehler). So wird aus der alten Regel eine Variable:

| Alt (0.6) | Neu (0.7) |
|---|---|
| `#tab-list li { font-size: 1.2rem; font-weight: 300; letter-spacing: 0.04em }` | `--tab-size: 1.2rem; --tab-weight: 300; --tab-letter: 0.04em` |
| `#tab-list li { color: … }`, `li.active { color: … }` | `--tab-color`, `--tab-color-active` |
| `li.active { border-color: … }`, `border-bottom: 0.14rem` | `--tab-accent`, `--tab-underline: 0.14rem` |
| `li.active { text-shadow: 0 0 1rem … }` | `--tab-glow: 1; --tab-glow-color: …` |
| `#tab-list { gap: … }`, `li { padding: … }` | `--tab-gap`, `--tab-pad-x`, `--tab-pad-y` |
| `li { border-radius: 2rem; background: … }`, `li.active { background: … }` | `--tab-radius`, `--tab-bg`, `--tab-bg-active` |
| `li { clip-path: polygon(… 0.7rem …) }` (abgeschnittene Ecke) | `--tab-cut: 0.7rem` |
| `body { text-transform: uppercase; letter-spacing: … }` (erbte in die Tabs) | `--tab-case: uppercase; --tab-letter: …` |
| `#tab-list { background: …; border; backdrop-filter; border-radius; padding }` (Glas-Pille) | `--bar-bg`, `--bar-border`, `--bar-glass: 1`, `--bar-radius`, `--bar-pad` |
| `#top { padding-top: … }` | `--bar-pad-top` |
| `.glyph.shoulder { … }` | `--tab-glyph-bg`, `--tab-glyph-border`, `--tab-glyph-radius`, `--tab-glyph-size` |
| `--tab-min-width` | wie bisher |

Die Skins im Katalog (Tiefsee, Grünlicht, Neonstadt, Nova, Sternenstaub, Synthwave) zeigen, wie weit das geht: Pille mit Milchglas (Nova), abgeschnittene Ecken (Neonstadt), Leuchten (Tiefsee, Synthwave). Die Werte für diese sechs Skins stehen in [SKINS-0.7-catalog.md](SKINS-0.7-catalog.md) (Englisch).

### Restrisiko

- Mit dem Aufbau **nicht mehr möglich** sind die Umgehungen, die die Laufzeitprüfung von 0.6.4 nicht fangen konnte: Schlitze in einer Ebene zwischen den geprüften Stellen (Ebenen der Seite liegen nie über der Leiste), `@font-face` mit `size-adjust` und `ascent-`/`descent-override` (Namen, die ein Skin selbst deklariert, gelten für die Tabs nicht), `::first-letter` und `::first-line` an den Tabs, viele `text-shadow` (die Leiste setzt höchstens einen, mit begrenzter Unschärfe) und Ebenen von `body`/`html` über der Leiste.
- **Der Untergrund hinter der Leiste** ist der des Programms: `--bg` mit 80 % Deckung, über der Seite, die Skins frei gestalten. Die Lesbarkeitsprüfung rechnet die Schriftfarben gegen diesen Untergrund über Schwarz und über Weiß; was darunter noch durchscheint (bis zu 20 %), kann den Kontrast nur wenig senken. Ein Skin, dessen `--bg` selbst nicht zu seinem `--fg` passt, bekommt die Ersatzfarbe.
- **Der Rest der Seite** (Ansichten, Hinweisleiste unten, Statusbereich) ist weiter gestaltbar, auch zum Schlechten: Ein Skin kann eine Ansicht überdecken oder Chips im Statusbereich ausblenden. Die Tabs bleiben, und `skin-check.mjs` meldet Regeln, die `html` und `body` ausblenden, verblassen oder maskieren.
- Ein `@font-face`, das erst durch eine Medienabfrage nach einer Messung greift, wird bei der nächsten erfasst (Größen- und Skinwechsel, alle 30 Sekunden); bis dahin kann kurz eine andere Schrift stehen.
- Die Leiste ist nur so stark wie der Browser unter ihr: ein WebView2 vor Version 114 hat keine Popover (siehe oben).
- Die Durchsicht des Katalogs bleibt: Skins im Katalog prüfen und signieren wir selbst.

### Prüfhilfe

`scripts/skin-check.mjs` (im CouchHUD-Repository; `scripts/catalog-entry.ps1` ruft es beim Packen eines Skin-Ordners auf) ist eine **Prüfhilfe für die Katalog-Durchsicht**. Es meldet als Fehler, was noch an die Seite reicht: ein `@font-face` mit `size-adjust` unter 80 % oder mit `ascent`-/`descent`-/`line-gap-override`, ein `@font-face` unter dem Namen einer Systemschrift und Regeln an `html`/`body`, die die Seite ausblenden, verblassen oder maskieren. Als Hinweis (ohne Fehler) meldet es Regeln für die alte Leiste, die nichts mehr tun, und Werte der `--tab-*`-/`--bar-*`-Variablen, die keine einfache Zahl sind. Den Ordner des Katalogs gibt man als Argument an (in einem Git-Worktree gibt es `../couchhud-catalog-src` nicht), oder in der Umgebungsvariablen `COUCHHUD_CATALOG_SKINS`.

## Quadratische Kacheln

Welches Bild eine Kachel zeigt, sagt der Skin mit der CSS-Variable `--tile-art`, gesetzt **dort, wo er die Form der Kachel setzt** (`aspect-ratio`): an der Kachel selbst (`.shelf .cover`, `.grid .cover`, `.shelf .hero-art`), an ihrer Reihe oder auf `:root[data-skin="…"]` für alle Spiel-Kacheln. Werte:

| Wert | Bild zuerst | Ersatz, wenn es fehlt |
|---|---|---|
| `square` | Quadrat-Bild (SteamGridDB: quadratische Grids, danach Icons; Steam selbst hat keins) | Cover |
| `cover` (Standard für Spiel- und App-Kacheln) | Cover | Quadrat-Bild |
| `hero` (Standard für die „Weiterspielen“-Karte) | Hero-Bild | Cover, dann Quadrat-Bild |

Jeder andere oder fehlende Wert gilt als Standard der Kachel; der Standard-Skin ändert sich nicht. Die App-Kacheln (16:9) und die „Weiterspielen“-Karte setzen ihren Wert selbst, ein `square` auf `:root` erreicht sie nicht; ein Skin, der die Karte quadratisch macht, setzt `--tile-art: square` an `.shelf .hero-art`.

Passt ein Bild nicht zur Form der Kachel (ein Cover als Ersatz in einer quadratischen Kachel, ein Store-Header auf einer Cover-Kachel), zeigt CouchHUD es **ganz und mittig über einer unscharfen Kopie** von sich (Klasse `art-fit`, dazu `art-wide`, wenn das Bild breiter ist als die Kachel), nie gestreckt und nie mit abgeschnittenem Titel. Ein Quadrat- oder Hero-Bild in einer Kachel, die danach fragt, füllt sie. Die Form der Kachel liest CouchHUD aus ihrem `aspect-ratio`. Jede Kachel trägt `data-tile-art` mit dem Bild, das sie zeigt, und bei `square` die Klasse `art-square`.

Ein Skin-Wechsel schaltet die Bilder **sofort** um, in beide Richtungen, ohne Neustart und ohne neuen Scan: Sobald das Stylesheet des neuen Skins geladen ist, fragt jede Kachel neu. Es werden dabei nur Bilder geladen, die die Bibliothek schon kennt; fehlende Quadrat-Bilder holt nur die normale Artwork-Suche (mit SteamGridDB-Schlüssel und nur, solange die Datenschutz-Schalter sie erlauben). Das Quadrat-Bild wählt der Nutzer unter *Artwork ändern* im Reiter „Quadrat“.

```css
/* Quadratische Kacheln in der Home-Reihe, die Bibliothek bleibt bei Covern. */
[data-skin="user:synthwave"] .shelf .cover {
  aspect-ratio: 1;
  --tile-art: square;
}
```

## Mauszeiger

Bewegt der Nutzer eine Maus, zeichnet CouchHUD einen eigenen Zeiger. Er kommt aus dem Skin: Jeder Skin darf ihn setzen, ein Skin ohne eigenen Zeiger zeigt automatisch den Standard-Pfeil von CouchHUD. Mit der Einstellung „Echter Mauszeiger“ (Einstellungen › Darstellung) zeigt der Nutzer stattdessen den normalen Windows-Zeiger, dann greift dein Zeiger nicht.

| Variable | Wirkung |
|---|---|
| `--pointer` | Bild des Zeigers, am besten ein kleines SVG als `data:`-Adresse: `url("data:image/svg+xml,…")` (`#` als `%23` schreiben) |
| `--pointer-size` | Kantenlänge des quadratischen Bildes, Standard `4.6rem` |
| `--pointer-hx`, `--pointer-hy` | Hotspot als Bruchteil der Größe, `0 0` ist die linke obere Ecke, `0.5 0.5` die Mitte. Genau dort sitzt die Mausposition |

```css
:root[data-skin="user:synthwave"] {
  --pointer: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><path d='M5 3 L5 35 L13 27.5 L19 41 L25.5 38.2 L19.5 25 L31 25 Z' fill='%23ff2bd6' stroke='%2312141c' stroke-width='3.2' stroke-linejoin='round'/></svg>");
  --pointer-hx: 0.104;  /* Spitze des Pfeils bei (5, 3) von 48 */
  --pointer-hy: 0.0625;
}
```

Tipps: Ein Zeiger muss auf dem Fernseher aus dem Sessel auffallen. Eine dunkle Kontur (hier `#12141c`) hält ihn auf hellem Hintergrund sichtbar, eine helle Füllung auf dunklem. Der Zeiger ruht, solange die Maus still steht. Seit 0.7 ist der Zeiger ein geschlossener Shadow-Root in der obersten Ebene: `#cursor` und `#cursor .cp` erreicht kein Selektor mehr, der Skin steuert ihn nur über `--pointer`, `--pointer-size` (1 bis 8 rem) und `--pointer-hx`/`--pointer-hy` (0 bis 1); Klick-Quetschen und Lauf-Animation macht das Programm selbst (aus bei „Animationen reduzieren“). Das ist nur der Zeiger innerhalb von CouchHUD. In anderen Fenstern bleibt es der Windows-Zeiger.

## Ausprobieren

CouchHUD mit `--dev` starten, unter *Einstellungen › Darstellung › Skin* deinen Skin wählen. Änderungen an der CSS siehst du nach erneutem Auswählen.

Ohne Windows geht es auch: `node scripts/preview-server.js` startet die Oberfläche mit Beispieldaten im Browser. Eigene Skin-Ordner dort mit `CATALOG_SKINS=<Ordner mit deinen Skin-Ordnern> node scripts/preview-server.js` ausliefern und einen Skin mit `node scripts/ui-shot.mjs --catalog <Ordner> skin:user:<Ordnername> shot:home` ansehen. Das Stylesheet wird dort von Internet-Adressen befreit, genau wie in der App.

## Teilen

Ordner als `.zip` packen (mit oder ohne übergeordneten Ordner), auf GitHub mit dem Topic `couchhud-skin` veröffentlichen. Andere installieren ihn in CouchHUD unter *Einstellungen › Darstellung › Neue Skins installieren*.

Für den Katalog: Gib dem Skin einen eigenen Namen, keine fremde Marke (kein Konsolen-, Hersteller- oder Spielename), und verwende keine fremden Logos. Ein Stil darf an etwas erinnern, aber nicht danach heißen.
