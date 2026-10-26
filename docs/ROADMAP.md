# CouchHUD Roadmap

Goal: the living-room PC feels like a console. Switch on, sign in automatically, CouchHUD is there, and **everything** you need at the TV works with the controller and from this interface. No reaching for a wireless keyboard, no trip to the desktop unless you deliberately want PC mode.

## Guard rails

These rules apply to every feature and every plugin:

1. **Everything through the GUI.** What can be configured can be configured in CouchHUD. `config.json` is storage, not a user interface.
2. **Controller first, mouse and keyboard optional.** Every screen is fully usable with the pad. Text input goes through the built-in on-screen keyboard.
3. **Seamless and full screen.** No flicker, no desktop in between, no window borders, no foreign dialogs where avoidable.
4. **Third-party software belongs in plugins.** FanControl, SignalRGB, CaseHUD, UniGetUI, Kodi … never go into the core. The core provides the extension points.
5. **The shell never runs as admin.** What needs admin rights is done by a service through a narrowly defined interface. UAC appears exactly once, at setup.
6. **A broken plugin must never kill the shell.** CouchHUD is the shell; a crash would otherwise mean a black screen.

## Target architecture

```
┌────────────────────────── User session ───────────────────────────────┐
│  CouchHud.Shell (WPF + WebView2)                                      │
│   ├─ UI (ui/): screens, skins, on-screen keyboard, settings renderer  │
│   ├─ Core: library, config, artwork, skins                            │
│   └─ Plugin host ── plugins (each in its own load context)            │
│        e.g. SignalRGB, CaseHUD, FanControl, RSS, Kodi, winget (user)  │
└───────────────▲───────────────────────────────────────────────────────┘
                │ Named pipe, only for the signed-in user (ACL),
                │ fixed list of allowed operations
┌───────────────┴──────── CouchHud.Service (LocalSystem) ───────────────┐
│  Windows Update · winget (machine scope) · self-update                │
│  privileged plugin operations (e.g. reload FanControl)                │
└───────────────────────────────────────────────────────────────────────┘
```

### What a plugin should be able to do (SDK v1)

| Extension point | Purpose | Example |
|---|---|---|
| `ILibraryProvider` *(exists)* | Entries for the library | Kodi movies, GOG games |
| **Settings page** | Settings, described declaratively (toggle, slider, choice, text, folder, button) and rendered by CouchHUD in the current skin | SignalRGB: default effect |
| **Widgets** | Tiles/cards for Home and the quick menu | CPU/GPU temperature, news carousel |
| **Quick actions** | Entries in the Guide menu | "Lights off", "Quiet fans" |
| **Pages** | Own full-screen screens, declarative or as a custom HTML panel | RSS reader, media library |
| **Events** | React to `GameStarted`, `GameStopped`, `PcModeEntered`, `Idle`, `Sleep`, `Wake`, `HeadphonesConnected` | Different fan curve per game |
| **Notifications** | Toasts in CouchHUD | "3 updates available" |
| **Background tasks** | Periodic work with a lifecycle | Refresh feeds |
| **Privileged operations** | Plugin defines an operation, the service executes it (approved once by the user) | Switch FanControl profile |

**Declarative before HTML**: when a plugin describes its UI as a data model (list, card, slider …), it looks right in every skin and is automatically controller-operable. Custom HTML panels exist as an escape hatch for special cases.

---

## Milestones

The order follows the dependencies: first the foundation everything else builds on (settings UI, keyboard, mouse), then the console basics, then the plugin SDK (tested on the RSS reader), then the service for everything with admin rights, and only after that the hardware plugins, because e.g. FanControl runs as admin.

### Status (October 2026)

