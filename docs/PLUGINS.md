# Plugins für Mega Hub

Plugins erweitern Mega Hub um neue Spiele-Quellen, Seiten, Einstellungen, Einträge im Guide-Menü und Werkzeuge für den KI-Assistenten. Sie sind normale .NET-Klassenbibliotheken gegen `CouchHud.Sdk`.

Das Beispiel im Repository ist **[plugins/CouchHud.Plugin.Wand](../plugins/CouchHud.Plugin.Wand/WandPlugin.cs)**: eine Datei, die Einstellungen, eine Seite, eine Quick-Action, ein Tool und Events nutzt. Es ist **nur ein SDK-Beispiel** und steht nicht mehr im Katalog: Wand selbst ist seit 0.7 eine normale App, die als Begleit-App mit dem Spiel startet (Einstellungen › Bibliothek › Begleit-Apps). Wer so etwas für ein anderes Programm braucht, nimmt dafür keine Plugin-Lösung mehr, sondern eine Begleit-App.

## In fünf Minuten zum eigenen Plugin

```powershell
dotnet new classlib -n CouchHud.Plugin.Hallo -f net10.0
cd CouchHud.Plugin.Hallo
# Referenz auf das SDK (aus dem Mega-Hub-Repo oder der CouchHud.Sdk.dll neben der CouchHud.exe)
dotnet add reference ..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj
```

In der `.csproj` das SDK nicht mitkopieren, Mega Hub bringt es selbst mit:

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

