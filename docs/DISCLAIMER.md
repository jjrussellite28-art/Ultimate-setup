# Disclaimer

*As of October 2026, applies to CouchHUD from version 0.3*

CouchHUD is free software under the [GNU General Public License v3.0](../LICENSE). It is provided free of charge and **without any warranty**, to the extent permitted by law (sections 15 and 16 of the GPL-3.0). Use is at your own risk.

## Changes to Windows

Depending on the integration level you choose and the features you switch on, CouchHUD can change Windows settings. At the **Light** level CouchHUD behaves like a normal application. At the **Full** level CouchHUD can, among other things:

- register itself as the Windows shell for your user account (instead of the desktop),
- affect sign-in (couch lock, guard process, automatic Windows sign-in: the password is entered once, checked, and stored encrypted by Windows as an LSA secret),
- block Task Manager in couch mode,
- pause selected Windows services and start autostart programs itself,
- on request, download **CaseHUD** (a separate program for case displays), verify it, install it with administrator confirmation (the setup installs a sensor service and a driver) and start it in the background,
- minimize windows of game clients and update pop-ups and take the Guide button away from the Xbox Game Bar ("CouchHUD stays in front", reset in PC mode and on quit).

Each change is described individually, with location, admin need and the way to undo it, in CouchHUD under **Settings › Console › What CouchHUD changes in Windows**. For emergencies there is recovery mode: hold **Ctrl + Shift** or **LB + RB + View** for about 1.5 seconds at start.

## Important notes

- **Automatic sign-in** means that anyone with access to the PC gets into your Windows account if no couch lock is active.
- **The couch lock is only as strong as your account's Windows password.** It keeps kids and guests at the controller away from the desktop, but it is not a security solution: whoever has a keyboard and time, boots the PC from another medium or removes the hard drive can reach your data. With automatic sign-in the password (encrypted by Windows) is stored on the PC. So enable **device encryption or BitLocker** and use a proper password. The lock also does not protect everything that is started from CouchHUD (e.g. game clients with their own browsers or file dialogs); CouchHUD keeps these clients in the background in couch mode but cannot close every path. Windows sign-in as the lock requires an account with a password; the controller code also works without one, with a clear warning (without a password anyone with a keyboard gets into the account). Automatic sign-in needs a password and is dropped otherwise. With the lock closed, CouchHUD starts only games, minimizes clearly recognized non-games (settings, Explorer, consoles, browsers), closes file and print dialogs and sets reversible Windows policies (details under *What CouchHUD changes in Windows*). This is kiosk protection against kids and guests at the controller, not a safeguard against attacks with a keyboard, boot media, a removed drive or administrator rights; for that a good password and BitLocker or device encryption are needed. CouchHUD does not enable these itself.
- **Parental controls have limits.** They protect against kids at the controller, not against someone who knows their way around Windows: whoever has file access to the user folder (settings, counters) can change or delete them (a changed time file does count the day as used up, but the rules themselves can be changed). They work only as the Windows shell with the couch lock switched on, not in Light mode and not in PC mode. Games that were not started through CouchHUD are counted only as long as CouchHUD recognizes them as a running game; entries without a recognizable program folder (web pages, some Store apps) cannot be measured and therefore start only with the parent code. Age limits are based on the Steam store's information and are not an official age rating. The parent code is the couch-lock code or the Windows password: whoever knows it can change everything.
- **Pausing a game** suspends processes. Online games can lose the connection, and anti-cheat systems may react. The feature is off by default.
- **Plugins** come from third parties. They run with the same rights as CouchHUD. Install plugins only from sources you trust. The respective authors are responsible for plugins and their consequences.
- **Cheats and mods** (e.g. via the Wand plugin) can violate games' terms of use and lead to bans, especially in online games.
- **Brand names** such as Xbox, PlayStation, Steam, Epic Games, GOG, Ubisoft, EA, Battle.net, Windows, Discord, WhatsApp and others belong to their owners. CouchHUD is not affiliated with them; the skins "PS4 Classic" and "Xbox" are imitations without original artwork.

By confirming on first start you declare that you have read these notes.
