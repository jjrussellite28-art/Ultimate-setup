# CouchHUD Roadmap

Ziel: Der Wohnzimmer-PC fühlt sich an wie eine Konsole. Einschalten, automatisch anmelden, CouchHUD ist da, und **alles**, was man am TV braucht, geht mit dem Controller und aus dieser Oberfläche heraus. Kein Griff zur Funktastatur, kein Ausflug auf den Desktop, außer man will bewusst in den PC-Modus.

## Leitplanken

Diese Regeln gelten für jedes Feature und jedes Plugin:

1. **Alles über die GUI.** Was sich konfigurieren lässt, lässt sich in CouchHUD konfigurieren. Die `config.json` ist Speicher, keine Bedienoberfläche.
2. **Controller zuerst, Maus und Tastatur optional.** Jeder Screen ist komplett per Pad bedienbar. Texteingabe geht über die eigene Bildschirmtastatur.
3. **Nahtlos und Fullscreen.** Kein Flackern, kein Desktop zwischendurch, keine Fenster-Ränder, keine fremden Dialoge, wo es sich vermeiden lässt.
4. **Fremdsoftware gehört in Plugins.** FanControl, SignalRGB, CaseHUD, UniGetUI, Kodi … kommen nie in den Kern. Der Kern liefert die Erweiterungspunkte.
5. **Die Shell läuft nie als Admin.** Was Admin-Rechte braucht, erledigt ein Dienst über eine eng definierte Schnittstelle. UAC gibt's genau einmal, beim Setup.
6. **Ein kaputtes Plugin darf nie die Shell killen.** CouchHUD ist die Shell, ein Absturz bedeutet sonst schwarzen Bildschirm.

## Zielarchitektur

```
┌────────────────────────── Benutzer-Sitzung ───────────────────────────┐
│  CouchHud.Shell (WPF + WebView2)                                      │
│   ├─ UI (ui/): Screens, Skins, Bildschirmtastatur, Settings-Renderer  │
│   ├─ Core: Library, Config, Artwork, Skins                            │
│   └─ Plugin-Host ── Plugins (je eigener Load-Context)                 │
│        z.B. SignalRGB, CaseHUD, FanControl, RSS, Kodi, winget (User)  │
└───────────────▲───────────────────────────────────────────────────────┘
                │ Named Pipe, nur für den angemeldeten Benutzer (ACL),
                │ feste Liste erlaubter Operationen
┌───────────────┴──────── CouchHud.Service (LocalSystem) ───────────────┐
│  Windows Update · winget (Machine-Scope) · Self-Update                │
│  privilegierte Plugin-Operationen (z.B. FanControl neu laden)         │
└───────────────────────────────────────────────────────────────────────┘
```

### Was ein Plugin können soll (SDK v1)

