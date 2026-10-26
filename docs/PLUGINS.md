# Plugins for CouchHUD

Plugins extend CouchHUD with new game sources, pages, settings, entries in the Guide menu and tools for the AI assistant. They are normal .NET class libraries against `CouchHud.Sdk`.

The example in the repository is **[plugins/CouchHud.Plugin.Wand](../plugins/CouchHud.Plugin.Wand/WandPlugin.cs)**: one file that uses settings, a page, a quick action, a tool and events.

## Your own plugin in five minutes

```powershell
dotnet new classlib -n CouchHud.Plugin.Hello -f net10.0
cd CouchHud.Plugin.Hello
# Reference the SDK (from the CouchHUD repo or the CouchHud.Sdk.dll next to CouchHud.exe)
dotnet add reference ..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj
```

In the `.csproj`, do not copy the SDK along; CouchHUD brings it itself:

```xml
<ProjectReference Include="..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
<PropertyGroup><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>
```

```csharp
using CouchHud.Sdk;

public sealed class HelloPlugin : IPlugin
{
    public string Id => "hello";

    public void Initialize(IPluginContext context)
    {
        context.RegisterQuickAction(new QuickAction
        {
            Id = "wave",
            Icon = "👋",
            Label = "Say hello",
            Run = _ => Task.FromResult<string?>("Hello from the plugin!")
        });

        context.Events.GameStarted += game => context.Notify("Have fun", game.Name);
    }
}
```