Bauen und nach `%APPDATA%\CouchHud\plugins\CouchHud.Plugin.Hallo\` kopieren (Ordnername = Name der DLL). Mega Hub neu starten, fertig. Alternativ den Ordner als `.zip` packen und in Mega Hub unter *Einstellungen › Expertenmodus › Plugins & Skins › Plugin installieren* auswählen.

## Was ein Plugin kann (SDK v2)

| Methode | Wofür |
|---|---|
| `RegisterLibraryProvider(ILibraryProvider)` | Spiele oder Apps für die Bibliothek (Kodi, GOG, Emulatoren …) |
| `RegisterSettings(params PluginSetting[])` | Einstellungen (Toggle, Text, Geheim, Auswahl, ab SDK 4 auch ein Eintrag, der eine eigene Seite öffnet). Mega Hub zeigt und speichert sie, lesen über `context.Settings` |
| `RegisterPage(PluginPage)` | Eine Listen-Seite mit Zeilen (`PluginRow`), A auf einer Zeile ruft `Activate` auf |
| `RegisterQuickAction(QuickAction)` | Eintrag im Guide-Menü, z.B. „Licht aus“ |
| `RegisterTool(PluginTool)` | Werkzeug für den KI-Assistenten und MCP-Clients. Der Name wird zu `plugin_<plugin>_<werkzeug>`; ein Name, den es schon gibt, wird abgelehnt. Mit `Writes = true` bestätigt der Nutzer vorher am TV (Assistent); über MCP wird **jedes** Plugin-Werkzeug bestätigt, die Frage zeigt die Argumente. Bei gesperrter Couch laufen Plugin-Werkzeuge nicht |
| `Notify(title, body)` | Hinweis auf dem TV und in der Benachrichtigungsliste |
| `OpenUrl(url)` | Öffnet eine http(s)-Adresse im eingebauten Mega-Hub-Browser (nie im Windows-Standardbrowser), nur bei entsperrter Couch; `false`, wenn abgelehnt |
| `Events` | `GameStarted`, `GameEnded` (mit Spielzeit), `PcModeEntered`, `CouchModeEntered`, `LanguageChanged`, `LockChanged` (SDK 2) |
| `Locked` | **SDK 2:** `true`, solange die Couch gesperrt ist (ein Gast oder Kind am Controller). Dann nichts auf eigene Faust starten oder umschalten, z.B. keinen Autostart bei `GameStarted`. Ältere Mega-Hub-Versionen antworten `false` |
| `Language` | Die Sprache von Mega Hub jetzt: `"de"` oder `"en"` |
| `Settings` | Gespeicherte Werte, `Get`, `GetBool`, `Set` |
| `DataDirectory` | Eigener, beschreibbarer Ordner für Cache und Dateien |

### Zusätze im SDK 3 für Zugangsdaten, Menüeinträge und Web-Anmeldungen

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `Settings.SetSecret(key, value)` | keine | Das Gleiche wie `Set`, aber der Host speichert den Wert verschlüsselt (Windows-Konto, an die Plugin-Kennung gebunden); `Get` liefert ihn nur diesem Plugin. Einstellungen der Art `Secret` werden ebenso verschlüsselt. Ohne Verschlüsselung im Host wird nichts gespeichert |
| `RemoveQuickAction(id)` | `quickActions` | Nimmt einen Eintrag im Guide-Menü wieder heraus |
| `QuickAction.AllowedWhileLocked`, `PluginTool.AllowedWhileLocked` | `quickActions`, `assistant` | Der Eintrag im Guide-Menü beziehungsweise das Werkzeug darf auf der gesperrten Couch laufen. Zählt nur für CouchHUDs eigene Plugins; bei jedem anderen wird der Eintrag gesperrt nicht gezeigt und ein Aufruf abgelehnt |
| `SignIn.Open(seite, host, empfänger)` | `browser.signin` (nur CouchHUDs eigene Plugins) | Öffnet eine Anmeldeseite im Couch-Browser. Der Browser bricht jede Navigation zu `host` (muss auf `.invalid` enden) vor jeder Anfrage ab und gibt die Adresse einmal an `empfänger`; nur entsperrt, danach schließt der Browser |

**Wichtig zu Geheimnissen:** Plugins laufen im Prozess von Mega Hub und als derselbe Windows-Benutzer. Die Verschlüsselung durch `SetSecret` (DPAPI mit der Plugin-Kennung als Zusatz) trennt die Dateien der Plugins, ist aber **keine Schranke gegen ein bösartiges Plugin**, das selbst DPAPI aufruft oder fremde Dateien liest. Darum lehnt der Lint `ProtectedData` für Fremdentwickler ab. Dateizugriffe außerhalb des eigenen Ordners erkennt der Lint nicht zuverlässig (offener Punkt).

Ein Beispiel für alles zusammen ist das Home-Assistant-Plugin (eigenes Repository `CouchHud.Plugin.HomeAssistant`): Adresse und Token als Einstellungen, die Anmeldung als Seite, die Geräte als Kacheln.

### Dienste des Hosts für CouchHUDs eigene Plugins

Drei Fähigkeiten gibt es nur für CouchHUDs eigene Plugins (ein Fremdentwickler kann sie nicht einmal angeben); jeweils nennt das Plugin ein Programm oder eine Datei, der Host prüft sie noch einmal selbst:

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `RunSetupAsync(pfad, sha256)` | `setup.run` | Übergibt ein heruntergeladenes Setup an Windows (die Administrator-Abfrage fragt immer). Der Host verlangt die entsperrte Couch, kein Kinderprofil und einen Druck auf eine Zeile oder einen Guide-Eintrag dieses Plugins mit `RequiresParentGrant` (Einmal-Zeichen, höchstens 35 Minuten), erlaubt ein Setup zur Zeit, nimmt nur eine .exe im eigenen Datenordner dieses Plugins (allein in ihrem Ordner), rechnet den SHA-256 an der gehaltenen Datei noch einmal und prüft ihren echten Pfad. Gestartet wird über die Windows-Administrator-Abfrage. Liefert false bei Ablehnung |
| `RestrictWhileLocked(exe)` | `lock.restrict` | Fenster dieses Programms (echter Pfad, unterhalb von Program Files) werden auf der gesperrten Couch minimiert. Nur eine Beschränkung, nie eine Erlaubnis |
| `AdaptStartupArgs(exe, funktion)` | `startup.args` | Wenn Mega Hub als Shell die Autostart-Einträge ausführt, laufen die Argumente des Eintrags dieses Programms durch `funktion` (null lässt sie, wie sie sind; eine Sekunde, ohne Steuerzeichen, höchstens 1024 Zeichen). Das Programm bleibt das im Eintrag |

Dazu kommen die Fähigkeiten `process`, `registry`, `native`, `network:loopback`, `network:lan` und `network:internet`, die der Code-Lint beim Plugin findet und die es angeben muss. `network:loopback` heißt nur dieser PC, `network:lan` nur das Heimnetz (ein Fernseher, eine Zentrale), `network:internet` das Internet, auch zusammen mit dem Heimnetz. Ein Plugin, das nur im Heimnetz arbeitet, gibt `network:lan` an; braucht es beides, darf es beide angeben. **Benannte Pipes** (`System.IO.Pipes`) zählt der Lint ab 0.7 als Netzwerkcode, weil ein Pipe-Name auch einen anderen PC nennen kann (über SMB): Ein Plugin, das über eine Pipe mit einem Programm auf diesem PC spricht, gibt `network:loopback` an. Im Offline-Modus (Einstellungen › Datenschutz) pausiert Mega Hub jedes Plugin mit `network:internet`, auch wenn es zusätzlich `network:lan` angibt: Mega Hub fragt es nicht mehr nach Spielen, Seiten, Einträgen und Werkzeugen, schickt ihm keine Ereignisse und beantwortet seine Aufrufe nicht, und beim nächsten Start lädt es gar nicht. Technisch abgeschottet wird ein Plugin nicht (es läuft im Prozess von Mega Hub); `network:lan` ist die Angabe des Autors und wird vom Code-Lint nur auf Netzwerkcode geprüft, nicht auf das Ziel.

Das VPN-Plugin (eigenes Repository `CouchHud.Plugin.Vpn`, Kennung `vpn`) ist das kleinste Beispiel für ein Programm, das ein Plugin nur bedient: eine Seite mit Status, Schalter und Auswahl, ein Eintrag im Guide-Menü, nur die Rechte `pages`, `quickActions`, `process`, `registry` und `native`. Gesperrte Couch: nur das Ansehen ist mit `AllowedWhileLocked` markiert, jede Änderung hat `RequiresParentGrant` (Eltern-Code unter Jugendschutz) und ist nicht markiert; das Plugin prüft `Locked` vor jeder Änderung noch einmal selbst. Das Plugin hat keinen Reiter. Das VPN im Programm selbst gibt es nicht mehr; die zwei Einstellungen von 0.5 (*VPN im Guide-Menü*, zuletzt gewähltes Land) werden einmal übernommen (`PluginSettingSeeds`).

Das CaseHUD-Plugin (eigenes Repository `CouchHud.Plugin.CaseHud`, `docs/CASEHUD.md`) nutzt alle drei: Seite mit Kacheln und Vorschaubildern, Stufenreglern und Auswahlen, Einträge im Guide-Menü für den gesperrten Couch-Modus (`AllowedWhileLocked`), die Beschränkung seines Fensters auf der gesperrten Couch und die erste Installation. Seine Einstellung aus 0.5 übernimmt es einmal (`PluginSettingSeeds`, nur für CouchHUDs eigenes Plugin der Kennung `casehud`); eine Anbindung im Programm selbst gibt es nicht mehr.

### Sensortafel: Lüfter und Temperaturen (ab 0.7)

Plugins sprechen nie direkt miteinander. Werte, die mehrere Plugins brauchen, legt eines auf die Sensortafel des Hosts, die anderen lesen sie dort:

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `PublishSensors(werte)` | `sensors.publish` (nur CouchHUDs eigene Plugins) | Ersetzt die Werte dieses Plugins (null oder leer nimmt sie herunter). Höchstens 32, Lüfter in U/min (0 bis 20000), Temperaturen in °C (−40 bis 150); alles andere, NaN eingeschlossen, verwirft der Host. Namen werden wie Titel gesäubert (höchstens 40 Zeichen), die Quelle setzt der Host selbst (Name des Plugins) |
| `ReadSensors()` | `sensors.read` | Die neuesten Werte, die nicht älter als 30 Sekunden sind, sonst null. Nur Zahlen, kein Zugriff auf das andere Plugin, auf CaseHUD oder die Hardware |

Heute schreibt das CaseHUD-Plugin die Werte (aus CaseHUD und dessen LibreHardwareMonitor-Dienst, über CaseHUDs lokale API), das FanControl-Plugin zeigt sie auf seiner Seite. Ein pausiertes Plugin schreibt nichts (seine Werte werden entfernt) und liest null.

**Deklarativ statt HTML**: Plugins beschreiben Seiten als Daten. Mega Hub rendert sie im aktiven Skin, mit Controller, Maus und Tastatur. Dein Plugin sieht in jedem Skin richtig aus, ohne eine Zeile CSS.

## Eine Einstellung, die eine Seite des Plugins öffnet (SDK 4)

Neben Schaltern, Texten und Auswahlen kann die Einstellungsseite eines Plugins Einträge haben, die **eine eigene Seite des Plugins öffnen**: `new PluginSetting { Key = "open.devices", Label = "Geräte auswählen", Kind = SettingKind.Page, Page = "devices" }`. `Page` ist die Kennung einer Seite, die das Plugin mit `RegisterPage` angemeldet hat; der Eintrag hat keinen Wert. B auf der Seite führt zurück auf die Einstellungsseite. Das Plugin schreibt `"sdk": 4` in die `plugin.json`. Der Host übergeht den Eintrag (mit einer Protokollzeile), wenn die Seite nicht von diesem Plugin angemeldet wurde, mit `$` beginnt oder die Bezeichnung nach dem Bereinigen leer ist oder ein Name der Kernfunktionen wäre. Es gilt die Sperre der Einstellung (`PluginSetting.Function`, sonst die Funktion `settings`, sonst nur Admin) nur für die Frage, ob der Eintrag überhaupt geöffnet werden darf; den Eltern-Code im Kinderprofil verlangt allein die Seite mit ihrer eigenen Funktion (ein Besuch nur dieser Seite). Ein Plugin kann höchstens 64 Einstellungen anmelden. Die Aktion in der Fußzeile der Seite (siehe oben) bleibt als Abkürzung daneben möglich.

## Aktionen einer Seite als Tastenhinweise (SDK 4)

Eine Seite kann bis zu drei **Aktionen** (`PluginPage.Actions`, Typ `PluginPageAction`) als kleine Tastenhinweise **unten rechts in der Fußzeile** zeigen, so wie die eingebauten Seiten es mit X und Y tun („X Geräte wählen“, „Y An Guide-Menü anheften“). **Aktionen gehören in die Fußzeile, nicht als Zeilen in die Mitte der Seite**: Zeilen sind für Inhalt (Geräte, Werte, Einträge), nicht für Knöpfe um den Inhalt herum. Ein Plugin, das `Actions` setzt, schreibt `"sdk": 4` in die `plugin.json`, damit eine ältere Mega-Hub-Version es gar nicht erst lädt; Plugins für SDK 1 bis 3 laufen unverändert; steht in der `plugin.json` weniger als 4, werden `Actions` ignoriert (und vom Prüfer beim Installieren beanstandet).

```csharp
context.RegisterPage(new PluginPage
{
    Id = "home", Title = "Smart Home", Rows = LoadRows, Activate = OnPress,
    Actions =
    [
        PluginPageAction.Open(PluginPageButton.X, "Geräte wählen", "devices"),   // öffnet eine andere Seite desselben Plugins
        PluginPageAction.Run(PluginPageButton.Y, "Anheften", "pin")              // ruft Activate("pin") auf
    ]
});
```

| Mitglied | Bedeutung |
|---|---|
| `Button` | `PluginPageButton.X` oder `PluginPageButton.Y`. Mehr gibt es nicht: A (Ausführen) und B (Zurück) gehören dem Kern, LB/RB wechseln Reiter und Chips, Guide öffnet das Guide-Menü, die Menütaste das Ausschaltmenü. Auf PlayStation-Controllern erscheinen die Symbole dieser Familie (Quadrat, Dreieck) |
| `Label` | Klartext, höchstens 24 Zeichen. Mega Hub säubert ihn wie einen Seitentitel: kein Emoji, keine Symbole, keine unsichtbaren Zeichen, keine Mischung aus lateinischen und griechischen/kyrillischen Buchstaben, nicht der Name eines eingebauten Bildschirms und keine Bezeichnung einer Systemfunktion wie „Zurück“, „Ausschalten“, „Eltern-Code“, „Menü“, „Startseite“, „Einstellungen“ (alles nur bei fremden Plugins; Groß-/Kleinschreibung und ähnlich aussehende Buchstaben zählen nicht, Deutsch und Englisch). Bleibt nichts übrig, fällt die Aktion weg |
| `PageId` | Ziel: eine andere Seite **desselben** Plugins. Mega Hub öffnet sie selbst, der Code des Plugins wird nicht gefragt; ändert nichts. Nie eine Adresse |
| `ActionId` | Ziel: die Kennung, die `PluginPage.Activate` bekommt, wie bei einer Zeile. Genau eines von `PageId` und `ActionId` ist gesetzt |
| `Function` | Optional: die Funktion (`PluginFunction`), deren Stufe gilt (Admin, eingeschränkt, niemand). Ohne Angabe gilt die der Seite |
| `RequiresParentGrant`, `AllowedWhileLocked` | Wie bei `PluginRow`: Eltern-Code unter Jugendschutz (bei fremden Plugins für jede Aktion, die etwas ändert, ohnehin immer) und Benutzung auf der gesperrten Couch (nur bei CouchHUDs eigenen Plugins) |

Die Prüfung macht Mega Hub, nie das Plugin: höchstens drei Aktionen, pro Taste eine (die erste gilt, Weitere entfallen), nur gültige Tasten, ein Ziel, ein gesäuberter Text. Gedrückt wird dieselbe Schranke wie bei einer Zeile (`PluginPolicy.MayPress`): gesperrte Couch, Stufe der Funktion, Eltern-Code, vor dem Aufruf des Plugins verbraucht. Eine Aktion, die diese Schranke ablehnen würde (gesperrte Couch bei einem fremden Plugin, Stufe „niemand“, Kinderprofil), wird gar nicht erst angezeigt. Der Host liest die Aktion bei jedem Druck neu aus der Liste der Seite; die Seite schickt nur die Taste. Mausnutzer klicken den Hinweis an. Die Liste ist fest; sie ändert sich, indem das Plugin die Seite mit denselben Kennungen neu anmeldet.

## Netzwerkdienste, Wetter, News und Assistent (SDK 5)

Seit 0.7 sind das Wetter, der News-Tab und die Cloud-Anbieter des Assistenten Plugins (Kennungen `weather`, `news`, `anthropic`, `openai`, je ein eigenes Repository). Der Kern behält die Anzeige (Wetter in der Kopfzeile, News-Tab, Assistent und MCP-Server) und holt die Daten über drei Steckplätze. Ohne installiertes Plugin fehlt die Funktion; wer sie vorher benutzt hat, bekommt einmal einen Hinweis mit dem Plugin aus dem offiziellen Katalog. Ein Plugin, das eines davon nutzt, schreibt `"sdk": 5`.

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `Http(dienst)` | `network:internet` | Ein `HttpClient` des Hosts für einen in der `plugin.json` angegebenen Dienst (siehe unten). Null für einen nicht angegebenen Dienst und auf älteren Hosts. Nicht freigeben, der Host besitzt ihn |
| `RegisterWeather(IPluginWeather)` | `weather` | Das Wetter in der Kopfzeile: `Place`, `SetPlace`, `SearchAsync` (Ortssuche der Einstellungsseite des Kerns), `GetAsync` (etwa alle 15 Minuten, mit Zeitlimit). Temperaturen in °C, Wettercodes nach WMO; der Host zeichnet die Symbole |
| `RegisterNews(IPluginNews)` | `news` | Die Quelle des News-Tabs: `Feeds`, `AddFeed`, `RemoveFeed` (die Liste pflegt die Einstellungsseite des Kerns), `GetAsync` (neueste zuerst). Alles reiner Text; Links nur http(s), Bilder nur https |
| `RegisterChatProvider(IPluginChatProvider)` | `assistant.provider` (nur CouchHUDs eigene Plugins) | Ein Cloud-Anbieter für den Assistenten: Name, Modelle, `SetKey` (der Kern übergibt einen neuen Schlüssel und liest ihn nie zurück), `CompleteAsync` (Gespräch und Werkzeuge rein, Text und Werkzeugaufrufe raus). Fehler als `PluginChatException` mit `KeyInvalid`, `RateLimited`, `ModelNotFound`, `TooLong` oder `Other` |

Je Steckplatz gilt nur eine Quelle: CouchHUDs eigenes Plugin gewinnt, sonst das zuerst geladene. Der Host prüft alles, was zurückkommt (Länge, Anzahl, Steuerzeichen, Adressen) und ruft jedes Plugin mit Zeitlimit auf; ein Fehler im Plugin wird nie wörtlich angezeigt. `assistant.provider` gibt es nur für CouchHUDs eigene Plugins, weil der Anbieter das ganze Gespräch samt Werkzeug-Ergebnissen sieht. Die Bereiche gelten weiter: Ist *News* aus, gibt es weder den Tab noch das Werkzeug `plugin_news_get_news`.

**Netzwerkdienste in der `plugin.json`:**

```json
"capabilities": ["weather", "network:internet"],
"services": [
  { "id": "open-meteo", "name": { "de": "Wetterdienst (Open-Meteo)", "en": "Weather service (Open-Meteo)" },
    "hosts": ["api.open-meteo.com", "geocoding-api.open-meteo.com"] }
]
```

Höchstens vier Dienste, je höchstens 16 Hosts. Ein Host ist ein DNS-Name (keine IP-Adresse, kein Port, kein Platzhalter-Teil) oder genau `"*"` für jeden öffentlichen Host (etwa Feeds, die der Nutzer selbst einträgt; so wird es auch angezeigt). Die Kennung folgt den Regeln einer Funktionskennung. Was nicht passt, verwirft der Host mit einer Protokollzeile. Der Client von `Http` lässt eine Anfrage nur durch, wenn

- Offline aus ist, der Schalter des Dienstes unter *Einstellungen › Datenschutz* an ist (jeder Dienst eines geladenen Plugins hat dort einen eigenen Schalter, Standard an, unter Jugendschutz hinter dem Eltern-Code) und das Plugin nicht pausiert ist,
- das Ziel ein angegebener Host ist, über https auf dem Standard-Port und ohne Benutzerdaten in der Adresse (nur bei `"*"` ist auch http erlaubt, und nur auf Port 80 oder 443),
- die Adresse öffentlich ist (keine Adressen im Heimnetz oder auf diesem PC, auch nicht über einen Namen, der dorthin zeigt).

Weiterleitungen folgt der Host selbst, nur bei GET und HEAD, höchstens fünf, nie von https auf http; jeder Schritt wird neu geprüft. Kopfzeilen, die den Aufrufer ausweisen (`Authorization`, `x-api-key`, Cookies und alles außer `Accept*`, `User-Agent` und den Cache-/Range-Kopfzeilen), gehen nur an den Host, für den sie gedacht waren. Sonst scheitert die Anfrage mit einer `HttpRequestException`.

Die angegebenen Dienste und Hosts gehören zum signierten Paket-Statement (Feld `services`: je Dienst `id` und `hosts`) und werden in der Liste der freigegebenen Plugins festgehalten. Ein Update, das einen Dienst oder Host dazunimmt (oder `"*"`), gilt wie eine neue Berechtigung: erneute Bestätigung mit Eltern-Code, die Hosts stehen im Bestätigungstext, und die automatische Aktualisierung fragt nach. Die `plugin.json` im Paket darf nicht mehr angeben als das Statement; ein Fremdentwickler-Plugin bekommt beim Laden nie mehr Hosts als freigegeben waren. Ältere Statements ohne `services` bleiben gültig und geben keinen Dienst an.

Der Code-Lint zählt `new HttpClient(...)` und die anderen eigenen Netzwerkverbindungen (`HttpClientHandler`, `SocketsHttpHandler`, `WinHttpHandler`, `System.Net.Sockets`, `ClientWebSocket`, `WebClient`/`WebRequest`, `Dns`, `Ping`, `SmtpClient`) weiter als Netzwerkcode. Ein Fremdentwickler-Plugin ab SDK 5, das Dienste angibt, darf sie gar nicht benutzen: die Installation wird abgelehnt, es muss über `Http` gehen. Ohne angegebene Dienste bleibt alles wie bisher, aber dann gelten die Datenschutz-Schalter für dieses Plugin nicht (das sagt ein Hinweis unter *Datenschutz*). Technisch abgeschottet ist ein Plugin damit trotzdem nicht (es läuft im Prozess von Mega Hub, der Lint ist keine Sandbox). Der Client begrenzt außerdem jede Antwort auf 8 MB.

**Übernahme beim Update:** Der Ort des Wetters, selbst eingetragene Feeds (die früheren Standard-Feeds nicht) und die API-Schlüssel (neu verschlüsselt für das jeweilige Plugin) werden einmal als erste Einstellungen der Plugins übernommen (`PluginSettingSeeds`) und danach aus der `config.json` gelöscht; ein Schlüssel nur, wenn die Übernahme wirklich gelang. Die früheren Datenschutz-Schalter Wetter, News und Assistent werden zu den Schaltern der Dienste `weather/open-meteo`, `news/feeds`, `anthropic/api` und `openai/api`. Beim Deinstallieren eines Plugins löscht der Host seine gespeicherten Geheimnisse; die übrigen Einstellungen bleiben für eine Neuinstallation.

## Dienst erlaubt?, Zustand von Mega Hub, Beenden, Einstellungen (SDK 5, additiv)

Vier Ergänzungen, die zu SDK 5 gehören, ohne eine neue Nummer zu brauchen: Jedes Mitglied hat eine Standardimplementierung, ein Plugin für SDK 4 läuft unverändert, und auf einem älteren Host antworten sie mit `false`, `null` oder tun nichts. Ein Plugin, das sie benutzt, prüft das (etwa `ctx.Host is null`).

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `ServiceAllowed(dienst)` | keine | Darf der angegebene Dienst jetzt nach draußen? Wahr nur, wenn Offline aus ist, der Schalter des Dienstes unter *Einstellungen › Datenschutz* an ist und das Plugin nicht pausiert ist. **Fail closed:** `false` für einen Dienst, der nicht angegeben (oder nicht freigegeben) war, für ein Plugin ohne `network:internet` oder `network:lan`, für `null` und leere Kennungen und wenn der Host es nicht sagen kann |
| `ServiceAllowedChanged` (`Action<bool>`) | keine | Die Antwort hat sich für einen Dienst des Plugins geändert (Offline, ein Schalter). Der Wert ist nur ein Hinweis (ob jetzt irgendein angegebener Dienst erlaubt ist); den tatsächlichen Zustand liefert `ServiceAllowed` je Dienst. Kommt auch, wenn das Plugin pausiert wird (dann muss es aufhören), auf einem Pool-Thread |
| `Host` (`IPluginHostState?`) | `host.state` | CouchHUDs Zustand, nur lesend (unten). Null ohne die Fähigkeit |
| `Events.HostStopping` | keine | Mega Hub beendet sich gleich (Beenden, Neustart): Verbindungen schließen, Offenes speichern, Geändertes zurücksetzen. Der Host wartet auf alle Plugins **zusammen höchstens 2 Sekunden** und beendet sich dann trotzdem; ein Handler, der hängt oder wirft, hält nichts auf. Einmal, auf einem Pool-Thread, nicht für ein pausiertes Plugin. Nicht bei einem Absturz, Kill oder Stromausfall und damit nicht garantiert: Zustand beim nächsten Start ebenfalls wiederherstellen |
| `Settings.SettingChanged` (`Action<string>`) | keine | Ein Wert der eigenen Einstellungen des Plugins hat sich wirklich geändert (Schlüssel, nie der Wert); nach einer Änderung über die Einstellungsseite und nach `Set`/`SetSecret` des Plugins selbst |

**Das Ziel im Heimnetz: `"hosts": ["lan"]`.** Ein Plugin, das ein Gerät im Heimnetz anspricht, dessen Adresse der Nutzer selbst einträgt (Home Assistant, ein Streaming-Rechner), kann das nicht als Host-Namen angeben. Dafür gibt es neben `"*"` ein zweites Sondertoken im `hosts` eines Dienstes:

```json
"capabilities": ["network:lan"],
"services": [ { "id": "hub", "name": { "de": "Home Assistant", "en": "Home Assistant" }, "hosts": ["lan"] } ]
```

`lan` bedeutet: ein vom Nutzer eingegebenes Ziel im Heimnetz. Es zählt als neue Berechtigung wie ein neuer Host (Bestätigung mit Eltern-Code, der Text nennt es, die automatische Aktualisierung fragt nach); `"*"` schließt es **nicht** ein, und es schließt `"*"` nicht ein. Der Dienst bekommt wie jeder andere einen eigenen Schalter unter *Einstellungen › Datenschutz*. **`lan` ist eine Angabe plus ein Datenschutz-Schalter, keine technische Sperre:** eigene Sockets eines Plugins mit `network:lan` kann Mega Hub nicht abriegeln; geprüft werden kann die Zusage nur bei Plugins, die von Mega Hub stammen. Das steht auch im Bestätigungstext der Installation. Er gilt nur für die Abfrage: **`Http(dienst)` bleibt für das Heimnetz gesperrt** (ein Dienst, der nur `lan` nennt, bekommt gar keinen Client), und ein Plugin mit `network:lan` darf weiter eigene Sockets öffnen. Mega Hub kann die nicht abriegeln; das Plugin muss vor dem Verbinden `ServiceAllowed` fragen und bei `ServiceAllowedChanged(false)` aufhören. Ein Fremdentwickler-Plugin, das Dienste angibt, darf ohnehin keine eigene Verbindung bauen (der Lint lehnt sie ab), `lan` hilft ihm also nur zusammen mit `Http`-Hosts.

**Zustand von Mega Hub (`host.state`):**

```csharp
IPluginHostState? host = ctx.Host;          // null ohne die Fähigkeit "host.state"
string version = host.Version;               // "0.7.0"
PluginShellMode mode = host.Mode;            // Shell (Mega Hub ist die Windows-Shell) oder App
bool pc = host.PcMode;                       // der Nutzer ist auf dem Windows-Desktop
PluginAppInfo? app = host.RunningApp;        // Id und Name, oder null
DateTimeOffset? timer = host.SleepTimerEnds; // Ende des Ausschalt-Timers, oder null
bool locked = host.Locked;                   // wie ctx.Locked
host.Changed += () => { /* neu lesen */ };   // zusammengefasst, höchstens alle 250 ms, nicht während der Pause
```

Alles wird beim Lesen vom Host geholt, der Anfangszustand ist also schon beim Laden da. Es gibt keine Handles und keine Aktionen. `RunningApp` ist `null`, wenn der Eintrag vom Nutzer versteckt ist oder sein Bereich (Spiele, Apps) ausgeschaltet ist; der Name ist bereinigt (keine Steuerzeichen, höchstens 100 Zeichen). Die Fähigkeit steht im Bestätigungstext der Installation.

**Spielereignisse (`GameStarted`, `GameEnded`) seit 0.7:** Für einen Eintrag, den der Nutzer versteckt hat oder dessen Bereich ausgeschaltet ist, gibt es kein Ereignis mehr; Kennung und Name sind bereinigt. `InstallDir` bleibt aus Kompatibilitätsgründen ein Pfad dieses PCs. Kinderprofile werden nicht gesondert gefiltert: Ein Titel, den das Profil zeigt, sehen auch die Plugins.

**Eigene Namen:** Marken (`casehud`, `couchhud`, `nordvpn`, `homeassistant`, `stardust`, `tvcontrol`, `webostv`, `samsungtv`, `anthropic`, `openai`) sind für Fremdentwickler als ganzer Name und, ab fünf Zeichen, auch als Teil eines Namens oder Anzeigenamens reserviert (auch mit ähnlich aussehenden Zeichen). Allgemeine Wörter (`wand`, `vpn`, `weather`, `news`, `streaming`, `fancontrol`) nur als ganzer Name oder als erstes Wort: `weather` und `weather-plus` sind belegt, `my-weather-widget` und `fan-controller` frei. Ein Plugin-Eintrag mit einer Kennung, die mit `plugin:`, `steam:` oder `dir:` beginnt, wird ausgelassen.

**Gemeinsames Zeitlimit mehrerer Einträge (`LibraryItem.LimitGroup`).** Zeitlimit und Sperre der Eltern gelten je Spiel; Einträge mit demselben `InstallDir` zählen bisher als dasselbe Spiel. Liefert ein Plugin viele Einträge mit demselben Ordner (etwa Streams über einen Moonlight-Ordner), teilten sich alle das Limit. Mit `LimitGroup` (kurzer Text, höchstens 64 Zeichen, keine Steuerzeichen; sonst gilt er als nicht gesetzt) bilden nur Einträge mit derselben Gruppe ein Limit. Der Host stellt die Plugin-Kennung voran (`plugin:<kennung>:<gruppe>`), die Gruppe trifft also nie auf den Schlüssel eines anderen Plugins oder eines Kern-Eintrags (Steam-Kennung, `dir:`). Ohne `LimitGroup` bleibt alles wie bisher; die Erkennung des laufenden Spiels ändert sich nicht.
## Fernseher steuern (SDK 5, Fähigkeit `tv.control`)

Seit 0.7 spricht der Kern selbst kein TV-Protokoll mehr. Er behält alles rund um den Fernseher: die Seite *Einstellungen › TV*, den Kopplungsdialog, die Auslöser (TV mit Mega Hub einschalten, mit dem PC ausschalten), Wake-on-LAN, die Suche im Heimnetz (SSDP), die Adressregel (nur Heimnetz, nur das gekoppelte Gerät) und den Schlüssel (pro Fernseher, mit DPAPI für den Windows-Benutzer verschlüsselt, nie im Protokoll). Ein Plugin bringt nur das **Protokoll einer Fernseher-Art** mit, als `ITvProvider`. CouchHUDs eigene Plugins dafür sind **LG webOS** (`webostv`, Art `lg-webos`) und **Samsung Tizen** (`samsungtv`, Art `samsung-tizen`) im offiziellen Katalog.

```csharp
public void Start(IPluginContext context) => context.RegisterTvProvider(new MyTvProvider());

