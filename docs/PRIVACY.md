# Privacy notice

*As of October 2026, applies to CouchHUD from version 0.3*

CouchHUD is a local application. There is **no account, no telemetry, no usage statistics and no advertising**. The developers of CouchHUD receive no data from you.

## What is stored locally

| Location | Content |
|---|---|
| `%APPDATA%\CouchHud` | Settings (`config.json`: favorites, collections, play times, custom entries, bookmarks, parental-control rules), the parental-control day counter (`playtime.json`), save-game backups and the downloaded save-game manifest, caches for artwork, store info and catalog, the backup of the Windows values CouchHUD changed (`guard.json`) and of the changed Steam settings (`guard-steam.json`; the settings themselves live in Steam's file `userdata\<account>\config\localconfig.vdf` and in the registry value `HKCU\Software\Valve\Steam\StartupMode`, none of it leaves the PC and nothing about the Steam account is changed), installed plugins and skins |
| `%LOCALAPPDATA%\CouchHud` | Logs (`logs`), image cache for artwork (`artwork`, at most about 750 MB, images of current entries are always kept), profile of the interface (WebView2), profile of the browser (history, cookies, logins of the visited websites) |

API keys and tokens (SteamGridDB, Home Assistant, Anthropic, OpenAI, MCP) are stored encrypted with Windows DPAPI for your user account. The couch-lock controller code is stored only as a salted hash. CouchHUD never stores your Windows password, neither in the configuration nor in the log. Only if you set up automatic sign-in do you enter it once in CouchHUD: it is checked with Windows and stored by Windows, encrypted, as an LSA secret (like Sysinternals Autologon); CouchHUD does not keep it.

Everything can be deleted by removing the two folders.

## Which connections CouchHUD makes

CouchHUD connects only to these services, and only for the stated purpose. As with any web page request, the providers see your IP address.

| Service | When | What is transmitted |
|---|---|---|
| Steam Store (Valve; store.steampowered.com) | Description of your games (on focus, then stored on the PC) and the background search for artwork | App IDs of your Steam games. For games and apps without a Steam ID the **name** is searched in the Steam store (search term); each name at most once per 90 days (hit) or 7 days (no hit). The description is requested in the interface language (English or German, with the matching country code). At most one request at a time, at least 1.5 seconds apart |
| Steam CDN (Valve; shared.cloudflare.steamstatic.com, video.*.steamstatic.com) | Covers, banners, logos (once per image, then stored locally), screenshots and trailers (not stored) | Only the address of the image or video. If Steam has the images locally (`appcache\librarycache`), they are taken from there and not downloaded |
| Epic CDN (cdn1.epicgames.com and others) | Covers and banners of your Epic games, once per image | Only the address of the image. The addresses come from the Epic launcher's catalog cache on your PC; there is no request to an Epic API and no sign-in |
| SteamGridDB (www.steamgriddb.com and its image CDN) | Only with your own API key, in the background and in the "Change artwork" dialog | Names or IDs of your games and apps, to find artwork. At most one request at a time, at least 0.6 seconds apart |
| GitHub: self-update (api.github.com, github.com) | Every 12 hours and on "Check for updates", never during a game; can be switched off under Settings › Updates | Query for the latest CouchHUD version (ETag); on "Install" the download of setup, checksum file and signature. No data about you is transmitted |
| GitHub: plugin catalog (raw.githubusercontent.com, default `kewe87/CouchHUD-Catalog`) | When opening the catalog and in the background (after start and every 12 hours, never in a game); can be switched off | Fetching `index.json` and `index.json.sig`. On install, the download of the plugin or skin from the https address given in the index (usually GitHub). You can add further catalog sources yourself |
| GitHub (raw.githubusercontent.com, Ludusavi manifest) | When first opening a game with save games, then at most once a week | Only the fetch of the public database (~18 MB) with a version marker (ETag); your games are not transmitted, matching happens locally |
| Open-Meteo | Only if you set a weather location | Search term of the place and its approximate coordinates |
| RSS feeds (preconfigured: WinFuture, Caschys Blog) | News tab | Fetching the feeds and images |
| Home Assistant | Only if set up | Requests to your own server |
| CaseHUD (local API, 127.0.0.1) | Only if CaseHUD is installed and running; the *CaseHUD* page is open or a game is running | Control commands and status queries to the local CaseHUD (port and token are in its `api.json`), plus name and process ID of the running game so the displays can switch to the game scene (can be turned off in expert mode). Does not leave the PC |
| GitHub: CaseHUD release (api.github.com, github.com, `kewe87/CaseHUD`) | Only when opening the *CaseHUD* page (at most every 10 minutes) and on "Install" or "Update" | Query for the latest version; on "Install" the download of setup, `SHA256SUMS.txt` and `SHA256SUMS.txt.sig`. The signature is checked with the built-in CaseHUD release key (SHA-256 fingerprint `cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf`); without a valid signature and checksums nothing is installed. No data about you is transmitted |
| Anthropic or OpenAI | Only with your own API key and only when you use the assistant | Your questions and the information the assistant reads from CouchHUD for them (e.g. names and play times of your games, settings). The privacy terms of the respective provider apply. |
| Microsoft (Windows Update), winget sources | Update check and installation | As usual for Windows Update or winget |
| Microsoft (www.microsoft.com) | Only with parental controls on, when CouchHUD notices a jump in the system clock | One HTTPS request (HEAD) used only for the date in the response, to check the clock. No data is transmitted |
| Microsoft (WebView2 runtime) | Only at installation, if the runtime is missing | The setup brings the WebView2 bootstrapper, which downloads the runtime from Microsoft. Later, Windows updates the runtime itself |
| Websites in the browser | Only what you open (including links from news and plugins: they open in the built-in browser, not in the Windows default browser) | As in any browser, in its own profile (history, cookies); searches go to DuckDuckGo. Website entries for DRM streaming open in Edge on request, whose data stays with Edge |
| Game clients (Steam, Epic, …) | CouchHUD only starts them, the clients connect by themselves | Lies with the respective providers |

## Artwork: stored locally, background only, with consideration

- Every image is downloaded **once** and then lives in `%LOCALAPPDATA%\CouchHud\artwork`. The interface never loads artwork from the internet itself, only from there. Found addresses and failures (7 days) are stored too.
- Images are loaded only in the background after the library is read, **never while a game runs**. There is a rate limit per host; on "too many requests" (HTTP 429/503) the server's wait time (Retry-After) is honored, followed by exponential waiting. If a host answers repeatedly with errors, CouchHUD pauses it for several minutes up to an hour.
- Requests carry the name `CouchHUD/<version>` as user agent.
- Only complete png, jpeg, webp and gif files up to 12 MB are accepted; the content is checked, not the server's claim.
- Local sources come before any request: Steam's `librarycache`, the Epic launcher's catalog cache, package logos of Microsoft Store games, icons of the program files. If artwork is missing entirely, CouchHUD draws a banner itself (name, color), without network.
- Under Settings › Library (expert mode) you turn "Search artwork online" off (then only existing images remain) and clear the artwork cache. The switch applies to images only: descriptions and age ratings from the Steam store (needed for parental controls) are still loaded and stored on focus; trailers can be switched off separately under Appearance.
- Downloads come only from the hosts named above (Steam, Epic, SteamGridDB and their CDNs); every redirect is checked, at most 3. Connections to addresses in your own network or on this PC are rejected, even if a name points there. For this, CouchHUD does not use the Windows proxy for artwork and store queries.

## Services on the home network

- **Phone remote** (off by default): a small web server on port 8766 in the local network. Only devices paired via QR code may type and control.
- **MCP server** (off by default): port 8765, only on this PC or, on request, on the home network, with a token. Every change must be confirmed on the TV.

## Notifications

If you switch on "Mirror notifications" and allow Windows access, CouchHUD reads the Windows notifications (e.g. WhatsApp, Discord) to show them on the TV. They stay on the PC and are not stored.

## Plugins

Third-party plugins can make their own connections and store their own data. The respective plugin authors are responsible for that.
