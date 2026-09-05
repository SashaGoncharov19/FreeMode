# T-023 — Encrypted relay cost: pooled buffers, and a per-server relay key (Q-14)

Status: done
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

## Measurement (step 1, 5 Sept 2026)

Micro-benchmark on the dev container (`AesGcm` = `System.Security.Cryptography.AesGcm` on .NET 10 / OpenSSL, one instance reused):

| operation | ns/op |
| --- | --- |
| `SessionCipher.Seal` (allocates the output, one `AesGcm.Encrypt`), 100 B | 513 |
| `AesGcm.Encrypt` alone, preallocated buffers, 48 B / 100 B / 1200 B | 432 / 470 / 601 |
| `new byte[124]` + copy of 100 B | 15 |
| `new AesGcm(key)` per message (the key schedule) | 1396 |
| BouncyCastle `GcmBlockCipher` (the client's path), 100 B | 1002 |
| `Aes.EncryptEcb` 112 B (reference: the interop floor) | 846 |

So the cost is the *call*, not the bytes and not the allocation: ~450 ns of P/Invoke and OpenSSL context work per `Encrypt`,
regardless of size. With one call per recipient the tick thread pays 657 k × 0.5 µs ≈ 0.33 s per second at 300 players, plus
Lidgren's per-recipient enqueue. Nothing in user code makes one `AesGcm.Encrypt` cheaper; the ways out are fewer calls
(Q-14 relay key, T-003 fewer recipients) or calls on other threads — which is what this task does.

## Approach

1. Measure the split: a micro-benchmark of `Seal` on a 100-byte message (AES-NI should be ~0.3 µs), the allocations per
   relayed packet (two arrays and one pooled message per recipient today).
2. **Relay workers** (done): `Server/Managers/RelaySealer.cs` — `GameServer.Send` copies the payload once and hands it with the
   recipients to 1–4 worker threads; a connection always maps to the same worker (`RemoteUniqueIdentifier % workers`), so the
   per-client order and each session's counter stay single-threaded; the worker makes the per-connection message, seals it in
   place (`SessionCipher.SealInto`, no allocation) and enqueues it in Lidgren (thread-safe). A full worker queue drops unreliable
   messages (the next sync packet supersedes them; counted as `relay.dropped` in `/metrics.json`) and makes reliable ones wait.
   `<relaythreads>` (default 0 = cores − 2, clamped 1..4).
3. Q-14: a per-server **relay key** for the relayed sync channels only (pure/light/basic sync): the server encrypts each
   relayed packet once for all recipients; the per-session key stays for everything else and for the client → server
   direction. Trade-off: a player can decrypt other players' relayed sync (they receive it anyway) and could forge relayed
   packets if they can inject datagrams into another client's connection (needs that client's address and Lidgren state).
   Implement only if the owner decides (a).

## Acceptance criteria

- [x] `eng/load-test.sh 300 120` encrypted: tick p50 ≤ 10 ms with the same delivered rate as plaintext (baseline 66 ms vs 1.1 ms) —
      tick p50 0.52 ms (baseline 66); the server emits 2512 packets/s per player like plaintext (2622). The receivers got 1667 of
      them because Lidgren drops 25 % at its 64-message per-connection send window (`relay.lidgrenDropped` = 26 M in 140 s) — the
      transport's limit, recorded for Q-10, not this task's.
- [x] `eng/integration-test.sh` and `eng/integration-test-auth.sh` pass (`eng/dev-test.sh` green); the pinned-key refusal is part of `integration-test.sh`.
- [x] `docs/SYNC.md` §6 has the after-numbers next to the baseline.

## Log

* 2026-09-05 08:20 agent — created from the T-002 baseline.
* 2026-09-05 09:05 agent — started.
* 2026-09-05 10:00 agent — relay workers implemented and measured; PR opened.

## Result

* **Changed**: `Server/Managers/RelaySealer.cs` (new: the workers, per-connection partitioning, bounded queues, drop counters),
  `Server/GameServer.cs` (`Send` hands the payload to the workers when they run; `Start` creates them from `<relaythreads>`,
  the closing tick drains and stops them; synchronous fallback otherwise), `Shared/Crypto/SessionCipher.cs` (`SealInto`, no
  allocation; `Seal` uses it), `Shared/ServerSettings.cs` + `Server/settings.xml` (`<relaythreads>`, 0 = automatic),
  `Server/Managers/Metrics.cs` (`relay: { workers, queued, dropped, lidgrenDropped }` in `/metrics.json`), `docs/SYNC.md` §6,
  `docs/CODEMAP.md`, `docs/agents/testing.md`, `CHANGELOG.md`.
* **Verified**: `eng/load-test.sh 300 120` encrypted, before → after: tick 66.17 / 135.30 ms and 11 ticks/s → 0.52 / 19.63 ms and
  51 ticks/s; out 2200 → 2512 pkt/s per player (plaintext 2622); `eng/load-test.sh 1000 120`: all 1000 join, tick 3.37 / 16.61 ms,
  49 ticks/s, 437 connections time out at ~16 MB/s of relay (Lidgren's socket thread; before: one tick of 81 s and 969 → 4 players). `eng/dev-test.sh` green.
* **Not done / follow-ups**: Q-14 (relay key) stays open — with the workers the tick thread no longer pays for the cipher, so the
  decision can wait for T-003's recipient cuts. Lidgren's sender drops 25 % of the relayed unreliable messages at 300 players and 1000 connections do not hold at
  16 MB/s: the transport is the visible limit now (Q-10 has the evidence); T-003 (fewer messages) first, then the transport decision.