sealed class MyTvProvider : ITvProvider
{
    public string Id => "acme-tv";                       // klein, a-z 0-9 und '-', höchstens 32 Zeichen
    public string Name => "Acme TV";
    public IReadOnlyList<TvPort> Ports => [new(4430, Tls: true), new(8080, Tls: false, KeyFree: true)];

    public async Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct)
    {
        await using var ch = await link.OpenAsync(4430, "/pair", ct);   // nur Pfad: Adresse und Schema bestimmt der Kern
        onPrompt();                                                       // "Bitte am Fernseher bestätigen"
        // ... Antwort lesen ...
        return new TvPairResult(key) { Model = model, Mac = mac };
    }

    public Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct) => ...;
}
```

`plugin.json`: `"sdk": 5` und `"capabilities": ["tv.control"]`. Eine Netzwerk-Fähigkeit braucht das Plugin **nicht**, und ein fremdes Plugin darf `tv.control` auch nicht mit `network:*`, `native`, `process` oder `dynamicCode` zusammen haben (die Installation wird abgelehnt; ein solches Plugin bekommt `tv.control` auch dann nicht, wenn die Prüfung umgangen wurde). Die Prüfung beim Installieren meldet `ITvProvider`/`ITvLink` ohne `tv.control` und unter `"sdk": 5` („nutzt SDK 5 (TV control), ohne es anzugeben“).

**Das Plugin öffnet nie selbst eine Verbindung.** Jedes Byte geht über das `ITvLink`, das der Kern ihm reicht. Der Link

- erreicht genau **eine** IP-Adresse (die gekoppelte oder die, mit der gerade gekoppelt wird), nur im Heimnetz, ohne DNS, ohne Proxy, ohne Weiterleitungen, ohne Cookies; die Adresse wird beim Verbinden noch einmal geprüft;
- nur auf den **Ports, die der Provider angemeldet hat** (höchstens 8; ein anderer Port ist „blockiert“ und zählt als Fehler der ganzen Kopplung, auch wenn das Plugin ihn schluckt);
- **TLS** auf Ports mit `Tls: true`: Das selbst signierte Zertifikat des Fernsehers wird beim Koppeln mit seinem öffentlichen Schlüssel festgehalten (trust on first use) und danach bei jeder Verbindung verlangt; ein anderes Zertifikat ergibt „certChanged“, ohne dass etwas gesendet wird;
- einen **Klartext-Port** ohne `KeyFree` nur, wenn der Benutzer beim Koppeln ausdrücklich unverschlüsselt zugestimmt hat (Expertenmodus, nie für eine Adresse oder MAC, die schon verschlüsselt gekoppelt war); in der Sitzung einer TLS-Kopplung nie, und nie mehr, nachdem TLS auf einem TLS-Port gescheitert ist (Handshake abgelehnt, Verbindung zurückgesetzt);
- **`KeyFree`-Ports** (z. B. Geräteinfos über HTTP) nur für HTTP-Anfragen, nie als Web-Socket, und in einer Sitzung lehnt er jede Anfrage ab, in der er den Schlüssel erkennt (Pfad, Text, SOAPACTION, Content-Type; auch URL-kodiert, als Base64, Hex, JSON- oder XML-Zeichenverweis). Das ist ein Sicherheitsnetz gegen Fehler im Plugin nach bestem Wissen, keine Schranke gegen böswilligen Code. Eine Verbindung über einen `KeyFree`-Port zählt nicht als Kopplung: Ein Schlüssel, der über keine echte Verbindung kam, wird verworfen;
- begrenzt Größen und Zeit: Web-Socket-Nachrichten nur als Text bis 256 KB (größer oder binär schließt die Verbindung), senden bis 64 KB, HTTP-Antworten bis 64 KB, Anfragetext bis 16 KB, nur GET und POST, Kopfzeilen ohne Steuerzeichen, Pfade nur druckbares ASCII ab `/`, höchstens 4 Web-Sockets gleichzeitig und 30 neue Verbindungen in 10 Sekunden;
- wird mit der Sitzung geschlossen (alle Web-Sockets des Plugins gehen mit zu).

Der Kern prüft auch, was das Plugin zurückgibt: Kopplung höchstens 90 s, Verbindung 15 s, jeder Befehl 10 s (auch wenn das Plugin den Token ignoriert); ob eine Kopplung verschlüsselt ist und welches Zertifikat gilt, entscheidet allein, was über den Link ging; der Schlüssel muss 8 bis 256 druckbare ASCII-Zeichen haben; Lautstärke wird auf 0 bis 100 begrenzt, Eingänge auf 32 mit gültiger Kennung (`A-Z a-z 0-9 _ . : -`, höchstens 64) und bereinigtem Namen, App-Kennungen, Modell und Name werden bereinigt; jede andere Ausnahme des Plugins wird zu „ungültige Antwort“. Den Schlüssel einer Kopplung bekommt nur das Plugin, das gekoppelt hat, und nur solange es läuft (eingeschaltet, nicht pausiert, nicht deinstalliert). Die Arten `lg-webos` und `samsung-tizen` sind für CouchHUDs eigene Plugins reserviert; ein Plugin kann höchstens zwei Arten anmelden.

| `ITvSession` | Pflicht? | Bedeutung |
|---|---|---|
| `TurnOffAsync`, `GetVolumeAsync`, `VolumeStepAsync`, `SetVolumeAsync`, `SetMuteAsync` | ja | Ausschalten, Lautstärke |
| `GetInputsAsync`, `SwitchInputAsync`, `GetForegroundAppAsync` | ja | Eingänge (die Kennung wird vor dem Aufruf geprüft), App im Vordergrund („TV mit dem PC ausschalten“ nur, wenn sie zur gespeicherten PC-App passt) |
| `GetActiveInputAsync` | nein (null) | Eingang im Vordergrund; nur eine Kennung aus der Liste zählt |
| `GetPowerAsync` | nein (`true`) | `false`: erreichbar, aber im Standby (die Seite zeigt „Aus“, Einschalten geht dann über `TurnOnAsync`) |
| `TurnOnAsync` | nein (abgelehnt) | Einschalten über die Schnittstelle aus dem Standby; sonst Wake-on-LAN des Kerns |

Fehler meldet das Plugin als `TvException` mit `TvError` (`Unreachable`, `Timeout`, `Denied`, `Unpaired`, `Refused`, `BadAnswer`, …); `NotLocal`, `Downgrade`, `NoProvider` und ähnliche gibt nur der Kern. **Kopplungen aus 0.6** (damals sprach der Kern webOS selbst) bleiben erhalten und gehören dem Plugin `webostv`: Bis es installiert ist, zeigt die TV-Seite „Plugin für LG webOS installieren“ (öffnet den Katalog-Eintrag), und Mega Hub schlägt es einmal vor; danach gilt der gespeicherte Schlüssel ohne neue Bestätigung am Fernseher. Tests laufen gegen einen nachgebauten Fernseher auf 127.0.0.1 (`TvLinkOptions.AllowLoopback`, nur in Tests); echte Fernseher fasst kein Test an.

## Eigene Reiter (Fähigkeit `tabs`)

Ein Plugin kann Seiten, die es mit `RegisterPage` beschreibt, zusätzlich als **Reiter in der Kopfleiste** zeigen. Dafür deklarierst du die Fähigkeit `"tabs"` in `plugin.json` (sie steht in der Berechtigungsliste und im Katalog) und meldest den Reiter an:

```csharp
context.RegisterPage(new PluginPage { Id = "home", Title = "Smart Home", Rows = LoadRows, Sections = sections });
context.RegisterTab(new PluginTab("home", "home", "Smart Home", "Smart Home")
{
    Glyph = "🏠",      // genau ein Symbol; oder Icon = pngBytes (PNG bis 64 KB, höchstens 256 x 256 Pixel)
    Area = "always",   // games | apps (Standard) | browser | news | always (nur eigene Plugins von Mega Hub)
    ChildSafe = true   // auch im Kinderprofil zeigen (nur eigene Plugins von Mega Hub)
});
```

`PluginTab(Id, PageId, TitleDe, TitleEn)`: `PageId` ist eine Seite **desselben** Plugins; die Seite zeichnet Mega Hub wie jede Plugin-Seite (Kacheln, Zeilen, Regler, Auswahl, Verweise), ein Plugin bringt nie HTML oder Skript mit. Meldet das Plugin `Invalidate(pageId)`, lädt sich der offene Reiter neu. Eine Anmeldung mit derselben Id ersetzt die frühere. Beide Titel gelten für beide Sprachen, du musst bei einem Sprachwechsel nichts neu melden.

| Regel | Wert |
|---|---|
| Reiter je Plugin / insgesamt | 2 / 6 (CouchHUDs eigene Plugins zuerst, dann die anderen in der Reihenfolge der Anmeldung; was darüber hinausgeht, wird ignoriert) |
| Kennung | `a-z`, `0-9`, `-`, `_`, bis 32 Zeichen |
| Titel | 1 bis 14 Zeichen (die Leiste ist schmal), lateinische Buchstaben einschließlich Umlauten und Akzenten (`Küche`, `Übersicht`), Ziffern, Leerzeichen und `- & . ' + /`; Verwechslungsbuchstaben wie `ı` oder `ł` und andere Schriften werden abgelehnt; keine Steuer-, Bidi- oder Nullbreitenzeichen; wenigstens eine Sprache. **Titel von Reitern, Seiten und der Plugin-Name sind reiner Text**: Mega Hub entfernt Emoji, Symbole, Pfeile, Verbindungs- und unsichtbare Zeichen, Bidi-Steuerzeichen und Steuerzeichen und kürzt auf eine feste Länge (Reiter 14 Zeichen, Seitentitel und Name 60); bleibt nichts übrig, fällt der Titel auf den Namen bzw. die Id des Plugins zurück (ein Reiter ohne Titel entfällt) |
| Gesperrte Titel | Home, Favoriten/Favorites, Spiele/Games, Apps, Browser, Downloads, News/Nachrichten, Assistent/Assistant, Einstellungen/Settings, Katalog, Plugins, Guide, Profile, Eltern, Sicherheit, Mega Hub und die Namen von CouchHUDs eigenen Plugins, auch mit Ziffern statt Buchstaben, getrennt geschrieben oder als Teil eines längeren Titels (`Apps Hub`). „Smart Home“ darf nur ein eigenes Plugin von Mega Hub tragen |
| Symbol | Die Kopfleiste zeigt **nur den Titel**, wie bei jedem Reiter von Mega Hub: kein Symbol, kein Emoji, keine eigene Schrift oder Farbe. `Glyph` und `Icon` werden weiter angenommen, aber in der Leiste nicht gezeichnet. Symbole gehören in die Zeilen deiner Seiten (`PluginRow.Icon`) |
| `Area` | `games`, `apps` (Standard), `browser`, `news`. `always` (nie von einem Bereich ausgeblendet) gilt nur für CouchHUDs eigene Plugins; bei jedem anderen zählt es als `apps` |
| `ChildSafe` | Der Reiter erscheint auch im Kinderprofil. Wirkt nur bei CouchHUDs eigenen Plugins (z. B. ein Smart-Home-Reiter, der vorher schon für Kinder sichtbar war); bei allen anderen wird es ignoriert. Die Seite dahinter bleibt hinter den üblichen Schranken |

