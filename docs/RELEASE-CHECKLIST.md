# Mega Hub release checklist

Status: DEVELOPMENT ONLY. No approved public or commercial release.

## Prepared in this change

- [x] Independent-fork identity and original author attribution in the README.
- [x] Keep GPL v3 license and inherited file notices.
- [x] Fork-owned app update destination and installer publisher/project links.
- [x] Include licensing/source notices with app publish output.
- [x] Produce source and build-commit information during CI; releases remain drafts.

## Required before any installer is published

- [x] Select the Mega Hub logo and retain its original file and Bing generation source.
- [ ] Complete remaining branding variants and installer wizard artwork; refresh screenshots.
- [ ] Isolate installation ID, folders, service/pipe names, mutexes, data, registry/task/firewall identities from CouchHUD, or implement and test an explicit migration that cannot damage an upstream install.
- [ ] Configure a fork-owned release signing identity and verification key. Do not weaken signature validation or claim an upstream signature authenticates this fork.
- [ ] Decide whether to use the upstream catalog; label it accurately and verify its permitted use, or provision a fork catalog and trust key.
- [ ] Review privacy/security notices against actual fork behavior and establish a private security-reporting route; inherited documents describe upstream.
- [ ] Inventory all runtime/dependency and asset licenses, include their full required notices, and resolve the noncommercial save-location data and service terms before selling.
- [ ] Run build, automated checks, browser checks, and clean Windows install/update/uninstall tests, including shell-mode emergency recovery. Do not install a development build on the user's main Windows account.
- [ ] Verify each installer has matching source, build scripts, license/notices and any required Installation Information available alongside it.
- [ ] Review draft release notes and downloads, add hashes/signatures, and explicitly approve publication.

Do not mark these tasks complete on the basis of documentation changes alone.
