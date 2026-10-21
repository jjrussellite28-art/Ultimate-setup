# Sicherheit / Security

## Deutsch

### Unterstützte Versionen

Unterstützt wird die jeweils neueste 0.x-Version.

### Sicherheitslücke melden

Bitte ausschließlich über **„Report a vulnerability“** im Reiter *Security* des Repos: <https://github.com/kewe87/CouchHUD/security/advisories/new>. Keine öffentlichen Issues und keine Mail.

Bitte angeben: CouchHUD-Version, Schritte zum Nachstellen und die Auswirkung.

### Was CouchHUD bewusst nicht verspricht

Die Couch-Sperre ist ein **Kiosk-Schutz gegen Kinder und Gäste am Controller**. Sie schützt nicht gegen jemanden mit Administratorrechten, mit Datei- oder Registry-Zugriff als derselbe Benutzer, mit Tastatur und Zeit, mit einem Startmedium oder ausgebautem Laufwerk. Dagegen helfen ein gutes Windows-Kennwort und BitLocker bzw. die Geräteverschlüsselung; CouchHUD schaltet diese nicht selbst ein. Dasselbe gilt für den Jugendschutz. Details und Grenzen: [docs/DISCLAIMER.md](docs/DISCLAIMER.md) und der Abschnitt zur Couch-Sperre in der [README](README.md#couch-sperre-und-jugendschutz). Berichte, die nur diese dokumentierten Grenzen beschreiben, sind keine Sicherheitslücken.

### Der CouchHUD-Dienst

Seit 0.4.1 erledigt ein Windows-Dienst (`CouchHud.Service`, SYSTEM) alle Administrator-Aufgaben; CouchHUD selbst läuft nie mit Administratorrechten. Grenzen, die bewusst so sind (keine Sicherheitslücken):
- Der Dienst prüft Couch- und Eltern-Code selbst und kennt den Sperrmodus jedes Kontos. Freigaben sind an Konto, Windows-Anmeldung und Sitzung gebunden, enden beim Sperren, Trennen und Abmelden und zählen nicht in einer gesperrten Sitzung; Freigaben von Eltern oder Administrator gelten nur für die benannte Aktion, einmal, 30 Sekunden. Ein Programm, das schon als derselbe Benutzer läuft, kann den Controller mitlesen oder Freigaben aus dem Speicher von CouchHUD holen.
- Die einmalige Übernahme von Code-Hash und Sperrmodus aus der Benutzer-Konfiguration vertraut dem ersten Stand (Trust on First Use): Der Dienst kann nicht erkennen, ob sie vorher verändert wurden. Er nimmt sie nur einmal je Konto, nur im Format von CouchHUD, nie über einen vorhandenen Code und nie schwächer als der Modus, den er kennt.
- Das Kennwort eines Administrators geht nie durch CouchHUD: Eine Administrator-Bestätigung läuft über die Benutzerkontensteuerung (`sc.exe control CouchHud <Zahl>`, nur Administratoren dürfen dem Dienst diese Steuerzahl schicken). Das Kennwort der automatischen Anmeldung und die Code-Tasten sind dagegen unveränderliche .NET-Zeichenketten und bleiben bis zur Speicherbereinigung im Speicher. Nichts davon wird geloggt.
- Die UAC-Abfrage zeigt nur `sc.exe` und die Steuerzahl, nicht die Aktion. Eine Bestätigung bindet die **Operation** (z. B. „automatische Anmeldung einrichten“), **nicht ihre Argumente** (welches Kennwort, welche Ports, welcher Code): Die Argumente kommen danach mit der Operation vom Konto an der Couch. Je Sitzung ist nur eine Anfrage offen (eine zweite wird abgewiesen, bis die erste abgeholt oder nach 60 Sekunden verfallen ist), eine Steuerzahl wird für die Sitzung nicht gleich wieder vergeben, und der Dienst prüft beim Start seine eigene Zugriffsliste: Dürfen andere als Administratoren und SYSTEM ihm Steuerbefehle schicken, ignoriert er alle Bestätigungen und meldet einen Alarm. Ein Programm, das schon als derselbe Benutzer läuft, kann aber vor CouchHUD eine eigene Anfrage stellen; CouchHUD sagt vor der Abfrage, was bestätigt wird.
- Die Windows-Anmeldung mit dem Konto-Kennwort führt auch ohne laufenden Dienst zum Desktop (Notausgang); eine Anmeldung ergibt im Dienst keine Freigabe.
- Setzen Eltern oder ein Administrator einen einzelnen Code (Couch- oder Eltern-Code), entfernt der Dienst den jeweils anderen, außer beide werden im selben Schritt gesetzt; CouchHUD setzt mit Jugendschutz immer beide, der Fall tritt also nur bei direkten Aufrufen der Pipe auf.
- „Dienst reparieren“ startet nach einer UAC-Bestätigung `cmd.exe` mit dem festen Skript `repair-service.cmd` aus dem Programmordner; wer die UAC-Abfrage bestätigt, sollte Programm und Befehlszeile prüfen ([Handbuch](docs/GUIDE.md#notausgang)).
- Was ein Administrator, ein Startmedium oder Dateizugriff als SYSTEM kann, schützt der Dienst nicht.

Berichte zu Rechteausweitung über den Dienst (Aktionen für fremde Konten, Freigaben ohne Code oder über Sitzungsgrenzen, fremde Programme unter dem Pipe-Namen) sind ausdrücklich willkommen.

### Signaturen

Self-Update und Plugin-Katalog sind mit ECDSA P-256 signiert. Fingerabdruck (SHA-256 über den öffentlichen Schlüssel, SPKI-DER) des Schlüssels für Self-Update und Katalog:

`247bba62849ddfeab8749a38488791807bffa33c72347ea20931870dbbb7709f`

Die Releases von CaseHUD (`kewe87/CaseHUD`) sind ebenfalls signiert, Fingerabdruck des Schlüssels:

`cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf`

Siehe [docs/PLUGINS.md](docs/PLUGINS.md) und [docs/CASEHUD.md](docs/CASEHUD.md).

## English

### Supported versions

The latest 0.x release is supported.

### Reporting a vulnerability

Please use **"Report a vulnerability"** on the repo's *Security* tab only: <https://github.com/kewe87/CouchHUD/security/advisories/new>. No public issues and no email.

Please include: CouchHUD version, steps to reproduce, and the impact.

### What CouchHUD deliberately does not promise

The couch lock is **kiosk protection against children and guests at the controller**. It does not protect against someone with administrator rights, file or registry access as the same user, a keyboard and time, boot media, or a removed drive. A strong Windows password and BitLocker or device encryption help there; CouchHUD does not enable them itself. The same applies to parental controls. Details and limits: [docs/DISCLAIMER.md](docs/DISCLAIMER.md) and the couch lock section of the [README](README.md#couch-lock-and-parental-controls). Reports that only describe these documented limits are not vulnerabilities.

### The CouchHUD service

Since 0.4.1 a Windows service (`CouchHud.Service`, SYSTEM) does all administrator tasks; CouchHUD itself never runs with administrator rights. Limits that are deliberate (not vulnerabilities):
- The service checks the couch and parents' codes itself and knows each account's lock mode. Grants are bound to the account, the Windows sign-in and the session, end on lock, disconnect and sign-out and do not count in a locked session; a parent's or an administrator's grant counts only for the named action, once, for 30 seconds. A program that already runs as the same user can read the controller or take grants from CouchHUD's memory.
- The one-time takeover of the code hash and the lock mode from the user's configuration trusts what it finds first (trust on first use): the service cannot tell whether they were changed before. It takes them only once per account, only in CouchHUD's format, never over an existing code and never weaker than the mode it knows.
- An administrator's password never passes through CouchHUD: an administrator's approval goes through User Account Control (`sc.exe control CouchHud <number>`; only administrators may send the service that control code). The automatic sign-in password and the code buttons, however, are immutable .NET strings and stay in memory until the garbage collector removes them. None of it is logged.
- The UAC prompt shows only `sc.exe` and the control number, not the action. An approval binds the **operation** (e.g. "set up the automatic sign-in"), **not its arguments** (which password, which ports, which code): the arguments come afterwards with the operation from the account at the couch. Only one request is open per session (a second one is refused until the first is picked up or expires after 60 seconds), a control number is not handed to the same session again right away, and at every start the service checks its own access list: if anyone but administrators and SYSTEM may send it controls, it ignores all approvals and raises an alarm. A program that already runs as the same user can still place a request of its own before CouchHUD does; CouchHUD says before the prompt what is being approved.
- The Windows sign-in with the account password reaches the desktop even when the service is not running (emergency exit); a sign-in gives no grant in the service.
- When a parent or an administrator sets a single code (couch or parents'), the service removes the other kind unless both are set in the same step; with parental controls CouchHUD always sets both, so this only happens with direct calls to the pipe.
- "Repair service" starts `cmd.exe` with the fixed script `repair-service.cmd` from the program folder after a UAC approval; whoever approves the UAC prompt should check program and command line ([guide](docs/GUIDE.en.md#emergency-exit)).
- The service does not protect against what an administrator, boot media or file access as SYSTEM can do.

Reports of privilege escalation through the service (actions for other accounts, grants without a code or across session boundaries, other programs under the pipe's name) are explicitly welcome.

### Signatures

Self-update and the plugin catalog are signed with ECDSA P-256. Fingerprint (SHA-256 over the public key, SPKI DER) of the self-update and catalog key:

`247bba62849ddfeab8749a38488791807bffa33c72347ea20931870dbbb7709f`

CaseHUD releases (`kewe87/CaseHUD`) are signed too; key fingerprint:

`cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf`

See [docs/PLUGINS.md](docs/PLUGINS.md) and [docs/CASEHUD.md](docs/CASEHUD.md).
