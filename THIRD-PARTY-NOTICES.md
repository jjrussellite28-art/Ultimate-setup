# Third-party notices and release review

This inventory is a starting point, not a complete license audit of every transitive dependency or optional plugin.
Keep the exact dependency license texts and required notices with any distributed copies.

| Component | Inherited notice / required review |
| --- | --- |
| CouchHUD | Copyright © 2026 kewe87 and contributors; GPL v3. Full project LICENSE retained. |
| .NET libraries; System.ServiceProcess.ServiceController; System.Diagnostics.EventLog | MIT/.NET Foundation notices; collect exact notices for the versions actually bundled, including the self-contained runtime. |
| QRCoder | MIT; collect its copyright and full license text for the bundled version. |
| Microsoft WebView2 SDK/runtime | Microsoft terms apply; verify redistribution terms for the SDK and downloaded bootstrapper/runtime. |
| Inno Setup | Inno Setup license; preserve required notices for the compiler/runtime used. |
| Ludusavi save-location manifest / PCGamingWiki-derived data | Upstream identifies CC BY-NC-SA 3.0. Downloaded at runtime, not bundled. Resolve commercial permission or replace/disable the integration before a commercial release. Runtime downloading alone does not resolve the commercial-use question. |
| Open-Meteo, Steam artwork, SteamGridDB and other APIs | Check current service/commercial terms and attribution for the actual enabled features; some require users' own keys. |
| Optional catalog plugins and skins | Separately downloaded and separately licensed. Review each item you distribute or promote as included. The inherited catalog and its signing key belong to upstream. |
| Logos, banners, screenshots, sounds and fonts | Inventory actual shipped files and rights. Replace upstream product branding before release. The selected Mega Hub logo is AI-generated using Bing Image Creator; its original and provenance are in ui/assets/MEGA-HUB-LOGO.md. This attribution does not assert exclusive rights or legal clearance. |

The upstream README is preserved in docs/UPSTREAM-README.md. Do not delete original author notices
or treat this inventory as permission to redistribute assets under a different license.
