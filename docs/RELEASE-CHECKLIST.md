# Mega Hub release checklist

Status: DEVELOPMENT ONLY. No approved public or commercial release.

## Prepared in this change

- [x] Independent-fork identity and original author attribution in the README.
- [x] Keep GPL v3 license and inherited file notices.
- [x] Fork-owned app update destination and installer publisher/project links.
- [x] Include licensing/source notices with app publish output.
- [x] Produce source and build-commit information during CI; releases remain drafts.

## Required before any installer is published

- [x] Select the Mega Hub logo and retain its original files and generation source (ChatGPT; replaced the earlier Bing logo on 2026-11-10).
- [ ] Complete remaining branding variants and installer wizard artwork; refresh screenshots.
- [ ] Isolate installation ID, folders, service/pipe names, mutexes, data, registry/task/firewall identities from Mega Hub, or implement and test an explicit migration that cannot damage an upstream install.
- [ ] Configure a fork-owned release signing identity and verification key. Do not weaken signature validation or claim an upstream signature authenticates this fork.
- [ ] Decide whether to use the upstream catalog; label it accurately and verify its permitted use, or provision a fork catalog and trust key.
- [ ] Review privacy/security notices against actual fork behavior and establish a private security-reporting route; inherited documents describe upstream.
- [ ] Inventory all runtime/dependency and asset licenses, include their full required notices, and resolve the noncommercial save-location data and service terms before selling.
- [ ] Run build, automated checks, browser checks, and clean Windows install/update/uninstall tests, including shell-mode emergency recovery. Do not install a development build on the user's main Windows account.
- [ ] Verify each installer has matching source, build scripts, license/notices and any required Installation Information available alongside it.
- [ ] Review draft release notes and downloads, add hashes/signatures, and explicitly approve publication.

Do not mark these tasks complete on the basis of documentation changes alone.

## Own signing key and own catalog (asked for by the CouchHUD author)

kewe87 agreed to the fork on 2026-11-10 on the condition that Mega Hub uses its own key and its own catalog, and does
not hand out packages that look signed by CouchHUD. Status today: the key compiled into
`src/MegaHub.Core/Plugins/Catalog.cs` (`CatalogSignature.PublicKeyPem`, fingerprint `247bba62...709f`) and the official catalog
(`CatalogDefaults.IndexUrl`, `kewe87/CouchHUD-Catalog`) are still CouchHUD's. Mega Hub's own releases cannot be signed with that
key, so the built-in signed-update check and the service's setup check would refuse them (the Update button works today
only because it runs `Get-newest-Mega-Hub.ps1`).

Steps (the first one needs the owner's PC with PowerShell 7 and the .NET 10 SDK; the private key never leaves it):

- [ ] `.\scripts\sign-file.ps1 -GenerateKey` on the owner's PC; send the printed public key and fingerprint.
- [ ] Put the new public key into `CatalogSignature.PublicKeyPem`; update the tests that pin the old fingerprint.
- [ ] Decide the catalog: an own repository (for example `mega-hub-catalog`) with an own, signed `index.json`
      (`scripts/catalog-sign.ps1`), and change `CatalogDefaults.IndexUrl` and `CatalogUrl.CanonicalRepositories`.
      CouchHUD's plugins may stay available only as a separate, clearly labelled third-party source with his key pinned.
- [ ] Re-sign or remove `catalog/statements/*.json` (they are signed with CouchHUD's key).
- [ ] Sign every release with `scripts/sign-release.ps1` (own key) before it is offered to other users.

