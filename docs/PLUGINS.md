# Plugins für CouchHUD

Plugins erweitern CouchHUD um neue Spiele-Quellen, Seiten, Einstellungen, Einträge im Guide-Menü und Werkzeuge für den KI-Assistenten. Sie sind normale .NET-Klassenbibliotheken gegen `CouchHud.Sdk`.

Das Beispiel im Repository ist **[plugins/CouchHud.Plugin.Wand](../plugins/CouchHud.Plugin.Wand/WandPlugin.cs)**: eine Datei, die Einstellungen, eine Seite, eine Quick-Action, ein Tool und Events nutzt.

## In fünf Minuten zum eigenen Plugin

```powershell
dotnet new classlib -n CouchHud.Plugin.Hallo -f net10.0
cd CouchHud.Plugin.Hallo
# Referenz auf das SDK (aus dem CouchHUD-Repo oder der CouchHud.Sdk.dll neben der CouchHud.exe)
dotnet add reference ..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj
```

In der `.csproj` das SDK nicht mitkopieren, CouchHUD bringt es selbst mit:

```xml
<ProjectReference Include="..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
<PropertyGroup><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>
```

```csharp
using CouchHud.Sdk;

public sealed class HalloPlugin : IPlugin
{
    public string Id => "hallo";

    public void Initialize(IPluginContext context)
    {
        context.RegisterQuickAction(new QuickAction
        {
            Id = "wave",
            Icon = "👋",
            Label = "Hallo sagen",
            Run = _ => Task.FromResult<string?>("Hallo vom Plugin!")
        });

        context.Events.GameStarted += game => context.Notify("Viel Spaß", game.Name);
    }
}
```