| Milestone | Done | Open |
|---|---|---|
| M1 Foundation | Settings center (simple/expert, one-button setup), on-screen keyboard (with phone input via QR), file browser, folder scan and watched game folders, edit/hide entries, search/sort/favorites filter, mouse (cartoon pointer), controller as mouse (for logins and dialogs), boot screen with logo, artwork selection, collections, game-start transition | Import of Steam categories into collections |
| M2 Console basics | Guide menu, **tapping the Guide button brings CouchHUD over the running game, game keeps running (optionally paused)**, quit and force-quit game, play time, audio incl. auto switching, controller battery, controller de-duplication (virtual pads), Bluetooth, **per-app volume**, **Wi-Fi** (Settings › Network), display (resolution, Hz, HDR), night mode, game profiles, notification center, power-off timer, task manager, **screensaver against OLED burn-in**, **foreground guard** ("CouchHUD stays in front") | Real-hardware testing |
| M2b Save games & installing | Steam downloads with progress, **save games** (Ludusavi manifest: back up, restore, safety backup, backup before uninstalling) | Registry saves, ISO/setup by controller; installing not-installed store games and automatic backup after a game ends: see plan for 0.5 |
| M2c Design | Clear logos, background trailers, **artwork from local sources with cache** (Epic, Xbox, Ubisoft caches, Steam library, own banner as a fallback), colors from artwork, transitions, boot animation, rumble, performance display, captures gallery, **sounds per skin, background music**, **HDR mode**, logo and app icon, parallax/tilt, "Reduce animations" | – |
| M3 Plugins & news | **SDK v1** (pages, settings, quick actions, tools, events, notifications, language), plugin and skin management with .zip install, Wand plugin, news tab, docs for the community, **plugin catalog** (JSON index on GitHub, signed, mandatory SHA-256) with install and update notice, **catalog repo `kewe87/CouchHUD-Catalog` with real entries (Wand, Synthwave)**, the example index in the repo mirrors the real one | Hot reload of plugins (updates without restart) |
| M3b AI & MCP | Assistant (Anthropic, OpenAI), MCP server with confirmation on the TV, plugin tools | MCP resources, voice input |
| M4 Updates & services | Windows Update, winget, couch/PC profiles for autostart and services (scheduled task instead of a service), restart into PC mode, **game clients start quietly in couch mode**, **self-update** (GitHub releases, signed checksums, silent install) | CouchHud.Service for Windows Update without UAC (plan for 0.5), UniGetUI, pre-download self-update in the background |
| M6 Media & smart home | Home Assistant, weather, phone as remote and keyboard (also for login windows), more game sources (GOG, Ubisoft, Xbox) | Kodi/Jellyfin plugins, games from the EA app and Battle.net in the library (the clients already start along), game-streaming status |
| M7 Release | **Couch lock** (controller code or Windows Hello), guard process, Task Manager block, automatic sign-in (password checked, stored by Windows as an LSA secret), **emergency mode** (Ctrl+Shift or LB+RB+View, crash counter; two steps: first Windows lock, shell and auto sign-in go away only after Windows sign-in), **parental controls**, **installer** (Inno Setup, WebView2 check, uninstall resets Windows changes), **one-button setup**, **English** (interface, host, tray, installer), signed release checksums, **shell without Explorer**, **helper tasks per account** (also standard accounts), **hardened couch lock**, **audit fixes** from several VM tests, **CaseHUD integration** | Authenticode-signed builds (SmartScreen), issue templates |

### Done since 0.3 (release 0.4.0)

- ✅ **Catalog repo** `kewe87/CouchHUD-Catalog` with real entries (Wand, Synthwave), signed.
- ✅ **Artwork cache**: images from local sources and the web are downloaded once and kept locally (`%LOCALAPPDATA%\CouchHud\artwork`, LRU limit); the interface never loads from the web itself.
- ✅ **CaseHUD integration** (Settings › CaseHUD): installation from signed GitHub releases, start, control through the local API, game reporting to CaseHUD ([CASEHUD.md](CASEHUD.md)). It is a built-in integration in the host, not a plugin; CaseHUD itself remains a separate program.
- ✅ **Couch lock hardened** (see below, item 11), **emergency mode in two steps** (first Windows lock, then removal of shell and auto sign-in).
- ✅ **Audit fixes** from several review and VM test rounds (parental controls, phone remote, auto sign-in, recovery, own firewall rule name "CouchHUD Remote (CouchHUD)", plugin approval by hash).
- ✅ **Shell without Explorer**: CouchHUD starts as the shell without Explorer running along; the desktop comes in PC mode or after emergency mode.
- ✅ **Helper tasks per account**: the one-time setup creates fixed tasks per Windows account, also for standard accounts.

