# Security policy

Mega Hub is an independent fork of [CouchHUD](https://github.com/kewe87/CouchHUD) by kewe87, maintained by jjrussellite28-art.
It is a development preview; only the newest build is supported.

## Report a vulnerability (privately)

Please use **Report a vulnerability** on the *Security* tab of this repository:
<https://github.com/jjrussellite28-art/mega-hub/security/advisories/new>

- Do **not** open a public issue or pull request for a vulnerability, and do not post passwords, keys or personal configuration anywhere public.
- Include the Mega Hub version, the steps to reproduce and the impact.
- Problems in code that comes from CouchHUD and also exist there should be reported **privately to the CouchHUD project** as well,
  using [its own security policy](https://github.com/kewe87/CouchHUD/security). Problems that only exist in this fork belong here.

## Keeping up with upstream security fixes

Security fixes in CouchHUD are reviewed and brought into this fork.

## Signatures and trust

Mega Hub does not yet have its own release-signing key or plugin catalog; until it does, the signatures and keys described in
[docs/UPSTREAM-SECURITY.md](docs/UPSTREAM-SECURITY.md) belong to CouchHUD and do not authenticate Mega Hub builds.
The open items are tracked in [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md).
The inherited technical background (what the lock does and does not protect against) is kept in
[docs/UPSTREAM-SECURITY.md](docs/UPSTREAM-SECURITY.md).
