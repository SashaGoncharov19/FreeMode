# T-023 — Encrypted relay cost: pooled buffers, and a per-server relay key (Q-14)

Status: in progress
Epic: E-03 Scale
Size: M
Branch: task/T-023-encrypted-relay-cost from the integration branch
Depends on: T-002
PR: yes

## Goal

The encrypted relay costs about 1.1 µs per recipient-message against 78 ns in plaintext (T-002 baseline, `docs/SYNC.md` §6):
at 300 players the cipher path alone takes ~60 ms of every tick and the loop drops from 60 Hz to 11 Hz. Bring the per-recipient
cost well under 300 ns without weakening T-009's guarantees, measured with `eng/load-test.sh 300 120` (encrypted vs
`LOAD_NO_ENCRYPTION=1`).

## Files

* Change: `Server/GameServer.cs` `Send(msg, IList<NetConnection>, …)` (per recipient: `Server.CreateMessage`, a copy of the
  payload, `Encrypt`), `Shared/Crypto/SessionCipher.cs` `Seal` (allocates the output array per message),
  `Shared/Crypto/NetSessionEncryption.cs` `Encrypt` (replaces the message data), `Server/Packets.cs` `ResendPacket`.
* New (only if Q-14 is decided (a)): `Server/Crypto/RelayKey.cs`, the relay key in `ConnectionResponse` (inside the encrypted hail).

## Approach

1. Measure the split: a micro-benchmark of `Seal` on a 100-byte message (AES-NI should be ~0.3 µs), the allocations per
   relayed packet (two arrays and one pooled message per recipient today).
2. Zero-allocation path: encrypt straight into an outgoing message sized `length + Overhead` (no intermediate array), a
   `Span`-based `Seal` overload, `ArrayPool` for anything left; reuse one nonce buffer per session (already the case).
3. Q-14: a per-server **relay key** for the relayed sync channels only (pure/light/basic sync): the server encrypts each
   relayed packet once for all recipients; the per-session key stays for everything else and for the client → server
   direction. Trade-off: a player can decrypt other players' relayed sync (they receive it anyway) and could forge relayed
   packets if they can inject datagrams into another client's connection (needs that client's address and Lidgren state).
   Implement only if the owner decides (a).

## Acceptance criteria

- [ ] `eng/load-test.sh 300 120` encrypted: tick p50 ≤ 10 ms with the same delivered rate as plaintext (baseline 66 ms vs 1.1 ms).
- [ ] `eng/integration-test.sh` and `eng/integration-test-auth.sh` pass; `--pin` with a wrong key is still refused.
- [ ] `docs/SYNC.md` §6 gets the after-numbers next to the baseline.

## Log

* 2026-09-05 08:20 agent — created from the T-002 baseline.
* 2026-09-05 09:05 agent — started.

## Result

(empty)