### Plan for 0.5

In this order:

1. **VPN services**, first **NordVPN**: switch VPN on and off in the Guide menu and settings, choose country or location, see status (connected, location, server). Particularly interesting for streaming. Integration through the command line or interface of the installed VPN app (no VPN client of our own in CouchHUD, no credentials in CouchHUD); other providers (e.g. ProtonVPN, Surfshark, WireGuard profiles) through the same interface or as a plugin.
2. **Install not-installed games by controller.** Show not-installed games from Steam/Epic as their own row and trigger installation, show progress (see M2b). Plus a **central Downloads tab** like on PlayStation: everything currently downloading or installing in one place (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Microsoft Store, Windows updates, app updates, CouchHUD updates, plugins and CaseHUD), with progress and a notice when something finishes. For clients without an official interface only as far as progress can be read reliably from local data.
3. **TV control** via HDMI-CEC or the TV's network API: power on/off, choose input, volume.
4. **"Continue playing" row** on Home with play time and progress, plus an **automatic save-game backup after a game ends** (builds on the save games from M2b).
5. **Per-person profiles**: own favorites, collections, play-time rules and settings per person instead of just per Windows account.
6. **Smart home with only what you need.** After connecting, the tab no longer shows all entities (over a thousand sensors in large installations), only selected ones: a selection page by areas (HA areas), type (light, scene, switch, media player, climate, sensor) and search, plus sensible suggestions (scenes, lights, media players of the areas). An empty selection shows an entry point to the selection instead of a list.
7. **Home Assistant without typing a token:** "Sign in with Home Assistant" opens the HA sign-in in the built-in browser (HA's OAuth flow), and CouchHUD gets access itself. Until then, tokens and API keys have "Paste from PC" (clipboard) and Ctrl+V.

CouchHud.Service (Windows service for all admin operations) was brought forward and comes with 0.4.1.

### Next

Sorted by everyday usefulness. Done is ticked off, open says so honestly:

1. **Real-hardware testing (open).** Almost everything since v0.3 has only been compiled by CI, checked in the browser preview and in a Hyper-V test VM, not on a real couch PC at a TV. Especially check: Guide tap over exclusive-fullscreen games, pausing (NtSuspendProcess) with various games, guard and lock, foreground guard with all game clients, parental-control overlay over games, display off and waking by controller, auto sign-in dialog with the controller mouse, controller de-duplication with real Steam Input pads.
2. ✅ **Installer** (Inno Setup, `C:\Program Files\CouchHud`, WebView2 check, Start menu, uninstall restores shell and auto sign-in).
3. ✅ **First-time setup by controller**: the console button *Set up CouchHUD as a console* (overview, run, result list, way back).
4. ✅ **Self-update** (still to be tested at the TV): GitHub releases API with ETag, Updates page with release notes, download only from github.com over https, mandatory signature of the checksum file (`scripts/sign-release.ps1`), silent install with restart also as shell. Open: Authenticode signature of the setup, pre-download the update in the background.
5. ✅ **Per-app volume** and **Wi-Fi**.
6. ✅ **Plugin catalog** (still to be tested at the TV): signed JSON index on GitHub, install and updates straight from the plugin page. The repo `kewe87/CouchHUD-Catalog` exists with real entries (Wand, Synthwave).
7. ✅ **Save games** (Ludusavi, see M2b). Open: registry saves, cloud folder as a target; **not-installed store games** and the automatic backup after a game ends are in the plan for 0.5.
8. ✅ **Parental controls** (still to be tested at the TV): daily play-time limit (weekend separate), time window, per-game limit, warning 15 and 5 minutes before, "Time is up" with save-and-quit or extension by parent code, blocked games and age limit, protection against a changed system clock, also for games started without CouchHUD. Open: games without a recognizable process (URL start) cannot be measured; protection only against kids at the controller (file access to the user folder bypasses it); per-child profiles.
9. ✅ **English interface** (still to be tested at the TV): English and German in UI, host messages, tray and installer; Steam descriptions in the UI language; Settings › Info › Language (Automatic follows the Windows display language), takes effect immediately. Texts and terms: [i18n.md](i18n.md), check `scripts/i18n-check.mjs`. Open: more languages.
10. **Signed builds (open).** The release checksums are signed, but the setup itself is not Authenticode-signed, which is why SmartScreen warns. Needs a code-signing certificate.
11. ✅ **Hardening of the couch lock** (checked in several VM tests, still to be tested at the TV): only games without unlocking, allow list and list of clear non-games for the foreground, file and print dialogs closed, Windows policies and accessibility shortcuts only while locked and reversible, browser only unlocked and without dialogs, password requirement for Windows sign-in as the lock (controller code with a warning, auto sign-in dropped without a password). The lock stays as strong as the Windows password and protects only against kids and guests at the controller, not against file access, boot media or admin rights; BitLocker or device encryption is recommended (note in the one-button setup).
12. ✅ **Steam config patch ("package C", checked in the VM, still to be tested at the TV).** Steam does not get in CouchHUD's way in couch mode: no Big Picture start (`HKCU\Software\Valve\Steam › StartupMode` 4 → 7), the Guide button does not focus Steam (`localconfig.vdf › Controller_CheckGuideButton` = 0), no Big Picture overlay on controller (`system/EnableSCTenFootOverlayCheckNew` = 0), no pop-up when a controller connects (`system/ControllerConnectNotificationToast` = 0). All four keys were confirmed individually in a Steam client by changing a single setting in the interface and comparing the files (details in the source of `SteamGuard.cs`). Implementation: `SteamGuard` (Core) with its own backup `guard-steam.json` via `GuardSnapshot` (new: `OnlyIfUnchanged`, only what is still our value is reset) and `SteamGuardService` (Shell): Steam writes its files itself, so only while Steam is not running; if it runs, it is closed cleanly with `steam -shutdown` only to apply and only in a quiet moment (no game, no Steam window in front, no mouse mode/login, no download, no open sign-in; checked again before `-shutdown`), never by force (after 45 s without success nothing is written and it is retried with pauses; a late exit still leads to the quiet restart), written, verified after a short pause and restarted quietly. Resetting never restarts Steam; it waits until Steam is not running (the Run entry stays until the verified reset; quitting waits for nothing). If the user resets one of our values, it is not enforced again in this couch session. Checking and writing run under a cross-process mutex (`SteamGuardRunner`, tested with a fake Steam). Part of "CouchHUD stays in front", not a setting of its own. Reset on PC mode, Light mode, option off, quit, after a crash (Run entry `CouchHudReleaseSteamGuard` and next start); not on uninstall by the administrator process (VDF is deliberately forbidden there), which is handled when CouchHUD quits. Deliberately not touched: "Allow Guide button shortcut" (`SteamController_Enable_Chord`, confirmed, but it would take away Steam's Guide shortcut in games with Steam Input) and the overlay itself. Open: the Guide button itself could not be pressed without a controller (files, registry and the switches in Steam's interface were checked); whether Steam Input in a game with its own Steam input still intercepts the Guide button is to be checked at the TV.
13. **Kodi/Jellyfin, UniGetUI (open).** Media plugins (M6) and UniGetUI as a second app-update source (M4). The Windows service CouchHud.Service is in the plan for 0.5.
14. **New ideas:** hot reload of plugins; MCP resources; import Steam categories as collections; voice input on the phone; screenshot regression tests for all skins in CI (`scripts/ui-shot.mjs` already exists); more languages through community translations.

Home Assistant, weather and news are built-in integrations for now. As soon as SDK v1 can do its own screens and settings pages, they become plugins (guard rail 4).

### ✅ M0: Prototype (v0.2, done)

- Console UI with Home, Games, Apps, System
- Steam, Epic, custom entries, covers via SteamGridDB/Steam store
- Global XInput incl. Guide button, PC mode, shell registration, power
- Skins: CouchHUD, PS4 Classic, Xbox, Cyberpunk, Nova, plus custom ones
- Plugin loader with `ILibraryProvider`
- Detail page: description (Steam store), last played, size, install location, developer/genres, uninstall
- C#/.NET 10, WebView2, CI on Windows

### M1: Foundation – "Everything through the GUI"

*Without this, every later feature is half finished.*

- **Settings framework**: one renderer for declarative settings (toggle, slider, choice, text, folder picker, action). All of today's `config.json` values get a page. Plugins use the same system later.
- **On-screen keyboard** for the controller: QWERTY/QWERTZ, capitalization, special characters, shortcuts (`.com`, `@`, `www.`), triggers as cursor, Y = space, X = delete.
- **Folder browser** by controller (drives, folders, favorites), without a keyboard.
- **Library management**:
  - add/remove game directories, scan for `.exe` with suggestions (ignores installers, uninstallers, crash reporters, redistributables)
  - rename entries, hide them, classify as game/app, set launch parameters
  - **Artwork selection** (done): detail page → "Change artwork", a grid each for cover, banner and logo with all matches from SteamGridDB and Steam, own image from a file, "Automatic" as reset; stored as an override per entry in `config.json`
  - **Collections** (done): create, rename, delete, add entries via the Y menu or detail page, as a filter in Games/Apps, pin as a tile to Home. Import of Steam categories is open (Steam's `cloud-storage` format is undocumented and changes)
- **Mouse**:
  - when moved, a large, clearly visible pointer appears (skinnable), fading out after about 2 s without movement
  - hover highlights but does not move controller focus. A click selects and activates, the mouse wheel scrolls.
  - as soon as the controller is used again, the pointer disappears immediately.
- **Seamless full screen**:
  - black start without flicker: show the window only once the UI is rendered, with a boot splash in the skin's style
  - transitions at game start ("Starting game …" with artwork) until the game window is there
  - no Windows Explorer dialog where CouchHUD has its own solution

### M2: Console basics

- **Guide overlay / quick menu**: detects the running game (process tree, window in the foreground). Offers "Back to game", "Quit game" (with confirmation), volume, audio output, time, controller battery.
- **Play-time tracking**: per game and week, shown on the game card.
- **Audio**:
  - volume and mute system-wide, per-app volume (Core Audio sessions, running game first) *(exists)*
  - choose output device (TV/HDMI, headphones, soundbar, Bluetooth)
  - **automatic switching** when headphones are connected, with a toast "Output: headphones" and switching back on unplugging
  - technology: Windows Core Audio (`IMMDeviceEnumerator`, device-change events), setting the default device via `IPolicyConfig`
- **Display**: resolution, refresh rate, HDR on/off, scaling, choose screen/TV
- **Power**: standby, restart, shut down *(exists)*, plus screen off, power-off timer ("switch off in 30 min"), power plan
- **Network** *(exists)*: Wi-Fi list, connect (password via on-screen keyboard), disconnect, status, IP. Technology: `Windows.Devices.WiFi` (no admin, location access needed)
- **Bluetooth**: search, pair, disconnect devices, for controllers and headsets. Technology: `Windows.Devices.Bluetooth` / `DeviceInformationPairing`.
- **Notifications**: uniform toasts and a notification list in the quick menu
- **Controller status**: connected pads, battery level (XInput battery), player 1–4 assignment

### M2b: Save games and installing

**Save games**
- Save locations via the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) (community database from PCGamingWiki, thousands of games, Steam ID and name as keys)
- On the detail page: where the saves are, how big, when last changed
- Backup/restore at the press of a button (dated ZIP), offer an automatic backup before uninstalling
- ✅ Implemented: manifest reloaded weekly via ETag (own streaming YAML reader, ~0.5 s for 53,000 games), matching by Steam ID, otherwise normalized name (incl. alias); placeholders, wildcards (`*`, `?`, `**`) and `<storeUserId>` for Windows; backup as a ZIP with `manifest.json`, restore with an automatic safety backup, zip-slip protection and restriction to the game's save locations, rotation, lock while the game runs, backup offer before uninstalling
- Open: back up registry saves, cloud folder as a target, automatic backup after a game ends, restore into a different Windows user

**Installing games**
1. **Store games** (the most common case): show not-installed games from the Steam/Epic library as their own row and trigger installation (`steam://install/<id>`). Show progress as soon as the launcher reveals it.
2. **ISO / setup**: file browser by controller → mount the ISO (Windows built-ins) → find and start `setup.exe` or `autorun.inf`. Afterwards the new game is suggested as an entry by scanning the target folder.
3. **Portable games / archives** (e.g. GOG offline installers, emulator packages): choose folder, choose EXE, done.

**The two real hurdles:**
- **UAC**: the UAC dialog runs on the "secure desktop", where no controller works. Solution via the service from M4: it starts the setup chosen by the user elevated in the user session, without a dialog appearing.
- **Setup wizards are mouse dialogs**: for this there is a **"controller as mouse"** mode (left stick = pointer, A = click, B = right click, X = on-screen keyboard) that switches on automatically while a setup runs. It is needed for PC mode anyway.

### M2c: Design offensive ("PC Master Race")

*Goal: you switch on the TV and never want to leave. Runs in parallel to M1/M2, every item can ship on its own.*

- **Clear logos instead of text** on Home and the detail page *(groundwork in place)*, plus banners, icons and hero art from several sources (see below)
- **Trailer preview**: when focus rests on a game for a moment, the Steam trailer starts silently in the background, like on the PS5
- **Colors from the artwork**: accent color, glow and background tint are pulled live from the focused game's cover
- **Depth and motion**: parallax between background, logo and tiles, slight 3D tilt with a light reflection on the focused tile, soft page transitions (View Transitions), boot animation per skin
- **Haptics**: subtle controller rumble on focus, confirm and bump (XInput vibration, can be turned off)
- **Sound design per skin**: own sound sets instead of today's synth tones
- **Live system bar**: CPU/GPU temperature, load, FPS in the running game (sensors via CaseHUD/LHM), stylish and on request only
- **"Continue playing" card** very large with play time and progress
- **Performance budget**: 60 fps at 4K, no stutter when scrolling through large libraries (virtualization from ~300 tiles)

**Artwork sources (clear logos, banners & co.)**

| Source | What it offers | Access | Status |
|---|---|---|---|
| Steam CDN | Cover, hero, logo, header for Steam games | open | ✅ used |
| [SteamGridDB](https://www.steamgriddb.com) | Grids (cover, banner), heroes, **clear logos**, icons, also for non-Steam games and apps | free API key, API use explicitly intended | ✅ cover/hero/logo used, icons open |
| [TheGamesDB](https://thegamesdb.net) | Clear logos, banners, fanart, box art, screenshots (strong for retro/emulation) | free API key, observe the terms of use | planned (plugin) |
| [IGDB](https://www.igdb.com) | Cover, artworks, screenshots, metadata | Twitch developer app, check the terms | later (plugin) |

All sources except Steam come as plugins, so that license and key questions stay with the user.

### M3: Plugin SDK v1, plugin manager, news

- **SDK v1** with the extension points from the table above, versioned, plus an **example plugin** in the repo
- **Plugin manager in the GUI**: installed plugins, on/off, settings, updates. Installation from a catalog (a JSON index on GitHub, with mandatory SHA-256 check and update notice) or as a ZIP.
- **Stability**: plugin calls with timeout and fault isolation. A plugin that crashes repeatedly is disabled and reported.
- **RSS/news as the first plugin** (tests the SDK on a real case):
  - news carousel as a widget on Home, own reader screen with large type and images, browsable by controller
  - manage feeds in the GUI, preconfigured: [WinFuture](https://winfuture.de) (`https://static.winfuture.de/feeds/WinFuture-News-rss2.0.xml`), [Caschys Blog](https://stadt-bremerhaven.de) (`https://stadt-bremerhaven.de/feed/`)
  - open articles in the embedded reader (WebView with controller scrolling), "read" status
  - technology: `System.ServiceModel.Syndication` (RSS + Atom), cache, background fetching

### M3b: AI assistant and MCP

*Builds on the plugin SDK: AI and plugins use the same tools as the interface.*

- **Providers**: Claude (Anthropic) and OpenAI, store the API key in settings. It is stored encrypted with Windows DPAPI, never in plain text in `config.json`.
- **Assistant in full screen**: own screen and quick action in the Guide menu. Input via on-screen keyboard, suggested prompts to tap, text input from the phone (M6), optionally voice input.
- **What the AI may do**, through a fixed tool list (tool use), the same typed operations as the UI:
  - search the library, create and edit games/apps ("Add RetroArch with this path"), find and set artwork
  - change settings and skins, create scenes, configure plugins
  - scan game directories, check updates, explain info about games
- **Confirmation before every change**: the AI proposes, CouchHUD shows a preview, you press A. No shell commands, no file-system access outside the tools.
- **MCP server** for access from outside (e.g. Claude Desktop or another MCP client on the laptop)
  - off by default; when switching on, generate a token and show it as a QR code
  - Streamable HTTP, localhost only or LAN, depending on the setting
  - the same tools as the assistant, resources for library and settings, write access can be enabled individually
- **Plugins can contribute tools** (e.g. SignalRGB plugin: "set effect"), automatically available to the assistant and MCP

### M4: CouchHud.Service and updates

*This is where UAC is solved: once at setup, never again.*

- **CouchHud.Service** as a Windows service (LocalSystem), installed by the setup
  - named pipe with an ACL only for the interactive user
  - **No** "run this command" interface, but a fixed list of typed operations with checked parameters (update IDs, package IDs, profile names). Otherwise the service would be an open barn door for privilege escalation.
  - plugins can register privileged operations. They show up in settings and are approved once by the user.
- **Windows Update**: search, list with size and type (security, driver, optional), download and install with progress, notice when a restart is needed ("Now" or "On shutdown"). Technology: Windows Update Agent API (`IUpdateSession`, `IUpdateSearcher`, `IUpdateInstaller`) in the service.
- **App updates** as two plugins, merged on the Updates page:
  - **winget**: show available upgrades, update individually or all. Packages with user scope run in the user context, machine scope through the service without a UAC pop-up. Technology: winget COM API (`Microsoft.Management.Deployment`), fallback to the CLI. *Spike: winget in the SYSTEM context has limitations, we check that early.*
  - **UniGetUI**: through its local IPC API or the `unigetui` CLI, for everyone who uses UniGetUI anyway (also covers Scoop, Chocolatey, pip, npm …)
- **CouchHUD self-update** (implemented): release feed on GitHub, checksum file with signature (ECDSA like the catalog), silent installation via the setup with one UAC prompt. Open: pre-download the update in the background and apply it at the next start
- **Driver hints** (optional): show NVIDIA/AMD driver version, point to a new version

### M5: Hardware plugins

*FanControl and SignalRGB as standalone plugins, not a line of them in the core. CaseHUD is implemented as a built-in integration (no CaseHUD code in the core, only the API client).*

**CaseHUD** (LCDs, your project)
- ✅ Implemented in 0.4.0 as a built-in integration (not as a plugin), details in [CASEHUD.md](CASEHUD.md): connection through the local HTTP API, discovery via `%APPDATA%\casehud\api.json` (port + token), displays with live preview, design, brightness, rotation, mode, scenes, Guide menu entries, game reporting to CaseHUD
- Open: sensor widgets and performance overlay from CaseHUD's LHM service
- In CouchHUD: displays with live preview, choose a design per display, brightness, "fan set"
- **Sensors from CaseHUD's LHM service** we use right away: temperature/load widgets and a performance overlay in the quick menu, without a second hardware driver
- One display design per game (matches CaseHUD's own 1.2 roadmap "profiles on game start")

**SignalRGB** (lighting)
- List and apply effects through the local REST API (`http://127.0.0.1:16038/api/v1`), fallback via the URL scheme `signalrgb://effect/apply/<Name>` or `SignalRgbLauncher.exe --url=…?-silentlaunch-` (does not come to the foreground, important for seamless full screen)
- Effect gallery in the GUI, favorites, effect per game, quick actions ("Cinema mode", "Lights off")
- *Spike: check the scope of the REST API (brightness? effect parameters?). The URL interface is still an "early implementation" according to the docs.*

**FanControl** (fans)
- FanControl is closed source and runs as admin. The realistic ways:
  1. **Switch configurations**: one FanControl config per scenario ("Quiet", "Gaming", "Movie"), switching via the command line (`-c`/`--config` with full path, per the release notes from V262). Because FanControl runs as admin, this goes through the service.
  2. **Bridge plugin on the FanControl side**: FanControl has its own plugin system (C#). A small "CouchHUD Bridge" plugin *inside* FanControl could provide values and accept commands via a pipe. *Spike: check what the FanControl plugin API allows from outside.*
- In CouchHUD: current profile, switch profile, fan RPM and temperatures (via CaseHUD/LHM), profile per game

**Scenes**: only when all three are there does this really pay off. A scene bundles fan profile, RGB effect, LCD design and audio output and is activated by quick action or automatically on `GameStarted`. Examples: "Gaming", "Movie" (lights dimmed, fans quiet), "Night".

### M6: Media, smart home, more sources

- **Media center**: Kodi (JSON-RPC: library, continue watching, start), Jellyfin/Plex (library, continue watching as a Home row)
- **Streaming apps** as stylish entries: Netflix, Disney+, YouTube TV (browser in kiosk mode with controller control)
- **More game sources**: GOG Galaxy, Xbox/Game Pass, EA app, Ubisoft Connect, Battle.net, emulators (RetroArch playlists)
- **Home Assistant**: scenes and lights from the quick menu, events in both directions (e.g. game start dims the living room)
- **Phone as remote**: the UI is web anyway. An optional LAN page, paired via PIN, for text input from the phone, browsing the library, starting a game.
- **Game-streaming status**: Sunshine/Moonlight, who is connected
- Weather, calendar, "Now playing" card

### M7: Release

- **Setup** *(done: Inno Setup, WebView2 check, uninstall resets Windows changes; the service comes with CouchHud.Service)*: CouchHUD, service, .NET runtime check, WebView2 check, optional "set up as shell"
- **First-time setup by controller**: language, skin, scan libraries, audio output, choose plugins, set up **auto sign-in** *(done: enter the password once, checked with LogonUser, stored by Windows as an LSA secret; CouchHUD does not store it)*, activate shell
- **Emergency mode** *(done)*: Ctrl+Shift or LB+RB+View at start (held about 1.5 s), or three unclean exits in a row: Windows lock, then the normal desktop without the CouchHUD shell.
- Localization DE/EN *(done)*, docs, example plugins and skin template
- Signed builds (SmartScreen, open: release checksums are signed, the setup is not yet Authenticode-signed), issue templates, license, **open source**

---

## Open spikes (clarify early)

| Topic | Question | When |
|---|---|---|
| winget in the service | Does the COM API run reliably as SYSTEM? Otherwise a scheduled task in the user context with highest privileges. | before M4 |
| FanControl | Config switching via CLI during operation? What can a FanControl plugin do? | before M5 |
| SignalRGB | Scope and stability of the REST API on port 16038 | before M5 |
| Audio | `IPolicyConfig` is undocumented, check behavior on current Windows 11 | M2 |
| Focus | Reliably bringing CouchHUD back to the foreground over exclusive-fullscreen games | M2 |
| Double autostart | Does Explorer in PC mode run the Run entries a second time? If so, set Explorer's "StartupHasBeenRun" marker | M2 |
| Start setup elevated | The service starts the setup with the user's elevated token in their session, without a UAC dialog | M2b/M4 |
