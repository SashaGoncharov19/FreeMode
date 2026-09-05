# T-009 — Encrypted, authenticated session between client and server

Status: done
Epic: E-05 RPC and protocol security
Size: M
Branch: task/T-009-session-crypto from the integration branch
Depends on: T-008
PR: yes

## Goal

Every connection performs an X25519 key exchange during the hail/approval; all data messages after approval are
encrypted and authenticated with AES-256-GCM (Lidgren `NetEncryption` subclass); the server's public key is pinned
through the master list entry (E-07) or given in the connect string (`host:port#<key>`), so a man in the middle
cannot impersonate a listed server; a per-session token identifies reconnects.

## Files

* New: `Shared/Crypto/NetAesGcm.cs` (`NetEncryption` implementation), `Shared/Crypto/Handshake.cs` (X25519 + HKDF via
  BouncyCastle.Cryptography 2.x — net48 + netstandard2.0), `Shared/Crypto/ServerKey.cs` (key file `server.key`, created at
  first start, public key printed in the banner).
* Change: `Shared/Packets.cs` (`ConnectionRequest.ClientPublicKey`, `ConnectionResponse.ServerPublicKey`, `SessionToken`),
  `Client/Main/Network/MainNetwork.cs:61` (`ConnectToServer`: key pair, pinned key check), `Server/ProcessMessages.cs:117`
  (`ConnectionApproval`: derive the session key, attach encryption to the connection), `Server/GameServer.cs` (key file),
  `Tools/GTANetwork.Bot/Program.cs` (same handshake), `Shared/MasterServerAnnounce.cs` (public key field), `docs/CODEMAP.md` §8, `CHANGELOG.md`.

## Acceptance criteria

- [x] `eng/integration-test.sh` passes with encryption on (the bot speaks the handshake, `crypto: encrypted session` in its log); a
      client without the handshake is refused (`--no-encryption` bot: "This server requires an encrypted session").
- [x] Overhead recorded in `docs/SYNC.md`: +24 bytes per message (8-byte counter + 16-byte tag); CPU with hardware AES on the
      server; the 300-bot number waits for the load harness (T-002, not built yet).
- [x] Wrong pinned key → refused with a clear line (bot: `server key mismatch`; client: `session: SERVER KEY MISMATCH …` in
      `Runtime.log` plus a notification). The in-game run is the owner's.

## Risks and notes

Lidgren encrypts per message; unreliable channels tolerate loss (no stream cipher state). Replay protection: GCM nonce =
per-direction counter; Lidgren's sequence numbers are not authenticated — include the packet type byte in the AAD.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-009-session-crypto` (decision D-14); `eng/dev-test.sh` green; PR opened.

## Result

* **Changed**: new `Shared/Crypto/SessionHandshake.cs` (X25519 key pairs, HKDF-SHA256 session key, fingerprints, hex helpers),
  `Shared/Crypto/ServerKey.cs` (`server.key`: the private key as hex, created at the first start), `Shared/Crypto/SessionCipher.cs`
  (`[counter][ciphertext][tag]`, direction nonces, replay window, `IAeadCipher` with a BouncyCastle implementation and a factory
  hook), `Shared/Crypto/NetSessionEncryption.cs` (Lidgren `NetEncryption` adapter, compiled into the server, the client and the
  bot), `Shared/Packets.cs` (`ClientPublicKey`, `ServerPublicKey`, `SessionToken`), `Shared/ServerSettings.cs` (`RequireEncryption`,
  default true), `Shared/MasterServerAnnounce.cs` (`PublicKey`), `Shared/GTANetworkShared.csproj` (BouncyCastle.Cryptography 2.6.2);
  server: new `Server/Crypto/AesGcmNet.cs`, `Server/Program.cs` (key file, banner lines, AesGcm), `Server/GameServer.cs`
  (`ServerKey`, `RequireEncryption`, `Send`/`Broadcast` helpers that encrypt per recipient), `Server/ProcessMessages.cs` (handshake
  at approval, refusal, decrypt of every data message), `Server/Packets.cs`, `Resources.cs`, `API.cs`, `Managers/UnoccupiedVehicleManager.cs`
  (all sends through the helpers), `Server/Elements/Client.cs` (`Session`, the connection's `Tag`), `Server/settings.xml`; client:
  `Client/Main/Network/MainNetwork.cs` (`Send`, `DecryptIncoming`, `CompleteHandshake`, `ConnectToServer(..., pinnedServerKey)`),
  `Client/Main.cs` (decrypt in the message pump), `Client/Main/Network/ProcessMessages.cs`, the other send sites, `Client/GUI/CefMenu.cs`
  (`host:port#key`), `Client/GTANetworkClient.csproj`; bot: `--pin`, `--no-encryption`, the handshake and the cipher; tests:
  `eng/smoke-test-server.sh` (banner, `server.key`), `eng/integration-test.sh` (encrypted session, refusal, pin mismatch, no
  authentication failures); docs: `CHANGELOG.md`, `README.md`, `docs/CODEMAP.md` §8, `docs/SYNC.md`, `docs/DECISIONS.md` D-14, `docs/HANDOFF.md`.
* **Verified**: `docker compose run --rm dev eng/dev-test.sh` → `All local checks passed.` with every bot phase encrypted (numbers
  and log lines in the PR).
* **Owner check**: in game, `Runtime.log` shows `session: encrypted (X25519 + AES-256-GCM), server key <fp> (not pinned)` after
  connecting; the server banner shows the same fingerprint; direct connect with `127.0.0.1:4499#<public key from the banner>`
  says `(pinned)`; with a wrong key the connection is refused with a notification.
* **Not done**: reconnect with the session token (the token is issued and logged, nothing uses it yet); the master list carrying
  public keys (T-011); the 300-bot CPU measurement (T-002); forward secrecy (a static server key; see D-14).