Wo der Reiter steht und wann er fehlt: nach den eingebauten Inhalts-Reitern, vor Assistent und Einstellungen. Er fehlt, wenn das Plugin aus oder nicht geladen ist, wenn sein **Bereich** (`Area`) ausgeschaltet ist, wenn die Seite nicht (mehr) angemeldet ist und **in Kinderprofilen, außer bei einem eigenen Plugin von Mega Hub mit `ChildSafe`**. Auf der gesperrten Couch bleiben nur die Reiter von CouchHUDs eigenen Plugins; die Seite zeigt nur, was `AllowedWhileLocked` freigibt, und bedienen lassen sich dort nur Zeilen von CouchHUDs eigenen Plugins (wie bei jeder Plugin-Seite). Schaltet der Nutzer das Plugin aus oder deinstalliert es, verschwindet der Reiter sofort. Alles, was eine Zeile ändert, geht durch dieselben Schranken wie sonst (Entsperren, Eltern-Code).

## Wer darf was: Funktionen

Ein Plugin kann benannte **Funktionen** anmelden und seine Seiten (`PluginPage.Function`), Zeilen (`PluginRow.Function`, sonst gilt die der Seite), Menüeinträge (`QuickAction.Function`), Reiter (`PluginTab.Function`) und Werkzeuge (`PluginTool.Function`) damit markieren:

