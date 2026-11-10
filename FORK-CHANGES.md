# Fork changes

## 2026-11-04 — release preparation (unreleased)

- Identify Mega Hub as an independent GPL-3.0-only fork of CouchHUD and retain upstream credits.
- Use the fork's repository for app update discovery, project links, installer publisher and release tooling defaults.
- Change visible UI text and product metadata to Mega Hub; retain internal compatibility identifiers.
- Include LICENSE, credits, source instructions and third-party notices in published app output.
- Prepare a source archive from the exact build commit and attach it to draft release preparation.
- Disable tag-driven public release publication: release output remains a draft.

The GPL license and inherited authors' notices are retained. This list describes preparation changes,
not a released feature upgrade. See docs/RELEASE-CHECKLIST.md for unfinished work.

## 2026-11-04 — Mega Hub name and supplied artwork (unreleased)

- Rename the visible product from Ultimate Setup to Mega Hub; the repository URL and internal compatibility identifiers remain unchanged.
- Add the user-supplied Bing Image Creator logo unchanged, credit its generation source, and use it in the README, startup screen, setup welcome and Windows application icon.
- Retain upstream credits and GPL v3 without claiming exclusive rights in the AI-generated artwork.

## 2026-11-10 — own artwork and logo (unreleased)

- Replace the inherited CouchHUD artwork (installer wizard pictures, icons, logo, banner, social preview, phone-page icon) so the fork cannot be mistaken for an official CouchHUD release, as the CouchHUD author asked.
- Replace the earlier Bing logo with one ChatGPT-made logo for every platform (no Windows, Android or Apple mark); originals and provenance in ui/assets/MEGA-HUB-LOGO.md.
- Use the wide Mega Hub picture as the startup-screen background.
- Own signing key and own catalog are still to do (docs/RELEASE-CHECKLIST.md).