Build it and copy it to `%APPDATA%\CouchHud\plugins\CouchHud.Plugin.Hello\` (folder name = name of the DLL). Restart CouchHUD, done. Alternatively pack the folder as a `.zip` and pick it in CouchHUD under *Settings › Expert mode › Plugins & skins › Install plugin*.

## What a plugin can do (SDK v2)

| Method | Purpose |
|---|---|
| `RegisterLibraryProvider(ILibraryProvider)` | Games or apps for the library (Kodi, GOG, emulators …) |
| `RegisterSettings(params PluginSetting[])` | Settings (toggle, text, secret, choice). CouchHUD shows and stores them, read them via `context.Settings` |
| `RegisterPage(PluginPage)` | A list page with rows (`PluginRow`); A on a row calls `Activate` |
| `RegisterQuickAction(QuickAction)` | Entry in the Guide menu, e.g. "Lights off" |
| `RegisterTool(PluginTool)` | Tool for the AI assistant and MCP clients. The name becomes `plugin_<plugin>_<tool>`; a name that already exists is rejected. With `Writes = true` the user confirms on the TV first (assistant); over MCP **every** plugin tool is confirmed, and the question shows the arguments. Plugin tools do not run while the couch is locked |
| `Notify(title, body)` | Notice on the TV and in the notification list |
| `OpenUrl(url)` | Opens an http(s) address in CouchHUD's built-in browser (never in the Windows default browser), only when the couch is unlocked; `false` if refused |
| `Events` | `GameStarted`, `GameEnded` (with play time), `PcModeEntered`, `CouchModeEntered`, `LanguageChanged`, `LockChanged` (SDK 2) |
| `Locked` | **SDK 2:** `true` while the couch is locked (a guest or child at the controller). Then do not start or switch anything on your own, e.g. no autostart on `GameStarted`. Older CouchHUD versions answer `false` |
| `Language` | CouchHUD's language right now: `"de"` or `"en"` |
| `Settings` | Stored values, `Get`, `GetBool`, `Set` |
| `DataDirectory` | Your own writable folder for cache and files |

**Declarative instead of HTML**: plugins describe pages as data. CouchHUD renders them in the active skin, with controller, mouse and keyboard. Your plugin looks right in every skin without a line of CSS.

## plugin.json

Optional but recommended, lies next to the DLL:

```json
{
  "name": "Hello",
  "version": "1.0.0",
  "author": "your-name",
  "description": "Says hello",
  "homepage": "https://github.com/your-name/couchhud-hello",
  "sdk": 1
}
```

`description` is a text or per language `{ "de": "Sagt Hallo", "en": "Says hello" }`. CouchHUD shows the description in the chosen language and falls back to the other.

`sdk` is the SDK version the plugin needs. Older CouchHUD versions then do not load it at all instead of crashing. SDK 2 is backward compatible: plugins for SDK 1 keep running unchanged, `Locked` and `LockChanged` are only additions (with a default implementation; a plugin that uses them also runs on an older CouchHUD, where `Locked` is always `false`). Write `"sdk": 2` only if the plugin is not safe without them.

## Two languages

CouchHUD comes in German and English, and the language can be switched at runtime. Your plugin reads it with `context.Language` (`"de"` or `"en"`; older CouchHUD versions always report `"de"`, so the plugin keeps running).

- Texts you build **on demand** (rows of a page, `Detail` of a quick action, the return value of `Activate`) you ask for anew on every call: `context.Language == "en" ? "Open" : "Öffnen"`.
- Texts you **register once** (`PluginSetting.Label`/`Description`, `PluginPage.Title`/`Description`, `QuickAction.Label`) apply to the language at the time of registration. Register them again on `Events.LanguageChanged`: a registration with the same id (`Key`, `Id`) replaces the earlier one.
- The description of the `PluginTool`s stays English, which models understand best.
- `description` in `plugin.json` and in the catalog exists per language (see above and [Format](#format)).

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

The Wand plugin (`plugins/CouchHud.Plugin.Wand`) does exactly this.

## Rules to keep CouchHUD stable

- Every call into a plugin (pages, actions, tools) has a timeout of 15 seconds. Slow work belongs in `Task.Run` or a background task.
- `QuickAction.Detail` (the second line in the Guide menu) is queried on a background thread with a 2-second timeout, never on the UI thread. If it does not answer, the entry has no second line; while a call hangs, no new one is started. The tick of the lock and parental controls does not depend on it.
- Exceptions are caught and shown as a message. A broken plugin never cripples the shell.
- Event handlers run in the background, never on the UI thread.
- Every plugin has its own load context: your own NuGet dependencies in your own versions are allowed.
- Plugins run with the user's rights. Please do nothing the user does not expect, and store secrets only via `SettingKind.Secret`.
- **Approval:** when installing (catalog, `.zip`), CouchHUD remembers a hash of each plugin's files. A folder in `%APPDATA%\CouchHud\plugins` that nobody installed through CouchHUD, or whose files have changed (also if copied by hand!), loads only after approval (*Plugins & skins* › plugin › *Approve plugin*, with the code of the files, only when unlocked). If you build your plugin directly into the folder while developing, approve it again after every change, or install it as a `.zip`. Plugins that were already there before this feature count as approved on first start. A queued catalog update is checked against the checksum again before being applied. All files of the folder go into the hash, except `desktop.ini`, `Thumbs.db` and `*.tmp` (which Windows and programs create by themselves). The list of approved plugins (`plugin-trust.json`) carries a signature (HMAC, the same key as for `playtime.json`, bound to the Windows account with DPAPI). **What this protects against:** a program or script that only writes files, drops a foreign folder and adds to or replaces the list by hand, and against a list copied in. **What it does not:** a program running as the same account can also read the key; the approval is no substitute for antivirus. If the signature does not match or the list is unreadable, CouchHUD reports an error on the plugins, loads nothing unknown and does not overwrite the file; to start over, delete `plugin-trust.json`. After that, whatever is then in the plugin folder counts as approved at the next start (as on the very first start): check beforehand that only plugins you know are there.

## Sharing a plugin

Create a repository on GitHub with the topic `couchhud-plugin` and upload a release with the `.zip` (folder with DLL, dependencies and `plugin.json`). That is how others find it. If you want it to appear directly in CouchHUD, add it to the [catalog](#catalog).

## Catalog

CouchHUD can install and update plugins and skins from a **catalog**: *Settings › Appearance › Install new skins* (or in expert mode *Plugins & skins*, there *Catalog*) (switch between *Installed* and *Catalog* with LB/RB). A catalog is a single `index.json` somewhere on the web. Example: [catalog/index.json](../catalog/index.json).

The default catalog is at `https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json`. Your own sources go in `%APPDATA%\CouchHud\config.json`:

```json
"CatalogSources": [
  "https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json",
  "https://example.com/my-catalog/index.json"
],
"CatalogUpdateCheck": true
```

Several sources are merged; with the same `id` the first wins.

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

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique identifier, `A-Z a-z 0-9 . _ -`, up to 64 characters |
| `name` | yes | Display name |
| `type` | yes | `plugin` or `skin` |
| `version` | yes | SemVer (`1.2.3`, `1.3.0-beta.1`). The catalog shows "Update" as soon as it is higher than the installed one |
| `download` | yes | **https** address of a `.zip` (structure as for manual installation, see above and [SKINS.md](SKINS.md)) |
| `sha256` | yes | SHA-256 of the `.zip`, 64 hex characters. Without a matching checksum nothing is installed |
| `description` | no | Text or `{ "de": …, "en": … }` |
| `author`, `homepage`, `icon` | no | `homepage` and `icon` only as https |
| `minCouchHud` | no | Lowest CouchHUD version. If the running one is lower, the entry stays visible but cannot be installed |
| `folder` | no | Folder name of the package, default is the `id`. For plugins the name of the DLL and folder (`CouchHud.Plugin.Wand`) |

Entries without `id`, `name`, a valid `type` or a valid `version` are skipped. Entries without a valid checksum or with an address without https are shown but cannot be installed. A broken index (not JSON) is an error of the source; CouchHUD then shows the last stored state.

The `.zip` must contain exactly one folder named `folder` (or the files directly, in which case `folder` is used as the name). The helper `scripts\catalog-entry.ps1 -Folder <folder> -Version 1.0.0` packs the folder correctly and prints the checksum for the entry.

### Trust: checksum and signature

Two layers of protection with different jobs:

- The **checksum** (`sha256` in the entry) protects against damaged or swapped **downloads**: the `.zip` must match the hash in the index exactly.
- The **signature** protects against a tampered **index**. Whoever can only change `index.json` (e.g. with a hijacked GitHub account) could otherwise swap download URL and hash together. The default catalog therefore delivers an `index.json.sig` next to `index.json`: Base64 of an ECDSA P-256 signature (SHA-256, IEEE P1363) over the **exact bytes** of `index.json`. CouchHUD ships the public key (`CatalogSignature.PublicKeyPem`). Without a valid signature the default source is "not trusted" and nothing from it can be installed. With a broken signature CouchHUD shows the last verified stored state.
- **Your own sources** may be unsigned. They are marked "⚠ unsigned source", and the install dialog points this out. A signature with the project's key is not needed there.

Limits: the signature proves that the index comes from the key holder, not that the plugin is harmless. Whoever has the private key can approve anything. It lives only in the maintainer's user profile (DPAPI-protected, `%USERPROFILE%\.couchhud\catalog-signing.key`), never in the repository. Also recommended: branch protection on `main` in the catalog repo (pull requests with review, no force pushes).

Sign the index (PowerShell 7) once it is final, without later changes to bytes or line endings (in the catalog repo `catalog/* -text` in `.gitattributes`):

```powershell
.\scripts\catalog-sign.ps1 -Index .\catalog\index.json   # produces index.json.sig; creates the key the first time (the implementation lives in sign-file.ps1, which also signs the release checksums). What is signed is `couchhud-catalog-v1` + newline + the bytes of the file; the same signature counts neither for release checksums nor the other way round (domain separation). Signatures in the old format (bytes only) are no longer accepted: older CouchHUD versions reject the newly signed index
```

### Flow and security

- **Loading**: 10-second time limit, at most 1 MB. The last successful index of each source lies in `%APPDATA%\CouchHud\catalog-cache.json`; without a network the catalog shows that state ("Offline").
- **Installing** (A on an entry): first a notice that **plugins run with your rights** and should come only from trusted sources, confirmed with A. Then download (at most 200 MB, abort if no data arrives for 60 seconds), **mandatory SHA-256 check**, then the usual installer. The installer checks all paths in the archive (no `../`, no absolute paths or drives), size and file count, and unpacks into a temp folder next to the target first. The old version is only moved aside and comes back on an error. An archive that states less size than it contains is rejected.
- **Installing offline**: if the source is unreachable, the catalog shows the stored state. Installing is still possible as soon as the download itself is reachable. The signature of the stored state is checked again on loading.
- **https only**. Catalog, downloads and images over http, UNC paths (`file://server/…`) or local paths are rejected (except with the developer switch, then only local drives and loopback).
- **Sources**: updates come only from the source a package was installed from. Bundled and hand-installed plugins can be updated only from the default source. Same folders in several sources: the first source wins. The same `id` with a different folder in a later source is a conflict and not installable.
- **Bundled plugins**: a copy in `%APPDATA%\CouchHud\plugins` replaces the bundled plugin only if it contains a DLL and its version in `plugin.json` is not older. Otherwise the bundled one loads, and the log says why.
- **Restart**: plugin DLLs can be neither unloaded nor replaced while CouchHUD runs. New plugins are unpacked immediately and loaded at the next start. An **update** is put aside (`%APPDATA%\CouchHud\pending`) and applied at the next start, before plugins are loaded. Skins are usable immediately. An update of a bundled plugin (e.g. Wand) creates a copy in `%APPDATA%\CouchHud\plugins`, which then replaces the bundled one.
- **Update notice**: five minutes after start and then every 12 hours CouchHUD checks the catalogs in the background, never while a game runs. New updates appear once in the notifications and as the *Plugins & skins* section on the Updates page. Turn off: `"CatalogUpdateCheck": false`.
- What was installed? `%APPDATA%\CouchHud\catalog-installed.json` remembers version and folder (skins have no version of their own).

### Testing the catalog (developer switch)

With `"CatalogAllowLocal": true` in `config.json`, CouchHUD additionally accepts `file:///…` sources and `http://127.0.0.1`/`localhost` for catalog and downloads. The checksum stays mandatory. Switch it on only for testing; the default is `false`:

```json
"CatalogAllowLocal": true,
"CatalogSources": [ "file:///C:/Temp/catalog/index.json" ]
```

### Running your own catalog

A GitHub repository with an `index.json` is enough (`raw.githubusercontent.com`). The `.zip` files hang on it as release assets. Pull requests for the default catalog are welcome: an entry in `index.json`, with the plugin's source code linked publicly.