```csharp
context.RegisterFunctions(
    new PluginFunction("lighting", "Beleuchtung", "Lighting") { Suggested = PluginLevel.Restricted },
    new PluginFunction("settings", "Einstellungen", "Settings"));   // Admin ist der Standard
```

Der Besitzer wählt pro Funktion in *Plugins › (Plugin)*: **Nur Admin** (`admin`), **Auch eingeschränkt** (`restricted`: auch gesperrte Couch und Kinderprofil) oder **Niemand** (`off`: ausgeblendet, Aufrufe abgelehnt). `Suggested` ist nur der Startwert, und nur bei CouchHUDs eigenen Plugins zählt `Restricted`; bei jedem anderen Plugin startet die Funktion als *Nur Admin*, der Besitzer kann sie freigeben. Der Host erzwingt die Stufe vor jedem Zeigen und Ausführen; ein markierter Eintrag folgt allein der Stufe, seine Flags (`AllowedWhileLocked`, `RequiresParentGrant`) gelten nur für nicht markierte. Bei *Auch eingeschränkt* fragt der Host nie nach dem Eltern-Code; wer das nicht will, lässt die Funktion bei *Nur Admin*. Eine Funktion, die nicht angemeldet wurde, zählt als *Nur Admin*. Prüfe nicht selbst strenger oder lockerer: Entscheidet der Host, dass eine Funktion erlaubt ist, muss die Seite auch ohne weitere eigene Sperre laufen. Im Kinderprofil verlangt eine Admin-Funktion den Eltern-Code (eine Seite öffnet sich damit für einen Besuch von höchstens 5 Minuten, eine Zeile oder ein Eintrag ohne Admin-Seite fragt bei jedem Druck); für Werkzeuge ist sie dort verboten. Plugins, die Funktionen anmelden, brauchen Mega Hub 0.6.0 oder neuer (`minCouchHud` 0.6.0). `RegisterFunctions` braucht keine Fähigkeit und keinen neuen SDK-Stand; ein älterer Host ignoriert es, die alten Flags gelten dann weiter. Höchstens zwölf Funktionen, Kennung `a-z0-9-_` bis 32 Zeichen.

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