Bauen und nach `%APPDATA%\CouchHud\plugins\CouchHud.Plugin.Hallo\` kopieren (Ordnername = Name der DLL). CouchHUD neu starten, fertig. Alternativ den Ordner als `.zip` packen und in CouchHUD unter *Einstellungen › Expertenmodus › Plugins & Skins › Plugin installieren* auswählen.

## Was ein Plugin kann (SDK v2)

| Methode | Wofür |
|---|---|
| `RegisterLibraryProvider(ILibraryProvider)` | Spiele oder Apps für die Bibliothek (Kodi, GOG, Emulatoren …) |
| `RegisterSettings(params PluginSetting[])` | Einstellungen (Toggle, Text, Geheim, Auswahl). CouchHUD zeigt und speichert sie, lesen über `context.Settings` |
| `RegisterPage(PluginPage)` | Eine Listen-Seite mit Zeilen (`PluginRow`), A auf einer Zeile ruft `Activate` auf |
| `RegisterQuickAction(QuickAction)` | Eintrag im Guide-Menü, z.B. „Licht aus“ |
| `RegisterTool(PluginTool)` | Werkzeug für den KI-Assistenten und MCP-Clients. Der Name wird zu `plugin_<plugin>_<werkzeug>`; ein Name, den es schon gibt, wird abgelehnt. Mit `Writes = true` bestätigt der Nutzer vorher am TV (Assistent); über MCP wird **jedes** Plugin-Werkzeug bestätigt, die Frage zeigt die Argumente. Bei gesperrter Couch laufen Plugin-Werkzeuge nicht |
| `Notify(title, body)` | Hinweis auf dem TV und in der Benachrichtigungsliste |
| `OpenUrl(url)` | Öffnet eine http(s)-Adresse im eingebauten CouchHUD-Browser (nie im Windows-Standardbrowser), nur bei entsperrter Couch; `false`, wenn abgelehnt |
| `Events` | `GameStarted`, `GameEnded` (mit Spielzeit), `PcModeEntered`, `CouchModeEntered`, `LanguageChanged`, `LockChanged` (SDK 2) |
| `Locked` | **SDK 2:** `true`, solange die Couch gesperrt ist (ein Gast oder Kind am Controller). Dann nichts auf eigene Faust starten oder umschalten, z.B. keinen Autostart bei `GameStarted`. Ältere CouchHUD-Versionen antworten `false` |
| `Language` | Die Sprache von CouchHUD jetzt: `"de"` oder `"en"` |
| `Settings` | Gespeicherte Werte, `Get`, `GetBool`, `Set` |
| `DataDirectory` | Eigener, beschreibbarer Ordner für Cache und Dateien |

**Deklarativ statt HTML**: Plugins beschreiben Seiten als Daten. CouchHUD rendert sie im aktiven Skin, mit Controller, Maus und Tastatur. Dein Plugin sieht in jedem Skin richtig aus, ohne eine Zeile CSS.

## plugin.json

Optional, aber empfohlen, liegt neben der DLL:

```json
{
  "name": "Hallo",
  "version": "1.0.0",
  "author": "dein-name",
  "description": "Sagt Hallo",
  "homepage": "https://github.com/dein-name/couchhud-hallo",
  "sdk": 1
}
```

`description` ist ein Text oder pro Sprache `{ "de": "Sagt Hallo", "en": "Says hello" }`. CouchHUD zeigt die Beschreibung in der eingestellten Sprache und fällt auf die andere zurück.

`sdk` ist die SDK-Version, die das Plugin braucht. Ältere CouchHUD-Versionen laden es dann gar nicht erst, statt abzustürzen. SDK 2 ist abwärtskompatibel: Plugins für SDK 1 laufen unverändert weiter, `Locked` und `LockChanged` sind nur Zusätze (mit Standardimplementierung, ein Plugin, das sie nutzt, läuft auch auf einem älteren CouchHUD, dort ist `Locked` immer `false`). Wer sie braucht, schreibt `"sdk": 2` nur, wenn das Plugin ohne sie nicht sicher ist.

## Zwei Sprachen

CouchHUD gibt es auf Deutsch und Englisch, und die Sprache lässt sich zur Laufzeit umstellen. Dein Plugin liest sie mit `context.Language` (`"de"` oder `"en"`; ältere CouchHUD-Versionen melden immer `"de"`, das Plugin läuft also weiter).

- Texte, die du **auf Abruf** baust (Zeilen einer Seite, `Detail` einer Quick-Action, Rückgabe von `Activate`), fragst du bei jedem Aufruf neu: `context.Language == "en" ? "Open" : "Öffnen"`.
- Texte, die du **einmal registrierst** (`PluginSetting.Label`/`Description`, `PluginPage.Title`/`Description`, `QuickAction.Label`), gelten für die Sprache zum Zeitpunkt der Registrierung. Registriere sie bei `Events.LanguageChanged` noch einmal: eine Registrierung mit derselben Id (`Key`, `Id`) ersetzt die frühere.
- Die Beschreibung der `PluginTool`s bleibt Englisch, das verstehen die Modelle am besten.
- `description` in `plugin.json` und im Katalog gibt es pro Sprache (siehe oben und [Format](#format)).

```csharp
public void Initialize(IPluginContext context)
{
    _context = context;
    Register();
    context.Events.LanguageChanged += _ => Register();
}

private string T(string de, string en) => _context.Language == "en" ? en : de;

