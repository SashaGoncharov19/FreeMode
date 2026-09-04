# T-009 — Encrypted, authenticated session between client and server

Status: ready
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

- [ ] `eng/integration-test.sh` passes with encryption on (the bot speaks the new handshake); a client without the handshake is refused.
- [ ] Overhead measured: bytes per pure-sync packet before/after (GCM tag = +16 B) and CPU per packet on the server at 300 bots (T-002 harness) — recorded in `docs/SYNC.md`.
- [ ] Wrong pinned key → the client refuses to connect with a clear message in `Runtime.log`.

## Risks and notes

Lidgren encrypts per message; unreliable channels tolerate loss (no stream cipher state). Replay protection: GCM nonce =
per-direction counter; Lidgren's sequence numbers are not authenticated — include the packet type byte in the AAD.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