`description` ist ein Text oder pro Sprache `{ "de": "Sagt Hallo", "en": "Says hello" }`. Mega Hub zeigt die Beschreibung in der eingestellten Sprache und fällt auf die andere zurück.

`sdk` ist die SDK-Version, die das Plugin braucht. Ältere Mega-Hub-Versionen laden es dann gar nicht erst, statt abzustürzen. SDK 2 ist abwärtskompatibel: Plugins für SDK 1 laufen unverändert weiter, `Locked` und `LockChanged` sind nur Zusätze (mit Standardimplementierung, ein Plugin, das sie nutzt, läuft auch auf einem älteren Mega Hub, dort ist `Locked` immer `false`). Wer sie braucht, schreibt `"sdk": 2` nur, wenn das Plugin ohne sie nicht sicher ist. **SDK 4 ist anders:** Wer `PluginPage.Actions` nutzt, muss `"sdk": 4` schreiben (das Plugin kennt die Mitglieder sonst auf einem älteren Mega Hub nicht, und ein älteres Mega Hub lädt es dann gar nicht erst). Steht weniger als 4 in der `plugin.json`, ignoriert Mega Hub die Aktionen (nichts wird angezeigt, ein Druck wird abgelehnt), und die Prüfung beim Installieren meldet sie als „nutzt SDK 4, ohne es anzugeben“. Pakete im Katalog mit einem höheren `sdk`, als das laufende Mega Hub kennt, zeigt es als „Benötigt neueres Mega Hub“ und installiert sie nicht. **SDK 5** (Mega Hub 0.7) bringt `services`, `Http` und die Steckplätze für Wetter, News und Assistent sowie die Fernseher-Steuerung (`ITvProvider`, `tv.control`): Die `services` einer `plugin.json` mit `sdk` unter 5 übergeht der Host, ein solches Plugin bekommt also keinen Client von `Http`.

## Zwei Sprachen

Mega Hub gibt es auf Deutsch und Englisch, und die Sprache lässt sich zur Laufzeit umstellen. Dein Plugin liest sie mit `context.Language` (`"de"` oder `"en"`; ältere Mega-Hub-Versionen melden immer `"de"`, das Plugin läuft also weiter).

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

Das Wand-Beispiel (`plugins/CouchHud.Plugin.Wand`, nur noch als SDK-Beispiel) macht genau das. Es verwendet, wie die Regeln für Plugins es verlangen, keine Icons oder Emojis in Namen von Seiten und Aktionen.

## Regeln, damit Mega Hub stabil bleibt