private void Register() => _context.RegisterQuickAction(new QuickAction
{
    Id = "open",
    Label = T("Öffnen", "Open"),
    Run = _ => Task.FromResult<string?>(null)
});
```

Das Wand-Plugin (`plugins/CouchHud.Plugin.Wand`) macht genau das.

## Regeln, damit CouchHUD stabil bleibt

- Jeder Aufruf in ein Plugin (Seiten, Aktionen, Tools) hat ein Timeout von 15 Sekunden. Langsame Arbeit gehört in `Task.Run` oder einen Hintergrund-Task.
- `QuickAction.Detail` (die zweite Zeile im Guide-Menü) wird auf einem Hintergrund-Thread mit 2 Sekunden Timeout abgefragt, nie auf dem UI-Thread. Antwortet es nicht, hat der Eintrag keine zweite Zeile; solange ein Aufruf hängt, wird kein neuer gestartet. Der Takt von Sperre und Jugendschutz hängt davon nicht ab.
- Exceptions werden abgefangen und als Meldung angezeigt. Ein kaputtes Plugin legt nie die Shell lahm.
- Event-Handler laufen im Hintergrund, nie auf dem UI-Thread.
- Jedes Plugin hat einen eigenen Load-Context: eigene NuGet-Abhängigkeiten in eigenen Versionen sind erlaubt.
- Plugins laufen mit den Rechten des Benutzers. Bitte nichts tun, was der Nutzer nicht erwartet, und Secrets nur über `SettingKind.Secret` speichern.
- **Freigabe:** CouchHUD merkt sich beim Installieren (Katalog, `.zip`) einen Hash der Dateien jedes Plugins. Ein Ordner in `%APPDATA%\CouchHud\plugins`, den niemand über CouchHUD installiert hat, oder dessen Dateien sich geändert haben (auch von Hand kopiert!), lädt erst nach einer Freigabe (*Plugins & Skins* › Plugin › *Plugin freigeben*, mit dem Code der Dateien, nur entsperrt). Wer sein Plugin beim Entwickeln direkt in den Ordner baut, gibt es nach jeder Änderung neu frei, oder installiert es als `.zip`. Plugins, die schon vor dieser Funktion dort lagen, gelten beim ersten Start als freigegeben. Ein vorgemerktes Katalog-Update wird vor dem Einspielen erneut gegen die Prüfsumme geprüft. In den Hash gehen alle Dateien des Ordners ein, außer `desktop.ini`, `Thumbs.db` und `*.tmp` (die legen Windows und Programme von allein an). Die Liste der freigegebenen Plugins (`plugin-trust.json`) trägt eine Signatur (HMAC, derselbe Schlüssel wie bei `playtime.json`, mit DPAPI an das Windows-Konto gebunden). **Wogegen das schützt:** gegen ein Programm oder Skript, das nur Dateien schreibt, einen fremden Ordner ablegt und die Liste von Hand ergänzt oder ersetzt, und gegen eine hineinkopierte Liste. **Wogegen nicht:** ein Programm, das als dasselbe Konto läuft, kann den Schlüssel auch lesen; die Freigabe ersetzt keinen Virenschutz. Passt die Signatur nicht oder ist die Liste unlesbar, meldet CouchHUD einen Fehler an den Plugins, lädt nichts Unbekanntes und überschreibt die Datei nicht; zum Neubeginnen `plugin-trust.json` löschen. Danach gilt, was dann im Plugin-Ordner liegt, beim nächsten Start als freigegeben (wie beim allerersten Start): vorher prüfen, dass dort nur Plugins liegen, die du kennst.

## Plugin teilen

Repository auf GitHub mit dem Topic `couchhud-plugin` anlegen und ein Release mit dem `.zip` (Ordner mit DLL, Abhängigkeiten und `plugin.json`) hochladen. So finden andere es. Wer will, dass es direkt in CouchHUD erscheint, trägt es im [Katalog](#katalog) ein.

## Katalog

CouchHUD kann Plugins und Skins aus einem **Katalog** installieren und aktualisieren: *Einstellungen › Darstellung › Neue Skins installieren* (oder im Expertenmodus *Plugins & Skins*, dort *Katalog*) (mit LB/RB zwischen *Installiert* und *Katalog* wechseln). Ein Katalog ist eine einzelne `index.json` irgendwo im Netz. Beispiel: [catalog/index.json](../catalog/index.json).

Der Standard-Katalog liegt unter `https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json`. Eigene Quellen stehen in `%APPDATA%\CouchHud\config.json`:

```json
"CatalogSources": [
  "https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json",
  "https://example.com/mein-katalog/index.json"
],
"CatalogUpdateCheck": true
```

Mehrere Quellen werden zusammengeführt, bei gleicher `id` gewinnt die erste.

### Format

```json
{
  "version": 1,
  "entries": [
    {
      "id": "wand",
      "folder": "CouchHud.Plugin.Wand",
      "name": "Wand",
      "type": "plugin",
      "version": "1.0.0",
      "minCouchHud": "0.3.0",
      "author": "kewe87",
      "description": { "de": "Cheats und Mods …", "en": "Cheats and mods …" },
      "homepage": "https://github.com/…",
      "icon": "https://…/wand.png",
      "download": "https://…/CouchHud.Plugin.Wand-1.0.0.zip",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }
  ]
}
```

| Feld | Pflicht | Bedeutung |
|---|---|---|
| `id` | ja | Eindeutige Kennung, `A-Z a-z 0-9 . _ -`, bis 64 Zeichen |
| `name` | ja | Anzeigename |
| `type` | ja | `plugin` oder `skin` |
| `version` | ja | SemVer (`1.2.3`, `1.3.0-beta.1`). Der Katalog zeigt „Update“, sobald sie höher ist als die installierte |
| `download` | ja | **https**-Adresse eines `.zip` (Aufbau wie bei der manuellen Installation, siehe oben und [SKINS.md](SKINS.md)) |
| `sha256` | ja | SHA-256 des `.zip`, 64 Hex-Zeichen. Ohne passende Prüfsumme wird nichts installiert |
| `description` | nein | Text oder `{ "de": …, "en": … }` |
| `author`, `homepage`, `icon` | nein | `homepage` und `icon` nur als https |
| `minCouchHud` | nein | Kleinste CouchHUD-Version. Ist die laufende kleiner, bleibt der Eintrag sichtbar, ist aber nicht installierbar |
| `folder` | nein | Ordnername des Pakets, Standard ist die `id`. Bei Plugins der Name der DLL und des Ordners (`CouchHud.Plugin.Wand`) |

Einträge ohne `id`, `name`, gültigen `type` oder gültige `version` werden übersprungen. Einträge ohne gültige Prüfsumme oder mit einer Adresse ohne https werden angezeigt, lassen sich aber nicht installieren. Ein kaputter Index (kein JSON) ist ein Fehler der Quelle; CouchHUD zeigt dann den zuletzt gespeicherten Stand.

Das `.zip` muss genau einen Ordner namens `folder` enthalten (oder die Dateien direkt, dann wird `folder` als Name genommen). Die Hilfe `scripts\catalog-entry.ps1 -Folder <Ordner> -Version 1.0.0` packt den Ordner richtig und gibt die Prüfsumme für den Eintrag aus.

### Vertrauen: Prüfsumme und Signatur

Zwei Schutzschichten mit verschiedenen Aufgaben:

- Die **Prüfsumme** (`sha256` im Eintrag) schützt gegen beschädigte oder ausgetauschte **Downloads**: Das `.zip` muss genau dem Hash im Index entsprechen.
- Die **Signatur** schützt gegen einen manipulierten **Index**. Wer nur `index.json` ändern kann (z.B. mit einem gekaperten GitHub-Konto), könnte sonst Download-URL und Hash zusammen austauschen. Der Standard-Katalog liefert deshalb neben `index.json` eine `index.json.sig`: Base64 einer ECDSA-P-256-Signatur (SHA-256, IEEE P1363) über die **exakten Bytes** der `index.json`. Den öffentlichen Schlüssel bringt CouchHUD mit (`CatalogSignature.PublicKeyPem`). Ohne gültige Signatur ist die Standardquelle „nicht vertrauenswürdig“, und nichts daraus ist installierbar. Bei einer kaputten Signatur zeigt CouchHUD den zuletzt geprüften gespeicherten Stand.
- **Eigene Quellen** dürfen unsigniert sein. Sie sind mit „⚠ unsignierte Quelle“ markiert, und der Installationsdialog weist darauf hin. Eine Signatur mit dem Schlüssel des Projekts ist dort nicht nötig.

Grenzen: Die Signatur beweist, dass der Index vom Schlüsselinhaber stammt, nicht dass das Plugin harmlos ist. Wer den privaten Schlüssel hat, kann alles freigeben. Er liegt nur im Benutzerprofil des Maintainers (DPAPI-geschützt, `%USERPROFILE%\.couchhud\catalog-signing.key`), nie im Repository. Zusätzlich empfohlen: Branch-Protection auf `main` im Katalog-Repo (Pull Requests mit Review, keine Force-Pushes).

Index signieren (PowerShell 7), nachdem er endgültig ist, ohne spätere Änderung von Bytes oder Zeilenenden (im Katalog-Repo `catalog/* -text` in `.gitattributes`):

```powershell
.\scripts\catalog-sign.ps1 -Index .\catalog\index.json   # erzeugt index.json.sig; legt den Schlüssel beim ersten Mal an (die Umsetzung steckt in sign-file.ps1, das auch die Release-Prüfsummen signiert). Signiert werden `couchhud-catalog-v1` + Zeilenumbruch + die Bytes der Datei; dieselbe Signatur gilt weder für Release-Prüfsummen noch umgekehrt (Domain-Trennung). Signaturen im alten Format (nur die Bytes) werden nicht mehr akzeptiert: ältere CouchHUD-Versionen lehnen den neu signierten Index ab
```

### Ablauf und Sicherheit

- **Laden**: 10 Sekunden Zeitlimit, höchstens 1 MB. Der letzte erfolgreiche Index jeder Quelle liegt in `%APPDATA%\CouchHud\catalog-cache.json`; ohne Netz zeigt der Katalog diesen Stand („Offline“).
- **Installieren** (A auf einem Eintrag): erst ein Hinweis, dass **Plugins mit deinen Rechten laufen** und nur aus vertrauenswürdigen Quellen kommen sollen, bestätigt mit A. Dann Download (höchstens 200 MB, Abbruch, wenn 60 Sekunden lang keine Daten kommen), **SHA-256-Pflichtprüfung**, danach der gewöhnliche Installer. Der Installer prüft alle Pfade im Archiv (kein `../`, keine absoluten Pfade oder Laufwerke), Größe und Dateianzahl und packt erst in einen Temp-Ordner neben dem Ziel. Die alte Version wird nur beiseite geschoben und kommt bei einem Fehler zurück. Ein Archiv, das weniger Größe angibt, als es enthält, wird abgelehnt.
- **Offline installieren**: Ist die Quelle nicht erreichbar, zeigt der Katalog den gespeicherten Stand. Installieren ist trotzdem möglich, sobald der Download selbst erreichbar ist. Die Signatur des gespeicherten Standes wird beim Laden erneut geprüft.
- **Nur https**. Katalog, Downloads und Bilder werden über http, UNC-Pfade (`file://server/…`) oder lokale Pfade abgelehnt (außer mit dem Entwickler-Schalter, dann nur lokale Laufwerke und Loopback).
- **Quellen**: Updates kommen nur aus der Quelle, aus der ein Paket installiert wurde. Mitgelieferte und von Hand installierte Plugins sind nur aus der Standardquelle aktualisierbar. Gleiche Ordner in mehreren Quellen: die erste Quelle gewinnt. Dieselbe `id` mit anderem Ordner in einer späteren Quelle ist ein Konflikt und nicht installierbar.
- **Mitgelieferte Plugins**: Eine Kopie in `%APPDATA%\CouchHud\plugins` ersetzt das mitgelieferte Plugin nur, wenn sie eine DLL enthält und ihre Version in `plugin.json` nicht älter ist. Sonst lädt das mitgelieferte, und das Log sagt warum.
- **Neustart**: Plugin-DLLs lassen sich bei laufendem CouchHUD weder entladen noch ersetzen. Neue Plugins werden sofort entpackt und beim nächsten Start geladen. Ein **Update** wird abgelegt (`%APPDATA%\CouchHud\pending`) und beim nächsten Start eingespielt, bevor Plugins geladen werden. Skins sind sofort nutzbar. Ein Update eines mitgelieferten Plugins (z.B. Wand) legt eine Kopie in `%APPDATA%\CouchHud\plugins` an, die dann die mitgelieferte ersetzt.
- **Update-Hinweis**: Fünf Minuten nach dem Start und dann alle 12 Stunden prüft CouchHUD im Hintergrund die Kataloge, nie während ein Spiel läuft. Neue Updates erscheinen einmalig in den Benachrichtigungen und als Abschnitt *Plugins & Skins* auf der Updates-Seite. Abschalten: `"CatalogUpdateCheck": false`.
- Installiert wurde was? `%APPDATA%\CouchHud\catalog-installed.json` merkt sich Version und Ordner (Skins haben keine eigene Versionsangabe).

### Katalog testen (Entwickler-Schalter)

Mit `"CatalogAllowLocal": true` in der `config.json` akzeptiert CouchHUD zusätzlich `file:///…`-Quellen und `http://127.0.0.1`/`localhost` für Katalog und Downloads. Die Prüfsumme bleibt Pflicht. Nur zum Testen einschalten, Standard ist `false`:

```json
"CatalogAllowLocal": true,
"CatalogSources": [ "file:///C:/Temp/katalog/index.json" ]
```

### Eigenen Katalog betreiben

Ein GitHub-Repository mit einer `index.json` reicht (`raw.githubusercontent.com`). Die `.zip`-Dateien hängen als Release-Assets daran. Pull Requests für den Standard-Katalog sind willkommen: Eintrag in `index.json`, Quellcode des Plugins öffentlich verlinken.
