# Mega Hub

<img src="ui/assets/mega-hub-original.jpg" alt="Mega Hub logo: a silver key with teal panels on a dark blue background" width="480">

Logo generated with Bing Image Creator and supplied by jjrussellite28-art. [Source and provenance](ui/assets/MEGA-HUB-LOGO.md).

A controller-first Windows TV interface, independently maintained by **jjrussellite28-art**.

**Development preview — no Mega Hub release is published yet.**
This is a fork of [CouchHUD](https://github.com/kewe87/CouchHUD) by **kewe87 and contributors**,
under the GNU GPL v3. It is not an official CouchHUD product and is not endorsed or supported by that project.

## What it does

The inherited application brings game launchers and a game library into a controller-driven TV interface.
It includes an optional Windows shell mode, a background service, parental controls, and plugin support.
See the [English guide](docs/GUIDE.en.md) for inherited functionality. Older guide pages and screenshots
still use CouchHUD terminology; they are being reviewed for this fork.

## Changes in this fork

This preparation introduces the Mega Hub identity, preserves upstream attribution,
points app updates and project links at this repository, packages licensing/source information,
and prevents automatic release publication. See [FORK-CHANGES.md](FORK-CHANGES.md).
These changes do not claim authorship of the inherited application or new game-launcher features.

## Downloads and support

There is currently **no approved Mega Hub installer**. Do not treat upstream CouchHUD installers
as builds of this fork. Future reviewed builds will appear only on
[this repository's releases page](https://github.com/jjrussellite28-art/Ultimate-setup/releases).
For development feedback, use [pull requests](https://github.com/jjrussellite28-art/Ultimate-setup/pulls).
Issues are currently disabled. Do not send fork support requests to CouchHUD's maintainer.
Do not post passwords, API keys, personal configuration or exploit details publicly.

## Build a development version

Windows 10 (1809+) or Windows 11 x64, .NET 10 SDK, Node.js 22.13+ and an XInput-compatible controller.

```powershell
dotnet restore CouchHud.slnx
dotnet build CouchHud.slnx -c Release
dotnet test CouchHud.slnx -c Release --no-build
dotnet run --project src/CouchHud.Shell -- --dev
```

Browser preview with mock data: `node scripts/preview-server.js`.
Installer build instructions are in `.github/workflows/build.yml` (Inno Setup 6).
Internal `CouchHud` assembly, service, data and protocol names are inherited compatibility identifiers.
**Do not install this development fork alongside or over CouchHUD**: those identifiers are not isolated yet.
Use a disposable Windows test environment for installer and shell-mode testing.

## License, source and commercial distribution

The code remains **GPL-3.0-only**. Read [LICENSE](LICENSE), [CREDITS.md](CREDITS.md),
and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
You may charge for GPL-covered copies and support, while preserving recipients' GPL rights.
Distributions must retain notices and provide the exact corresponding source and required build/install materials.
Third-party assets, data and services require their own review; GPL does not override their terms.
See [SOURCE.md](SOURCE.md) and the [release checklist](docs/RELEASE-CHECKLIST.md).

## Before public distribution

Remaining installer artwork, installer isolation, fork-owned release signing, third-party commercial-use review,
and Windows installer/shell recovery testing remain open. A README update is not certification of release readiness.
No release should be published until the checklist is complete.