- Jeder Aufruf in ein Plugin (Seiten, Aktionen, Tools) hat ein Timeout von 15 Sekunden. Langsame Arbeit gehört in `Task.Run` oder einen Hintergrund-Task.
- `QuickAction.Detail` (die zweite Zeile im Guide-Menü) wird auf einem Hintergrund-Thread mit 2 Sekunden Timeout abgefragt, nie auf dem UI-Thread. Antwortet es nicht, hat der Eintrag keine zweite Zeile; solange ein Aufruf hängt, wird kein neuer gestartet. Der Takt von Sperre und Jugendschutz hängt davon nicht ab.
- Exceptions werden abgefangen und als Meldung angezeigt. Ein kaputtes Plugin legt nie die Shell lahm.
- Event-Handler laufen im Hintergrund, nie auf dem UI-Thread.
- `Initialize` hat 30 Sekunden Zeit. Wer Timer, Tasks oder Verbindungen startet, implementiert `IDisposable` und beendet sie dort (höchstens 5 Sekunden), damit sich das Plugin ohne Neustart ausschalten lässt (siehe [Plugins ohne Neustart](#plugins-ohne-neustart)).
- Jedes Plugin hat einen eigenen Load-Context: eigene NuGet-Abhängigkeiten in eigenen Versionen sind erlaubt.
- Plugins laufen mit den Rechten des Benutzers. Bitte nichts tun, was der Nutzer nicht erwartet, und Secrets nur über `SettingKind.Secret` speichern.
- **Freigabe:** Mega Hub merkt sich beim Installieren (Katalog, `.zip`) einen Hash der Dateien jedes Plugins. Ein Ordner in `%APPDATA%\CouchHud\plugins`, den niemand über Mega Hub installiert hat, oder dessen Dateien sich geändert haben (auch von Hand kopiert!), lädt erst nach einer Freigabe (*Plugins & Skins* › Plugin › *Plugin freigeben*, mit dem Code der Dateien, nur entsperrt). Wer sein Plugin beim Entwickeln direkt in den Ordner baut, gibt es nach jeder Änderung neu frei, oder installiert es als `.zip`. Plugins, die schon vor dieser Funktion dort lagen, gelten beim ersten Start als freigegeben. Ein vorgemerktes Katalog-Update wird vor dem Einspielen erneut gegen die Prüfsumme geprüft. In den Hash gehen alle Dateien des Ordners ein, außer `desktop.ini`, `Thumbs.db` und `*.tmp` (die legen Windows und Programme von allein an). Die Liste der freigegebenen Plugins (`plugin-trust.json`) trägt eine Signatur (HMAC, derselbe Schlüssel wie bei `playtime.json`, mit DPAPI an das Windows-Konto gebunden). **Wogegen das schützt:** gegen ein Programm oder Skript, das nur Dateien schreibt, einen fremden Ordner ablegt und die Liste von Hand ergänzt oder ersetzt, und gegen eine hineinkopierte Liste. **Wogegen nicht:** ein Programm, das als dasselbe Konto läuft, kann den Schlüssel auch lesen; die Freigabe ersetzt keinen Virenschutz. Passt die Signatur nicht oder ist die Liste unlesbar, meldet Mega Hub einen Fehler an den Plugins, lädt nichts Unbekanntes und überschreibt die Datei nicht; zum Neubeginnen `plugin-trust.json` löschen. Danach gilt, was dann im Plugin-Ordner liegt, beim nächsten Start als freigegeben (wie beim allerersten Start): vorher prüfen, dass dort nur Plugins liegen, die du kennst.
- **Reservierte Namen:** Namen, die CouchHUDs eigene Plugins tragen oder enthalten (`casehud`, `couchhud`, `homeassistant` und die Kennungen aus dem signierten Index), lehnt Mega Hub auch bei einem lokalen `.zip` ohne Signatur ab, und zwar vor dem Entpacken: als Ordnername, als `id` in der `plugin.json` und beim Laden als Kennung des Plugins selbst. Ein Paket ohne Signatur ersetzt außerdem nie einen Ordner, der mit Signatur installiert wurde.

## Plugins ohne Neustart

Seit 0.7 braucht nicht mehr jede Änderung einen Neustart von Mega Hub. Was sofort geht und was nicht:

| Vorgang | Wirkung |
|---|---|
| Installieren (Katalog oder `.zip`), Freigeben, Zurückgezogenes wieder erlauben | **sofort**: das Plugin startet über denselben Lader wie beim Start |
| Einschalten | **sofort**, es sei denn, ein früherer Lauf desselben Ordners ließ sich nicht sicher beenden (dann nach dem Neustart: zwei Kopien eines Plugins laufen nie nebeneinander) |
| Ausschalten, Deinstallieren | **sofort** für Mega Hub: Reiter, Seiten, Einstellungen, Schnellaktionen, Werkzeuge, Funktionen und Ereignisse sind weg, jeder weitere Aufruf des Plugins an den Host wird still ignoriert. Danach wird das Plugin zum Beenden aufgefordert (siehe unten). Bestätigt es das, ist es vollständig gestoppt; sonst enden seine eigenen Hintergrundaufgaben erst mit dem Neustart, und die Oberfläche sagt das |
| Update | **beim nächsten Start**: die DLLs des laufenden Plugins sind belegt und der alte Code lässt sich nicht in begrenzter Zeit nachweislich entladen; ein Update mit neuen Fähigkeiten ist außerdem eine neue Freigabe, kein Austausch |

**Sauber beenden lassen:** Implementiert die Plugin-Klasse `IDisposable` (oder `IAsyncDisposable`), ruft Mega Hub `Dispose` beim Ausschalten und Deinstallieren auf, höchstens 5 Sekunden lang. Dort gehören Timer, Tasks (per `CancellationToken`), Sockets, Prozesse und Ereignis-Abos hin. Kehrt `Dispose` rechtzeitig zurück, gilt das Plugin als vollständig gestoppt, sein Lade-Kontext wird freigegeben und die Dateien werden wieder frei. Ein Plugin ohne `IDisposable` wird abgeschaltet, aber nicht als gestoppt gezählt. Wird ein solches Plugin deinstalliert, bleiben seine Dateien bis zum nächsten Start; die Liste zeigt es dann als „Deinstalliert – wird beim nächsten Start entfernt“, ohne Aktionen, und es lässt sich bis dahin nicht wieder einschalten oder laden. Nach dem Abschalten liest es seine Einstellungen leer und schreibt keine mehr, und alle Abos auf Ereignisse des Hosts sind weg (deshalb lässt sich der Lade-Kontext freigeben, wenn das Plugin keine Host-Objekte festhält). **Native Bibliotheken** (`DllImport`) lädt Mega Hub nur aus dem Plugin-Ordner, wenn sie mitgeprüft wurden, sonst nur System-Bibliotheken aus System32; eine einmal geladene native Bibliothek bleibt bis zum Beenden von Mega Hub im Prozess. Ein Plugin, das eine geladen hat, lässt sich im selben Lauf nicht noch einmal starten (auch kein neues Paket im selben Ordner): nach dem Ausschalten ist ein Neustart nötig. Dieselbe Kennung in einem anderen Ordner startet nicht, solange ein früherer Lauf nicht nachweislich gestoppt ist. Hängt `Dispose` oder wirft es, wartet Mega Hub nicht weiter (nie länger als die 5 Sekunden), das Plugin bleibt abgeschaltet und startet erst nach einem Neustart wieder.

**Starten hat ein Zeitlimit:** `Initialize` darf 30 Sekunden dauern. Hängt es oder wirft es, wird alles zurückgenommen, was das Plugin bis dahin angemeldet hat, und der Eintrag zeigt den Fehler; ein hängender Start hält weder Mega Hub noch andere Plugins auf. Solange `Initialize` läuft, ist das Plugin für Listen, Reiter und Aufrufe unsichtbar; Registrierungen und Eintrag erscheinen in einem Schritt, wenn es fertig ist.

**Dieselben Prüfungen wie beim Start:** Ein sofort gestartetes Plugin geht durch genau den Code des Starts, kein zweiter Weg: Freigabeliste (Hash der Dateien), Entfernt-Markierung, Rückruf-Liste des Katalogs, Ein-/Aus-Schalter, Offline-Modus, SDK-Stufe, Kennungsregeln, Fähigkeiten aus `PluginPolicy.Granted` und Funktionsstufen der Eltern. Die Schranken davor (entsperrte Couch, kein Kinderprofil, Expertenmodus, Eltern-Code) gelten wie bisher für jede dieser Änderungen. Die Dateien des Ordners werden dabei einmal geöffnet und festgehalten: Der Hash, der mit der Liste verglichen wird, und der Code, der geladen wird, sind dieselben Bytes; der Lade-Kontext lädt keine Datei, die nicht mitgehasht wurde. Der Ordner eines laufenden Plugins lässt sich deshalb nicht überschreiben, umbenennen oder löschen, bis es gestoppt ist.

## Plugin teilen

Repository auf GitHub mit dem Topic `couchhud-plugin` anlegen und ein Release mit dem `.zip` (Ordner mit DLL, Abhängigkeiten und `plugin.json`) hochladen. So finden andere es. Wer will, dass es direkt in Mega Hub erscheint, trägt es im [Katalog](#katalog) ein.

## Katalog

Mega Hub kann Plugins und Skins aus einem **Katalog** installieren und aktualisieren: *Einstellungen › Darstellung › Neue Skins installieren* (oder im Expertenmodus *Plugins & Skins*, dort *Katalog*) (mit LB/RB zwischen *Installiert* und *Katalog* wechseln). Ein Katalog ist eine einzelne `index.json` irgendwo im Netz. Beispiel: [catalog/index.json](../catalog/index.json).

Der Standard-Katalog liegt unter `https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json`. Er ist mit CouchHUDs Schlüssel signiert, immer dabei und lässt sich weder entfernen noch ausschalten.

Weitere Kataloge von anderen Entwicklern kommen nur über die Oberfläche des HUD hinein (Expertenmodus, Eltern-Code, nie bei gesperrter Couch oder im Kinderprofil), nur über https und erst nach der Warnung, dass du dem Entwickler vertrauen musst: Er könnte Schadcode einschleusen. Sie stehen in `ExtraCatalogSources` (höchstens 8; Adresse, optional der festgehaltene Schlüssel des Katalogs, Name). Alles aus einem solchen Katalog ist Fremdentwickler: ein Plugin braucht die Signatur seines Entwicklers (Paketangabe), Namen von CouchHUDs Plugins und Kennungen des Standard-Katalogs sind gesperrt, und die Größe des Index ist begrenzt (512 KB, 200 Einträge). Bei gleicher `id` gewinnt immer der Standard-Katalog.

```json
"CatalogUpdateCheck": true
```

Die alte Liste `CatalogSources` in der `config.json` wirkt seit 0.6 nur noch zusammen mit dem Entwickler-Schalter (siehe unten).

### Oberfläche (Fernseher)

*Plugins & Skins › Katalog* ist eine Liste wie ein Homebrew-Kanal: Icon, Name, Kurzbeschreibung, Herausgeber, Version, Größe. Plugins von Fremdentwicklern tragen dauerhaft das Zeichen „Fremdentwickler“. **A** öffnet die Detailseite (Langbeschreibung, Screenshots, Berechtigungen in einfachen Worten, Lizenz, Änderungen), **Y** installiert oder aktualisiert direkt aus der Liste. Die Liste ist nach Kategorien gegliedert (Smart Home, Streaming, Hardware, Medien, KI, Information, HUD, Skins, Sonstige; es erscheinen nur Kategorien mit Einträgen, innerhalb einer Gruppe stehen Updates zuerst). Die oberste Zeile *Kategorie* wählt mit **A** reihum *Alle* oder eine einzelne Kategorie; der Cursor bleibt auf der Zeile, **B** geht wie immer zurück. Jede Änderung geht durch dieselben Schranken in derselben Reihenfolge: Warnung (bei Fremdentwicklern mit Entwickler, Schlüssel und Berechtigungen, *Abbrechen* ist vorgewählt), dann der Eltern-Code, dann erst der Aufruf. Der Host prüft alles noch einmal; die Seite bietet nur an, was er erlaubt.

Der Reiter *Quellen* gibt es nur im Expertenmodus. CouchHUDs Katalog steht oben („Offiziell“, nicht entfernbar), weitere Kataloge zeigen Name und Host. Hinzufügen: Adresse (und optional der Schlüssel) mit der Bildschirmtastatur, die volle Warnung, der Eltern-Code, dann erst der Aufruf mit der Bestätigung.

Seiten, die ein Plugin beschreibt (`RegisterPage`), zeichnet `ui/views/plugin-page.js`: Kacheln, Bilder (nur PNG), Schalter, Stufenregler (`Stepper`, mit `SettleMs`), Auswahl, Links auf Unterseiten; meldet das Plugin `Invalidate(pageId)`, wird die offene Seite neu geladen.

### Format

```json
{
  "version": 1,
  "serial": 1,
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
| `names` | nein | `{ "de": …, "en": … }`: der Anzeigename in der Sprache von Mega Hub (ab 0.6); ältere Versionen lesen `name` |
| `type` | ja | `plugin` oder `skin` |
| `version` | ja | SemVer (`1.2.3`, `1.3.0-beta.1`). Der Katalog zeigt „Update“, sobald sie höher ist als die installierte |
| `download` | ja | **https**-Adresse eines `.zip` (Aufbau wie bei der manuellen Installation, siehe oben und [SKINS.md](SKINS.md)) |
| `sha256` | ja | SHA-256 des `.zip`, 64 Hex-Zeichen. Ohne passende Prüfsumme wird nichts installiert |
| `description` | nein | Text oder `{ "de": …, "en": … }` |
| `author`, `homepage`, `icon` | nein | `homepage` und `icon` nur als https |
| `minCouchHud` | nein | Kleinste Mega-Hub-Version. Ist die laufende kleiner, bleibt der Eintrag sichtbar, ist aber nicht installierbar |
| `folder` | nein | Ordnername des Pakets, Standard ist die `id`. Bei Plugins der Name der DLL und des Ordners (`CouchHud.Plugin.Wand`) |
| `category` | nein | Kategorie für die Gruppierung im Katalog (ab 0.7): `smartHome`, `streaming`, `hardware`, `media`, `ai`, `information`, `hud`, `skins` oder `other`. Groß-/Kleinschreibung, Bindestriche und Unterstriche sind egal (`Smart-Home` gilt als `smartHome`). Die Namen zeigt Mega Hub selbst in Deutsch und Englisch; freier Text aus dem Index wird nie angezeigt. Fehlt das Feld oder ist der Wert unbekannt (etwa aus einem neueren Index), steht der Eintrag unter *Sonstige*, ein Skin unter *Skins* (ein Skin kann nur `skins` oder `hud` tragen, alles andere wird `skins`). Ältere Indizes ohne das Feld funktionieren unverändert, ältere Mega-Hub-Versionen ignorieren es |

Einträge ohne `id`, `name`, gültigen `type` oder gültige `version` werden übersprungen. Einträge ohne gültige Prüfsumme oder mit einer Adresse ohne https werden angezeigt, lassen sich aber nicht installieren. Ein kaputter Index (kein JSON) ist ein Fehler der Quelle; Mega Hub zeigt dann den zuletzt gespeicherten Stand.

Das `.zip` muss genau einen Ordner namens `folder` enthalten (oder die Dateien direkt, dann wird `folder` als Name genommen). Die Hilfe `scripts\catalog-entry.ps1 -Folder <Ordner> -Version 1.0.0` packt den Ordner richtig und gibt die Prüfsumme für den Eintrag aus.

### Vertrauen: Prüfsumme und Signatur

Zwei Schutzschichten mit verschiedenen Aufgaben:

- Die **Prüfsumme** (`sha256` im Eintrag) schützt gegen beschädigte oder ausgetauschte **Downloads**: Das `.zip` muss genau dem Hash im Index entsprechen.
- Die **Signatur** schützt gegen einen manipulierten **Index**. Wer nur `index.json` ändern kann (z.B. mit einem gekaperten GitHub-Konto), könnte sonst Download-URL und Hash zusammen austauschen. Der Standard-Katalog liefert deshalb neben `index.json` eine `index.json.sig`: Base64 einer ECDSA-P-256-Signatur (SHA-256, IEEE P1363) über die **exakten Bytes** der `index.json`. Den öffentlichen Schlüssel bringt Mega Hub mit (`CatalogSignature.PublicKeyPem`). Ohne gültige Signatur ist die Standardquelle „nicht vertrauenswürdig“, und nichts daraus ist installierbar. Bei einer kaputten Signatur zeigt Mega Hub den zuletzt geprüften gespeicherten Stand.
- Die **Seriennummer** (`serial`, ganze Zahl im Index, von der Signatur mit abgedeckt) schützt gegen das **Zurückspielen eines alten, echt signierten Index** (zum Beispiel über einen Proxy oder einen veralteten Cache), der die `revoked`-Liste von heute wieder entfernen würde. Sie muss bei **jeder** Änderung des Index steigen (`catalog-sign.ps1` erhöht sie vor dem Signieren selbst um 1). Mega Hub lehnt einen geladenen Index ab, dessen Nummer **kleiner** ist als die der gespeicherten Kopie derselben Adresse; es bleibt bei der gespeicherten Kopie, und die Meldung steht in der Fehlerzeile. Gleich ist erlaubt, ein fehlendes Feld zählt als 0. Jede Katalogadresse hat ihren eigenen Zähler. Geprüft wird nur, wo eine Signatur es belegt: beim Standard-Katalog und bei Katalogen mit festgehaltenem Schlüssel; bei einem unsignierten fremden Katalog nicht (eine erfundene hohe Nummer würde ihn sonst dauerhaft aussperren). Ältere Mega-Hub-Versionen (0.5) ignorieren das Feld.
- **Eigene Quellen** dürfen unsigniert sein. Sie sind mit „⚠ unsignierte Quelle“ markiert, und der Installationsdialog weist darauf hin. Eine Signatur mit dem Schlüssel des Projekts ist dort nicht nötig.

Grenzen: Die Signatur beweist, dass der Index vom Schlüsselinhaber stammt, nicht dass das Plugin harmlos ist. Wer den privaten Schlüssel hat, kann alles freigeben. Er liegt nur im Benutzerprofil des Maintainers (DPAPI-geschützt, `%USERPROFILE%\.couchhud\catalog-signing.key`), nie im Repository. Zusätzlich empfohlen: Branch-Protection auf `main` im Katalog-Repo (Pull Requests mit Review, keine Force-Pushes).

Index signieren (PowerShell 7), nachdem er endgültig ist, ohne spätere Änderung von Bytes oder Zeilenenden (im Katalog-Repo `catalog/* -text` in `.gitattributes`):

```powershell
.\scripts\catalog-sign.ps1 -Index .\catalog\index.json   # erhöht `serial` um 1 und erzeugt index.json.sig; legt nie einen Schlüssel an und lehnt einen Schlüssel ab, der nicht der in CouchHUD eingebaute ist (nur `-GenerateKey` erzeugt einen; die Umsetzung steckt in sign-file.ps1, das auch die Release-Prüfsummen signiert). Signiert werden `couchhud-catalog-v1` + Zeilenumbruch + die Bytes der Datei; dieselbe Signatur gilt weder für Release-Prüfsummen noch umgekehrt (Domain-Trennung). Signaturen im alten Format (nur die Bytes) werden nicht mehr akzeptiert: ältere CouchHUD-Versionen lehnen den neu signierten Index ab
```

### Ablauf und Sicherheit

- **Laden**: 10 Sekunden Zeitlimit, höchstens 1 MB. Der letzte erfolgreiche Index jeder Quelle liegt in `%APPDATA%\CouchHud\catalog-cache.json`; ohne Netz zeigt der Katalog diesen Stand („Offline“).
- **Installieren** (A auf einem Eintrag): erst ein Hinweis, dass **Plugins mit deinen Rechten laufen** und nur aus vertrauenswürdigen Quellen kommen sollen, bestätigt mit A. Dann Download (höchstens 200 MB, Abbruch, wenn 60 Sekunden lang keine Daten kommen), **SHA-256-Pflichtprüfung**, danach der gewöhnliche Installer. Der Installer prüft alle Pfade im Archiv (kein `../`, keine absoluten Pfade oder Laufwerke), Größe und Dateianzahl und packt erst in einen Temp-Ordner neben dem Ziel. Die alte Version wird nur beiseite geschoben und kommt bei einem Fehler zurück. Ein Archiv, das weniger Größe angibt, als es enthält, wird abgelehnt.
- **Offline installieren**: Ist die Quelle nicht erreichbar, zeigt der Katalog den gespeicherten Stand. Installieren ist trotzdem möglich, sobald der Download selbst erreichbar ist. Die Signatur des gespeicherten Standes wird beim Laden erneut geprüft.
- **Nur https**. Katalog, Downloads und Bilder werden über http, UNC-Pfade (`file://server/…`) oder lokale Pfade abgelehnt (außer mit dem Entwickler-Schalter, dann nur lokale Laufwerke und Loopback).
- **Quellen**: Updates kommen nur aus der Quelle, aus der ein Paket installiert wurde. Mitgelieferte und von Hand installierte Plugins sind nur aus der Standardquelle aktualisierbar. Gleiche Ordner in mehreren Quellen: die erste Quelle gewinnt. Dieselbe `id` mit anderem Ordner in einer späteren Quelle ist ein Konflikt und nicht installierbar.
- **Mitgelieferte Plugins**: Eine Kopie in `%APPDATA%\CouchHud\plugins` ersetzt das mitgelieferte Plugin nur, wenn sie eine DLL enthält und ihre Version in `plugin.json` nicht älter ist. Sonst lädt das mitgelieferte, und das Log sagt warum.
- **Neustart** (ab 0.7 nur noch, wo er nötig ist, siehe [Plugins ohne Neustart](#plugins-ohne-neustart)): Ein **neu installiertes** Plugin startet sofort. Ein **Update** wird abgelegt (`%APPDATA%\CouchHud\pending`) und beim nächsten Start eingespielt, bevor Plugins geladen werden, weil die DLLs des laufenden Plugins belegt sind. Skins sind sofort nutzbar. Ein Update eines mitgelieferten Plugins (z.B. Wand) legt eine Kopie in `%APPDATA%\CouchHud\plugins` an, die dann die mitgelieferte ersetzt.
- **Update-Hinweis**: Fünf Minuten nach dem Start und dann alle 12 Stunden prüft Mega Hub im Hintergrund die Kataloge, nie während ein Spiel läuft. Neue Updates erscheinen einmalig in den Benachrichtigungen und als Abschnitt *Plugins & Skins* im Tab Downloads. Abschalten: `"CatalogUpdateCheck": false`.
- **Automatisch aktualisieren** (Einstellungen › Datenschutz, `"PluginAutoUpdate"`, standardmäßig aus): Mit dem Schalter lädt Mega Hub beim selben Durchgang Updates von selbst, aber nur für Pakete aus dem offiziellen Katalog, bei Plugins nur mit einer Aussage, die Mega Hub selbst signiert hat; nur strikt neuere Versionen, die zu dieser Mega-Hub-Version und SDK-Stufe passen, und nur, wenn die neue Version dieselben oder weniger Fähigkeiten (`capabilities`) verlangt als die installierte (verglichen wird die signierte Aussage, `network:internet` gilt als mehr als `network:lan`). Deklariert die `plugin.json` im Paket mehr als die Aussage, wird nichts installiert. Das Plugin wird wie ein Update von Hand geprüft und abgelegt (`pending`) und ist nach dem Neustart aktiv; eine Benachrichtigung führt zu Downloads. Neue Fähigkeiten, fremde Kataloge und Einträge ohne prüfbare Signatur fragen weiter mit Warnung und Eltern-Code. Nie während ein Spiel läuft, bei Offline oder ausgeschalteter Update-Suche.
- Installiert wurde was? `%APPDATA%\CouchHud\catalog-installed.json` merkt sich Version und Ordner (Skins haben keine eigene Versionsangabe).

### Katalog testen (Entwickler-Schalter)

Mit `"CatalogAllowLocal": true` in der `config.json` akzeptiert Mega Hub zusätzlich `file:///…`-Quellen und `http://127.0.0.1`/`localhost` für Katalog und Downloads. Die Prüfsumme bleibt Pflicht. Nur zum Testen einschalten, Standard ist `false`:

```json
"CatalogAllowLocal": true,
"CatalogSources": [ "file:///C:/Temp/katalog/index.json" ]
```

### Eigenen Katalog betreiben

Ein GitHub-Repository mit einer `index.json` reicht (`raw.githubusercontent.com`). Die `.zip`-Dateien hängen als Release-Assets daran. Pull Requests für den Standard-Katalog sind willkommen: Eintrag in `index.json`, Quellcode des Plugins öffentlich verlinken.