| Erweiterungspunkt | Wofür | Beispiel |
|---|---|---|
| `ILibraryProvider` *(gibt's schon)* | Einträge für die Bibliothek | Kodi-Filme, GOG-Spiele |
| **Settings-Seite** | Einstellungen, deklarativ beschrieben (Toggle, Slider, Auswahl, Text, Ordner, Button) und von CouchHUD im aktuellen Skin gerendert | SignalRGB: Standard-Effekt |
| **Widgets** | Kacheln/Karten für Home und Quick-Menü | CPU-/GPU-Temperatur, News-Karussell |
| **Quick-Actions** | Einträge im Guide-Menü | „Licht aus“, „Lüfter leise“ |
| **Seiten** | Eigene Vollbild-Screens, deklarativ oder als eigenes HTML-Panel | RSS-Reader, Mediathek |
| **Events** | Reagieren auf `GameStarted`, `GameStopped`, `PcModeEntered`, `Idle`, `Sleep`, `Wake`, `HeadphonesConnected` | Pro Spiel andere Lüfterkurve |
| **Benachrichtigungen** | Toasts in CouchHUD | „3 Updates verfügbar“ |
| **Hintergrund-Tasks** | Periodische Arbeit mit Lebenszyklus | Feeds aktualisieren |
| **Privilegierte Operationen** | Plugin definiert Operation, Service führt sie aus (vom Nutzer einmal freigegeben) | FanControl-Profil wechseln |

**Deklarativ vor HTML**: Wenn ein Plugin seine UI als Datenmodell beschreibt (Liste, Karte, Slider …), sieht sie in jedem Skin richtig aus und ist automatisch controllerbedienbar. Eigene HTML-Panels gibt's als Notausgang für Spezialfälle.

---

## Meilensteine

Die Reihenfolge folgt den Abhängigkeiten: Erst das Fundament, auf dem alles andere aufsetzt (Settings-UI, Tastatur, Maus), dann die Konsolen-Basics, dann das Plugin-SDK (getestet am RSS-Reader), dann der Dienst für alles mit Admin-Rechten und erst danach die Hardware-Plugins, weil z.B. FanControl als Admin läuft.

### Stand (Oktober 2026)

| Meilenstein | Erledigt | Offen |
|---|---|---|
| M1 Fundament | Settings-Center (Einfach/Experte, Ein-Knopf-Einrichtung), Bildschirmtastatur (mit Handy-Eingabe per QR), Datei-Browser, Ordner-Scan und überwachte Spieleordner, Einträge bearbeiten/ausblenden, Suchen/Sortieren/Favoriten-Filter, Maus (Comic-Zeiger), Controller als Maus (für Logins und Dialoge), Boot-Screen mit Logo, Artwork-Auswahl, Sammlungen, Spielstart-Übergang | Import der Steam-Kategorien in Sammlungen |
| M2 Konsolen-Basics | Guide-Menü, **Guide-Taste antippen holt CouchHUD über das laufende Spiel, Spiel läuft weiter (optional pausiert)**, Spiel beenden und erzwingen, Spielzeit, Audio inkl. Auto-Wechsel, Controller-Akku, Controller-Entdoppelung (virtuelle Pads), Bluetooth, **Lautstärke pro App**, **WLAN** (Einstellungen › Netzwerk), Anzeige (Auflösung, Hz, HDR), Nachtmodus, Spielprofile, Benachrichtigungscenter, Ausschalt-Timer, Task-Manager, **Bildschirmschoner gegen OLED-Einbrennen**, **Vordergrund-Wächter** („CouchHUD bleibt vorne“) | Praxistest auf echter Hardware |
| M2b Spielstände & Installieren | Steam-Downloads mit Fortschritt, **Spielstände** (Ludusavi-Manifest: Sichern, Wiederherstellen, Sicherheits-Backup, Backup vor dem Deinstallieren) | Registry-Spielstände, ISO/Setup per Controller; nicht installierte Store-Spiele installieren und automatisches Backup nach Spielende: siehe Plan für 0.5 |
| M2c Design | Clear Logos, Hintergrund-Trailer, **Artwork aus lokalen Quellen mit Cache** (Epic-, Xbox-, Ubisoft-Cache, Steam-Bibliothek, eigener Banner als Notlösung), Farben aus dem Artwork, Übergänge, Boot-Animation, Rumble, Leistungsanzeige, Aufnahmen-Galerie, **Sounds pro Skin, Hintergrundmusik**, **HDR-Modus**, Logo und App-Icon, Parallax/Tilt, „Animationen reduzieren“ | – |
| M3 Plugins & News | **SDK v1** (Seiten, Settings, Quick-Actions, Tools, Events, Benachrichtigungen, Sprache), Plugin- und Skin-Verwaltung mit .zip-Installation, Wand-Plugin, News-Tab, Doku für die Community, **Plugin-Katalog** (JSON-Index auf GitHub, signiert, SHA-256-Pflicht) mit Installation und Update-Hinweis, **Katalog-Repo `kewe87/CouchHUD-Catalog` mit echten Einträgen (Wand, Synthwave)**, der Beispielindex im Repo spiegelt den echten | Hot-Reload von Plugins (Updates ohne Neustart) |
| M3b KI & MCP | Assistent (Anthropic, OpenAI), MCP-Server mit Bestätigung am TV, Plugin-Tools | MCP-Ressourcen, Spracheingabe |
| M4 Updates & Dienste | Windows Update, winget, Couch/PC-Profile für Autostart und Dienste (geplante Aufgabe statt Dienst), Neustart in den PC-Modus, **Spiele-Clients starten leise im Couch-Modus**, **Self-Update** (GitHub-Releases, signierte Prüfsummen, stille Installation) | CouchHud.Service für Windows Update ohne UAC (Plan für 0.5), UniGetUI, Self-Update im Hintergrund vorladen |
| M6 Medien & Smart Home | Home Assistant, Wetter, Handy als Fernbedienung und Tastatur (auch für Login-Fenster), weitere Spiele-Quellen (GOG, Ubisoft, Xbox) | Kodi/Jellyfin-Plugins, Spiele aus EA app und Battle.net in der Bibliothek (die Clients starten schon mit), Game-Streaming-Status |
| M7 Release | **Couch-Sperre** (Controller-Code oder Windows Hello), Wächterprozess, Task-Manager-Sperre, automatische Anmeldung (Passwort geprüft, als LSA-Secret von Windows abgelegt), **Notfall-Modus** (Strg+Umschalt oder LB+RB+View, Crash-Zähler; zweistufig: erst Windows-Sperre, Shell und Autologin fallen erst nach der Windows-Anmeldung weg), **Jugendschutz**, **Installer** (Inno Setup, WebView2-Prüfung, Deinstallation setzt Windows-Eingriffe zurück), **Ein-Knopf-Einrichtung**, **Englisch** (Oberfläche, Host, Tray, Installer), signierte Release-Prüfsummen, **Shell ohne Explorer**, **Hilfsaufgaben pro Konto** (auch Standardkonten), **gehärtete Couch-Sperre**, **Audit-Fixes** aus mehreren VM-Tests, **CaseHUD-Integration** | Authenticode-signierte Builds (SmartScreen), Issue-Vorlagen |

### Erledigt seit 0.3 (Release 0.4.0)

- ✅ **Katalog-Repo** `kewe87/CouchHUD-Catalog` mit echten Einträgen (Wand, Synthwave), signiert.
- ✅ **Artwork-Cache**: Bilder aus lokalen Quellen und dem Netz werden einmal geladen und lokal gehalten (`%LOCALAPPDATA%\CouchHudrtwork`, LRU-Begrenzung), die Oberfläche lädt nie selbst aus dem Netz.
- ✅ **CaseHUD-Integration** (Einstellungen › CaseHUD): Installation aus signierten GitHub-Releases, Start, Steuerung über die lokale API, Spielmeldung an CaseHUD ([CASEHUD.md](CASEHUD.md)). Sie ist eine eingebaute Integration im Host, kein Plugin; CaseHUD selbst bleibt ein eigenes Programm.
- ✅ **Couch-Sperre gehärtet** (siehe unten, Punkt 11), **Notfall-Modus zweistufig** (erst Windows-Sperre, dann Entfernen von Shell und Autologin).
- ✅ **Audit-Fixes** aus mehreren Review- und VM-Testrunden (Jugendschutz, Handy-Fernbedienung, Autologin, Wiederherstellung, eigener Firewall-Regelname „CouchHUD Remote (CouchHUD)“, Plugin-Freigabe per Hash).
- ✅ **Shell ohne Explorer**: CouchHUD startet als Shell, ohne dass der Explorer mitläuft; der Desktop kommt im PC-Modus bzw. nach dem Notfall-Modus.
- ✅ **Hilfsaufgaben pro Konto**: die einmalige Einrichtung legt feste Aufgaben je Windows-Konto an, auch für Standardkonten.

### Plan für 0.5

In dieser Reihenfolge:

1. **VPN-Dienste**, zuerst **NordVPN**: im Guide und in den Einstellungen VPN ein- und ausschalten, Land bzw. Ort wählen, Status (verbunden, Ort, Server) sehen. Gerade für Streaming interessant. Anbindung über die Kommandozeile bzw. Schnittstelle der installierten VPN-App (kein eigener VPN-Client in CouchHUD, keine Zugangsdaten in CouchHUD); weitere Anbieter (z. B. ProtonVPN, Surfshark, WireGuard-Profile) über dieselbe Schnittstelle bzw. als Plugin.
2. **Nicht installierte Spiele per Controller installieren.** Nicht installierte Spiele aus Steam/Epic als eigene Reihe zeigen und die Installation anstoßen, Fortschritt anzeigen (siehe M2b). Dazu ein **zentraler Downloads-Reiter** wie auf der PlayStation: alles, was gerade lädt oder installiert, an einer Stelle (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Microsoft Store, Windows-Updates, App-Updates, Updates von CouchHUD, Plugins und CaseHUD), mit Fortschritt und Hinweis, wenn etwas fertig ist. Für Clients ohne offizielle Schnittstelle nur, soweit sich der Fortschritt zuverlässig lokal auslesen lässt.
3. **TV-Steuerung** über HDMI-CEC bzw. die Netzwerk-API des Fernsehers: ein-/ausschalten, Eingang wählen, Lautstärke.
4. **Reihe „Weiterspielen“** auf Home mit Spielzeit und Fortschritt, dazu ein **automatisches Spielstand-Backup nach Spielende** (baut auf den Spielständen aus M2b auf).
5. **Profile pro Person**: eigene Favoriten, Sammlungen, Spielzeit-Regeln und Einstellungen je Person statt nur je Windows-Konto.
6. **Smart Home nur mit dem, was man braucht.** Nach dem Verbinden zeigt der Tab nicht mehr pauschal alle Entitäten (bei großen Installationen über tausend Sensoren), sondern nur ausgewählte: eine Auswahl-Seite nach Bereichen (HA-Areas), Typ (Licht, Szene, Schalter, Mediaplayer, Klima, Sensor) und Suche, dazu sinnvolle Vorschläge (Szenen, Lichter, Mediaplayer der Bereiche). Leere Auswahl zeigt einen Einstieg in die Auswahl statt einer Liste.
7. **Home Assistant ohne Token-Abtippen:** „Mit Home Assistant anmelden“ öffnet die HA-Anmeldung im eingebauten Browser (OAuth-Flow von HA), CouchHUD bekommt den Zugang selbst. Bis dahin gibt es bei Tokens und API-Keys „Vom PC einfügen“ (Zwischenablage) und Strg+V.

CouchHud.Service (Windows-Dienst für alle Admin-Operationen) ist vorgezogen und kommt mit 0.4.1.

### Als Nächstes

Nach Nutzen für den Alltag sortiert. Erledigt ist hier abgehakt, offen steht ehrlich dabei:

1. **Praxistest auf echter Hardware (offen).** Fast alles ab v0.3 ist nur per CI kompiliert, im Browser-Preview und in einer Hyper-V-Test-VM geprüft, nicht auf einem echten Couch-PC am TV. Besonders prüfen: Guide-Tap über Exclusive-Fullscreen-Spielen, Pause (NtSuspendProcess) mit verschiedenen Spielen, Wächter und Sperre, Vordergrund-Wächter mit allen Spiele-Clients, Jugendschutz-Einblendung über Spielen, Display-Aus und Aufwecken per Controller, Autologin-Dialog per Controller-Maus, Controller-Entdoppelung mit echten Steam-Input-Pads.
2. ✅ **Installer** (Inno Setup, `C:\Program Files\CouchHud`, WebView2-Prüfung, Startmenü, Deinstallation stellt Shell und Autologin zurück).
3. ✅ **Ersteinrichtung per Controller**: der Konsolen-Knopf *CouchHUD als Konsole einrichten* (Übersicht, Durchlauf, Ergebnisliste, Rückweg).
4. ✅ **Self-Update** (am TV noch zu testen): GitHub-Releases-API mit ETag, Updates-Seite mit Release-Notes, Download nur von github.com über https, Pflicht-Signatur der Prüfsummen-Datei (`scripts/sign-release.ps1`), stille Installation mit Neustart auch als Shell. Offen: Authenticode-Signatur des Setups, Update im Hintergrund vorladen.
5. ✅ **Lautstärke pro App** und **WLAN**.
6. ✅ **Plugin-Katalog** (am TV noch zu testen): signierter JSON-Index auf GitHub, Installation und Updates direkt aus der Plugin-Seite. Das Repo `kewe87/CouchHUD-Catalog` existiert mit echten Einträgen (Wand, Synthwave).
7. ✅ **Spielstände** (Ludusavi, siehe M2b). Offen: Registry-Spielstände, Cloud-Ordner als Ziel; **nicht installierte Store-Spiele** und das automatische Backup nach Spielende stehen im Plan für 0.5.
8. ✅ **Jugendschutz** (am TV noch zu testen): Spielzeit-Limit pro Tag (Wochenende getrennt), Zeitfenster, Limit pro Spiel, Warnung 15 und 5 Minuten vorher, „Spielzeit vorbei“ mit Speichern-und-Beenden oder Verlängern per Eltern-Code, gesperrte Spiele und USK-Grenze, Schutz vor verstellter Systemuhr, auch für Spiele, die ohne CouchHUD gestartet wurden. Offen: Spiele ohne erkennbaren Prozess (URL-Start) lassen sich nicht messen; Schutz nur gegen Kinder am Controller (Dateizugriff auf den Benutzerordner umgeht ihn); pro-Kind-Profile.
9. ✅ **Englische Oberfläche** (am TV noch zu testen): Englisch und Deutsch in UI, Host-Meldungen, Tray und Installer; Steam-Beschreibungen in der UI-Sprache; Einstellungen › Info › Sprache (Automatisch folgt der Windows-Anzeigesprache), sofort wirksam. Texte und Begriffe: [i18n.md](i18n.md), Prüfung `scripts/i18n-check.mjs`. Offen: weitere Sprachen.
10. **Signierte Builds (offen).** Die Release-Prüfsummen sind signiert, das Setup selbst aber nicht per Authenticode, deshalb warnt SmartScreen. Braucht ein Code-Signing-Zertifikat.
11. ✅ **Härtung der Couch-Sperre** (in mehreren VM-Tests geprüft, am TV noch zu testen): nur Spiele ohne Entsperrung, Positivliste und Liste klarer Nicht-Spiele für den Vordergrund, Datei- und Druckdialoge zu, Windows-Richtlinien und Eingabehilfe-Kürzel nur bei Sperre und reversibel, Browser nur entsperrt und ohne Dialoge, Kennwort-Pflicht für die Windows-Anmeldung als Sperre (Controller-Code mit Warnung, Autologin entfällt ohne Kennwort). Die Sperre bleibt so stark wie das Windows-Kennwort und schützt nur vor Kindern und Gästen am Controller, nicht vor Dateizugriff, Startmedium oder Admin-Rechten; BitLocker bzw. Geräteverschlüsselung wird empfohlen (Hinweis in der Ein-Knopf-Einrichtung).
12. ✅ **Steam-Config-Patch („Paket C“, in der VM geprüft, am TV noch zu testen).** Steam kommt CouchHUD im Couch-Modus nicht in die Quere: kein Big-Picture-Start (`HKCU\Software\Valve\Steam › StartupMode` 4 → 7), die Guide-Taste fokussiert Steam nicht (`localconfig.vdf › Controller_CheckGuideButton` = 0), kein Big-Picture-Overlay beim Controller (`system/EnableSCTenFootOverlayCheckNew` = 0), keine Einblendung beim Verbinden eines Controllers (`system/ControllerConnectNotificationToast` = 0). Alle vier Schlüssel wurden in einem Steam-Client einzeln durch eine einzige Einstellung in der Oberfläche und Vergleich der Dateien bestätigt (Details im Quelltext von `SteamGuard.cs`). Umsetzung: `SteamGuard` (Core) mit eigener Sicherung `guard-steam.json` über `GuardSnapshot` (neu: `OnlyIfUnchanged`, zurückgesetzt wird nur, was noch unser Wert ist) und `SteamGuardService` (Shell): Steam schreibt seine Dateien selbst, also nur bei beendetem Steam; läuft es, wird es nur zum Anwenden und nur in einem ruhigen Moment (kein Spiel, kein Steam-Fenster vorn, kein Mausmodus/Login, kein Download, keine offene Anmeldung; vor `-shutdown` erneut geprüft) mit `steam -shutdown` sauber beendet (nie mit Gewalt, nach 45 s ohne Erfolg wird nichts geschrieben und mit Pausen erneut versucht, ein spätes Ende führt trotzdem zum leisen Neustart), geschrieben, nach kurzer Pause verifiziert und leise neu gestartet. Das Zurücksetzen startet Steam nie neu, es wartet, bis Steam nicht läuft (der Run-Eintrag bleibt bis zur verifizierten Rücksetzung; Beenden wartet auf nichts). Setzt der Nutzer einen unserer Werte zurück, wird er in dieser Couch-Sitzung nicht erneut erzwungen. Prüfung und Schreiben laufen unter einem prozessübergreifenden Mutex (`SteamGuardRunner`, mit Fake-Steam getestet). Teil von „CouchHUD bleibt vorne“, keine eigene Einstellung. Zurück bei PC-Modus, Light-Modus, Option aus, Beenden, nach einem Absturz (Run-Eintrag `CouchHudReleaseSteamGuard` und nächster Start); bei der Deinstallation nicht durch den Administrator-Prozess (VDF ist dort bewusst verboten), das erledigt das Beenden von CouchHUD. Bewusst nicht angefasst: „Guide-Tastenkürzel erlauben“ (`SteamController_Enable_Chord`, bestätigt, würde aber Steams Guide-Kürzel in Spielen mit Steam-Eingabe wegnehmen) und das Overlay selbst. Offen: die Guide-Taste selbst ließ sich ohne Controller nicht drücken (geprüft sind Dateien, Registry und die Schalter in Steams Oberfläche); ob Steam Input in einem Spiel mit eigener Steam-Eingabe die Guide-Taste weiterhin abfängt, ist am TV zu prüfen.
13. **Kodi/Jellyfin, UniGetUI (offen).** Media-Plugins (M6) und UniGetUI als zweite App-Update-Quelle (M4). Der Windows-Dienst CouchHud.Service steht im Plan für 0.5.
14. **Neue Ideen:** Hot-Reload von Plugins; MCP-Ressourcen; Import der Steam-Kategorien als Sammlungen; Spracheingabe am Handy; Screenshot-Regressionstests für alle Skins in der CI (`scripts/ui-shot.mjs` gibt es schon); weitere Sprachen über Community-Übersetzungen.

Home Assistant, Wetter und News sind vorerst eingebaute Integrationen. Sobald das SDK v1 eigene Screens und Settings-Seiten kann, werden sie zu Plugins (Leitplanke 4).

### ✅ M0: Prototyp (v0.2, erledigt)

- Konsolen-UI mit Home, Spiele, Apps, System
- Steam, Epic, eigene Einträge, Cover über SteamGridDB/Steam-Store
- XInput global inkl. Guide-Button, PC-Modus, Shell-Registrierung, Power
- Skins: CouchHUD, PS4 Classic, Xbox, Cyberpunk, Nova, plus eigene
- Plugin-Loader mit `ILibraryProvider`
- Detailseite: Beschreibung (Steam-Store, deutsch), zuletzt gespielt, Größe, Installationsort, Entwickler/Genres, Deinstallieren
- C#/.NET 10, WebView2, CI auf Windows

### M1: Fundament – „Alles über die GUI“

*Ohne das hier ist jedes spätere Feature halb fertig.*

- **Settings-Framework**: Ein Renderer für deklarative Einstellungen (Toggle, Slider, Auswahl, Text, Ordner-Auswahl, Aktion). Alle heutigen `config.json`-Werte bekommen eine Seite. Dasselbe System nutzen später die Plugins.
- **Bildschirmtastatur** für den Controller: QWERTZ, Großschreibung, Sonderzeichen, Shortcuts (`.de`, `@`, `www.`), Trigger als Cursor, Y = Leerzeichen, X = Löschen.
- **Ordner-Browser** per Controller (Laufwerke, Ordner, Favoriten), ohne Tastatur.
- **Bibliotheksverwaltung**:
  - Spieleverzeichnisse hinzufügen/entfernen, Scan nach `.exe` mit Vorschlägen (ignoriert Installer, Uninstaller, Crash-Reporter, Redistributables)
  - Einträge umbenennen, ausblenden, als Spiel/App einordnen, Startparameter setzen
  - **Artwork-Auswahl** (erledigt): Detailseite → „Artwork ändern“, je Raster für Cover, Banner und Logo mit allen Treffern von SteamGridDB und Steam, eigenes Bild aus Datei, „Automatisch“ als Zurücksetzen; gespeichert als Override pro Eintrag in der `config.json`
  - **Sammlungen** (erledigt): anlegen, umbenennen, löschen, Einträge per Y-Menü oder Detailseite hinzufügen, als Filter in Spiele/Apps, als Kachel an Home anheften. Import der Steam-Kategorien ist offen (das `cloud-storage`-Format von Steam ist nicht dokumentiert und ändert sich)
- **Maus**:
  - Bei Bewegung erscheint ein großer, gut sichtbarer Zeiger (skinbar), nach ca. 2 s ohne Bewegung blendet er aus
  - Hover hebt hervor, verschiebt aber nicht den Controller-Fokus. Ein Klick wählt aus und aktiviert, das Mausrad scrollt.
  - Sobald wieder der Controller benutzt wird, verschwindet der Zeiger sofort.
- **Nahtloser Fullscreen**:
  - Schwarzer Start ohne Flackern: Fenster erst zeigen, wenn die UI gerendert ist, mit Boot-Splash im Skin-Stil
  - Übergänge beim Spielstart („Spiel startet …“ mit Artwork), bis das Spielfenster da ist
  - Kein Windows-Explorer-Dialog, wo CouchHUD eine eigene Lösung hat

### M2: Konsolen-Basics

- **Guide-Overlay / Quick-Menü**: Erkennt das laufende Spiel (Prozessbaum, Fenster im Vordergrund). Bietet „Zurück zum Spiel“, „Spiel beenden“ (mit Bestätigung), Lautstärke, Audio-Ausgang, Uhrzeit, Akku der Controller.
- **Spielzeit-Tracking**: pro Spiel und Woche, als Anzeige auf der Spielkarte.
- **Audio**:
  - Lautstärke und Mute systemweit, Lautstärke pro App (Core-Audio-Sessions, laufendes Spiel zuerst) *(gibt's schon)*
  - Ausgabegerät wählen (TV/HDMI, Kopfhörer, Soundbar, Bluetooth)
  - **Automatischer Wechsel**, wenn Kopfhörer angeschlossen werden, mit Toast „Ausgabe: Kopfhörer“ und Rückwechsel beim Abziehen
  - Technik: Windows Core Audio (`IMMDeviceEnumerator`, Gerätewechsel-Events), Standardgerät setzen über `IPolicyConfig`
- **Anzeige**: Auflösung, Bildwiederholrate, HDR an/aus, Skalierung, Bildschirm/TV wählen
- **Energie**: Standby, Neustart, Herunterfahren *(gibt's schon)*, dazu Bildschirm aus, Ausschalt-Timer („in 30 Min ausschalten“), Energiesparplan
- **Netzwerk** *(gibt's schon)*: WLAN-Liste, verbinden (Passwort über Bildschirmtastatur), trennen, Status, IP. Technik: `Windows.Devices.WiFi` (kein Admin, Standortzugriff nötig)
- **Bluetooth**: Geräte suchen, koppeln, trennen, für Controller und Headsets. Technik: `Windows.Devices.Bluetooth` / `DeviceInformationPairing`.
- **Benachrichtigungen**: einheitliche Toasts und eine Benachrichtigungsliste im Quick-Menü
- **Controller-Status**: verbundene Pads, Akkustand (XInput Battery), Zuordnung Spieler 1–4

### M2b: Spielstände und Installieren

**Spielstände**
- Speicherorte über das [Ludusavi-Manifest](https://github.com/mtkennerly/ludusavi-manifest) (Community-Datenbank aus dem PCGamingWiki, tausende Spiele, Steam-ID und Name als Schlüssel)
- Auf der Detailseite: wo liegen die Saves, wie groß, wann zuletzt geändert
- Backup/Restore per Knopfdruck (ZIP mit Datum), automatisches Backup vor dem Deinstallieren anbieten
- ✅ Umgesetzt: Manifest wöchentlich per ETag nachgeladen (eigener, streamender YAML-Leser, ~0,5 s für 53.000 Spiele), Zuordnung per Steam-ID, sonst normalisierter Name (inkl. Alias); Platzhalter, Wildcards (`*`, `?`, `**`) und `<storeUserId>` für Windows; Backup als ZIP samt `manifest.json`, Wiederherstellen mit automatischem Sicherheits-Backup, Zip-Slip-Schutz und Beschränkung auf die Speicherorte des Spiels, Rotation, Sperre während das Spiel läuft, Backup-Angebot vor dem Deinstallieren
- Offen: Registry-Spielstände sichern, Cloud-Ordner als Ziel, automatisches Backup nach Spielende, Wiederherstellen in einen anderen Windows-Benutzer

**Spiele installieren**
1. **Store-Spiele** (der häufigste Fall): nicht installierte Spiele aus der Steam-/Epic-Bibliothek als eigene Reihe zeigen und die Installation anstoßen (`steam://install/<id>`). Fortschritt anzeigen, sobald der Launcher ihn preisgibt.
2. **ISO / Setup**: Datei-Browser per Controller → ISO einhängen (Windows-Bordmittel) → `setup.exe` bzw. `autorun.inf` finden und starten. Danach wird das neue Spiel per Scan des Zielordners als Eintrag vorgeschlagen.
3. **Portable Spiele / Archive** (z.B. GOG-Offline-Installer, Emulator-Pakete): Ordner wählen, EXE wählen, fertig.

**Die zwei echten Hürden dabei:**
- **UAC**: Der UAC-Dialog läuft auf dem „sicheren Desktop“, dort funktioniert kein Controller. Lösung über den Dienst aus M4: Er startet das vom Nutzer gewählte Setup erhöht in der Benutzersitzung, ohne dass ein Dialog auftaucht.
- **Setup-Assistenten sind Maus-Dialoge**: Dafür gibt es einen **„Controller als Maus“**-Modus (linker Stick = Zeiger, A = Klick, B = Rechtsklick, X = Bildschirmtastatur), der sich automatisch einschaltet, solange ein Setup läuft. Das braucht es ohnehin auch für den PC-Modus.

### M2c: Design-Offensive („PC Master Race“)

*Ziel: Man schaltet den TV ein und will gar nicht mehr weg. Läuft parallel zu M1/M2, jeder Punkt einzeln auslieferbar.*

- **Clear Logos statt Text** auf Home und Detailseite *(Grundlage steht)*, dazu Banner, Icons und Hero-Art aus mehreren Quellen (siehe unten)
- **Trailer-Vorschau**: Bleibt der Fokus kurz auf einem Spiel, läuft der Steam-Trailer stumm im Hintergrund an, wie auf der PS5
- **Farben aus dem Artwork**: Akzentfarbe, Glow und Hintergrund-Tönung werden live aus dem Cover des fokussierten Spiels gezogen
- **Tiefe und Bewegung**: Parallax zwischen Hintergrund, Logo und Kacheln, leichter 3D-Tilt mit Lichtreflex auf der fokussierten Kachel, weiche Seitenübergänge (View Transitions), Boot-Animation pro Skin
- **Haptik**: dezentes Controller-Rumble bei Fokus, Bestätigen und Anschlag (XInput-Vibration, abschaltbar)
- **Sounddesign pro Skin**: eigene Klangsets statt der heutigen Synth-Töne
- **Live-Systemleiste**: CPU-/GPU-Temperatur, Auslastung, FPS im laufenden Spiel (Sensoren über CaseHUD/LHM), schick und nur auf Wunsch
- **„Weiterspielen“-Karte** ganz groß mit Spielzeit und Fortschritt
- **Performance-Budget**: 60 fps auf 4K, keine Ruckler beim Scrollen durch große Bibliotheken (Virtualisierung ab ~300 Kacheln)

**Artwork-Quellen (Clear Logos, Banner & Co.)**

| Quelle | Was es gibt | Zugang | Status |
|---|---|---|---|
| Steam-CDN | Cover, Hero, Logo, Header für Steam-Spiele | offen | ✅ genutzt |
| [SteamGridDB](https://www.steamgriddb.com) | Grids (Cover, Banner), Heroes, **Clear Logos**, Icons, auch für Nicht-Steam-Spiele und Apps | kostenloser API-Key, Nutzung per API ausdrücklich vorgesehen | ✅ Cover/Hero/Logo genutzt, Icons offen |
| [TheGamesDB](https://thegamesdb.net) | Clear Logos, Banner, Fanart, Boxart, Screenshots (stark bei Retro/Emulation) | kostenloser API-Key, Nutzungsbedingungen beachten | geplant (Plugin) |
| [IGDB](https://www.igdb.com) | Cover, Artworks, Screenshots, Metadaten | Twitch-Developer-App, Bedingungen prüfen | später (Plugin) |

Alle Quellen außer Steam kommen als Plugins, damit Lizenz- und Key-Fragen beim Nutzer bleiben.

### M3: Plugin-SDK v1, Plugin-Manager, News

- **SDK v1** mit den Erweiterungspunkten aus der Tabelle oben, versioniert, dazu ein **Beispiel-Plugin** im Repo
- **Plugin-Manager in der GUI**: installierte Plugins, an/aus, Einstellungen, Updates. Installation aus einem Katalog (ein JSON-Index auf GitHub, mit SHA-256-Pflichtprüfung und Update-Hinweis) oder als ZIP.
- **Stabilität**: Plugin-Aufrufe mit Timeout und Fehlerisolation. Ein Plugin, das wiederholt abstürzt, wird deaktiviert und gemeldet.
- **RSS/News als erstes Plugin** (testet das SDK an einem echten Fall):
  - News-Karussell als Widget auf Home, eigener Reader-Screen mit großer Schrift und Bildern, per Controller blätterbar
  - Feeds in der GUI verwalten, vorkonfiguriert: [WinFuture](https://winfuture.de) (`https://static.winfuture.de/feeds/WinFuture-News-rss2.0.xml`), [Caschys Blog](https://stadt-bremerhaven.de) (`https://stadt-bremerhaven.de/feed/`)
  - Artikel öffnen im eingebetteten Leser (WebView mit Controller-Scrollen), „Gelesen“-Status
  - Technik: `System.ServiceModel.Syndication` (RSS + Atom), Cache, Abruf im Hintergrund

### M3b: KI-Assistent und MCP

*Baut auf dem Plugin-SDK auf: KI und Plugins nutzen dieselben Werkzeuge wie die Oberfläche.*

- **Anbieter**: Claude (Anthropic) und OpenAI, API-Key in den Einstellungen hinterlegen. Gespeichert wird er mit Windows DPAPI verschlüsselt, nie im Klartext in der `config.json`.
- **Assistent im Fullscreen**: eigener Screen und Quick-Action im Guide-Menü. Eingabe per Bildschirmtastatur, vorgeschlagene Prompts zum Antippen, Texteingabe vom Handy (M6), optional Spracheingabe.
- **Was die KI darf**, über eine feste Werkzeugliste (Tool-Use), dieselben typisierten Operationen wie die UI:
  - Bibliothek durchsuchen, Spiele/Apps anlegen und bearbeiten („Leg mir RetroArch mit diesem Pfad an“), Artwork suchen und setzen
  - Einstellungen und Skins ändern, Szenen anlegen, Plugins konfigurieren
  - Spieleverzeichnisse scannen, Updates prüfen, Infos zu Spielen erklären
- **Bestätigung vor jeder Änderung**: Die KI schlägt vor, CouchHUD zeigt eine Vorschau, du drückst A. Keine Shell-Befehle, kein Dateisystemzugriff außerhalb der Werkzeuge.
- **MCP-Server** für den Zugriff von außen (z.B. Claude Desktop oder ein anderer MCP-Client auf dem Laptop)
  - Standardmäßig aus; beim Einschalten Token erzeugen und als QR-Code anzeigen
  - Streamable HTTP, nur localhost oder im LAN, je nach Einstellung
  - Dieselben Werkzeuge wie der Assistent, Ressourcen für Bibliothek und Einstellungen, Schreibzugriff einzeln freischaltbar
- **Plugins können Werkzeuge beisteuern** (z.B. SignalRGB-Plugin: „Effekt setzen“), automatisch für Assistent und MCP verfügbar

### M4: CouchHud.Service und Updates

*Hier wird UAC gelöst: einmal beim Setup, danach nie wieder.*

- **CouchHud.Service** als Windows-Dienst (LocalSystem), installiert vom Setup
  - Named Pipe mit ACL nur für den interaktiven Benutzer
  - **Keine** „führe diesen Befehl aus“-Schnittstelle, sondern eine feste Liste typisierter Operationen mit geprüften Parametern (Update-IDs, Paket-IDs, Profilnamen). Sonst wäre der Dienst ein offenes Scheunentor für Rechteausweitung.
  - Plugins können privilegierte Operationen anmelden. Die tauchen in den Einstellungen auf und werden einmal vom Nutzer freigegeben.
- **Windows Update**: suchen, Liste mit Größe und Typ (Sicherheit, Treiber, optional), herunterladen und installieren mit Fortschritt, Hinweis bei nötigem Neustart („Jetzt“ oder „Beim Ausschalten“). Technik: Windows Update Agent API (`IUpdateSession`, `IUpdateSearcher`, `IUpdateInstaller`) im Dienst.
- **App-Updates** als zwei Plugins, beide in der Updates-Seite zusammengeführt:
  - **winget**: verfügbare Upgrades anzeigen, einzeln oder alle aktualisieren. Pakete mit User-Scope laufen im Benutzerkontext, Machine-Scope über den Dienst ohne UAC-Popup. Technik: winget-COM-API (`Microsoft.Management.Deployment`), Fallback auf die CLI. *Spike: winget im SYSTEM-Kontext hat Einschränkungen, das prüfen wir früh.*
  - **UniGetUI**: über dessen lokale IPC-API bzw. die `unigetui`-CLI, für alle, die UniGetUI ohnehin nutzen (deckt auch Scoop, Chocolatey, pip, npm … ab)
- **CouchHUD-Self-Update** (umgesetzt): Release-Feed auf GitHub, Prüfsummen-Datei mit Signatur (ECDSA wie beim Katalog), stille Installation per Setup mit einer UAC-Abfrage. Offen: Update im Hintergrund vorladen und beim nächsten Start einspielen
- **Treiber-Hinweise** (optional): NVIDIA/AMD-Treiberversion anzeigen, auf neue Version hinweisen

### M5: Hardware-Plugins

*FanControl und SignalRGB als eigenständige Plugins, keine Zeile davon im Kern. CaseHUD ist als eingebaute Integration umgesetzt (kein CaseHUD-Code im Kern, nur der API-Client).*

**CaseHUD** (LCDs, dein Projekt)
- ✅ Umgesetzt in 0.4.0 als eingebaute Integration (nicht als Plugin), Details in [CASEHUD.md](CASEHUD.md): Anbindung über die lokale HTTP-API, Discovery über `%APPDATA%\casehudpi.json` (Port + Token), Displays mit Live-Vorschau, Design, Helligkeit, Drehung, Modus, Szenen, Guide-Menü-Einträge, Spielmeldung an CaseHUD
- Offen: Sensor-Widgets und Performance-Overlay aus CaseHUDs LHM-Dienst
- In CouchHUD: Displays mit Live-Vorschau, Design pro Display wählen, Helligkeit, „Lüfter-Set“
- **Sensoren aus CaseHUDs LHM-Dienst** nutzen wir gleich mit: Temperatur-/Last-Widgets und ein Performance-Overlay im Quick-Menü, ohne zweiten Hardware-Treiber
- Pro Spiel ein Display-Design (deckt sich mit CaseHUDs eigener 1.2-Roadmap „Profile beim Spielstart“)

**SignalRGB** (Beleuchtung)
- Effekte auflisten und anwenden über die lokale REST-API (`http://127.0.0.1:16038/api/v1`), Fallback über das URL-Schema `signalrgb://effect/apply/<Name>` bzw. `SignalRgbLauncher.exe --url=…?-silentlaunch-` (kommt nicht in den Vordergrund, wichtig für den nahtlosen Fullscreen)
- Effekt-Galerie in der GUI, Favoriten, Effekt pro Spiel, Quick-Actions („Kino-Modus“, „Licht aus“)
- *Spike: Umfang der REST-API prüfen (Helligkeit? Effektparameter?). Die URL-Schnittstelle ist laut Doku noch „early implementation“.*

**FanControl** (Lüfter)
- FanControl ist closed source und läuft als Admin. Die realistischen Wege:
  1. **Konfigurationen wechseln**: Je Szenario eine FanControl-Config („Leise“, „Gaming“, „Film“), Umschalten per Kommandozeile (`-c`/`--config` mit vollem Pfad, laut Release Notes ab V262). Weil FanControl als Admin läuft, geht das über den Dienst.
  2. **Brücken-Plugin auf FanControl-Seite**: FanControl hat ein eigenes Plugin-System (C#). Ein kleines „CouchHUD Bridge“-Plugin *innerhalb* von FanControl könnte Werte bereitstellen und Befehle per Pipe entgegennehmen. *Spike: prüfen, was die FanControl-Plugin-API von außen erlaubt.*
- In CouchHUD: aktuelles Profil, Profil wechseln, Lüfter-RPM und Temperaturen (über CaseHUD/LHM), Profil pro Spiel

**Szenen**: Erst wenn alle drei da sind, lohnt das richtig. Eine Szene bündelt Lüfterprofil, RGB-Effekt, LCD-Design und Audio-Ausgang und wird per Quick-Action oder automatisch bei `GameStarted` aktiviert. Beispiele: „Gaming“, „Film“ (Licht gedimmt, Lüfter leise), „Nacht“.

### M6: Medien, Smart Home, mehr Quellen

- **Media Center**: Kodi (JSON-RPC: Bibliothek, Weiterschauen, Start), Jellyfin/Plex (Bibliothek, Weiterschauen als Home-Reihe)
- **Streaming-Apps** als schicke Einträge: Netflix, Disney+, YouTube TV (Browser im Kiosk-Modus mit Controller-Steuerung)
- **Weitere Spiele-Quellen**: GOG Galaxy, Xbox/Game Pass, EA App, Ubisoft Connect, Battle.net, Emulatoren (RetroArch-Playlists)
- **Home Assistant**: Szenen und Lichter aus dem Quick-Menü, Events in beide Richtungen (z.B. Spielstart dimmt das Wohnzimmer)
- **Handy als Fernbedienung**: Die UI ist ohnehin Web. Eine optionale, per PIN gekoppelte LAN-Seite für Texteingabe vom Handy, Bibliothek durchsuchen, Spiel starten.
- **Game-Streaming-Status**: Sunshine/Moonlight, wer ist verbunden
- Wetter, Kalender, „Jetzt läuft“-Karte

### M7: Release

- **Setup** *(erledigt: Inno Setup, WebView2-Prüfung, Deinstallation stellt Windows-Eingriffe zurück; der Dienst kommt mit CouchHud.Service)*: CouchHUD, Dienst, .NET-Runtime-Check, WebView2-Check, optional „als Shell einrichten“
- **Ersteinrichtung per Controller**: Sprache, Skin, Bibliotheken scannen, Audio-Ausgang, Plugins wählen, **Auto-Login** einrichten *(erledigt: Passwort einmal eingeben, mit LogonUser geprüft, von Windows als LSA-Secret abgelegt; CouchHUD speichert es nicht)*, Shell aktivieren
- **Notfall-Modus** *(erledigt)*: Strg+Umschalt oder LB+RB+View beim Start (etwa 1,5 s gehalten), oder drei unsaubere Beendigungen in Folge: Windows-Sperre, danach normaler Desktop ohne CouchHUD-Shell.
- Lokalisierung DE/EN *(erledigt)*, Doku, Beispiel-Plugins und Skin-Vorlage
- Signierte Builds (SmartScreen, offen: Release-Prüfsummen sind signiert, das Setup noch nicht per Authenticode), Issue-Vorlagen, Lizenz, **Open Source**

---

## Offene Spikes (früh klären)

| Thema | Frage | Wann |
|---|---|---|
| winget im Dienst | Läuft die COM-API zuverlässig als SYSTEM? Sonst geplanter Task im Benutzerkontext mit höchsten Rechten. | vor M4 |
| FanControl | Config-Wechsel per CLI im laufenden Betrieb? Was kann ein FanControl-Plugin? | vor M5 |
| SignalRGB | Umfang und Stabilität der REST-API auf Port 16038 | vor M5 |
| Audio | `IPolicyConfig` ist undokumentiert, Verhalten auf aktuellem Windows 11 prüfen | M2 |
| Fokus | Zuverlässiges Zurückholen in den Vordergrund bei Exclusive-Fullscreen-Spielen | M2 |
| Autostart doppelt | Startet der Explorer im PC-Modus die Run-Einträge ein zweites Mal? Falls ja, Explorers „StartupHasBeenRun“-Markierung setzen | M2 |
| Setup erhöht starten | Dienst startet Setup mit dem erhöhten Token des Nutzers in dessen Sitzung, ohne UAC-Dialog | M2b/M4 |
