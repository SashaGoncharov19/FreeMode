# T-025 — Signed client manifest: the release job signs manifest.json, the server verifies the signature

Status: ready
Epic: E-10 Anti-cheat
Size: S
Branch: task/T-025-manifest-signing from the integration branch
Depends on: T-017
PR: yes

## Goal

`manifest.json` in the client package carries an Ed25519 signature made by the release job with a key from a repository secret;
the server refuses an unsigned or tampered manifest next to itself (so a server admin cannot be fed a fake one) and the public key
lives in `Shared`. Until the owner creates the secret the job writes an unsigned manifest and the server accepts it with a warning.

## Files

* Change: `eng/package-client.ps1` (sign when `GTAN_MANIFEST_KEY` is set), `.github/workflows/build.yml` (the secret into the
  Windows job's environment), `Server/Managers/Anticheat.cs` (verify with BouncyCastle Ed25519), new `Shared/Crypto/ManifestKey.cs`
  (the public key), `docs/CODEMAP.md` §10, `CHANGELOG.md`.

## Acceptance criteria

- [ ] With the secret set, the package's `manifest.json` has `signature` and the server logs `client manifest ... signed`.
- [ ] A manifest edited by hand is refused (`manifest.json: signature does not match`).

## Log

* 2026-09-05 20:10 agent — created from T-017's follow-ups. **Needs owner**: the secret (`GTAN_MANIFEST_KEY`, an Ed25519 private key).

## Result

(empty)
