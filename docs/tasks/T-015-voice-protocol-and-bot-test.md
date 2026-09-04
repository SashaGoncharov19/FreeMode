# T-015 — Voice chat protocol: Opus frames over Lidgren, server relay by range, bot test

Status: ready
Epic: E-09 Voice
Size: M
Branch: task/T-015-voice-protocol from the integration branch
Depends on: T-003 (range sets) preferred; none required
PR: no

## Goal

`PacketType.Voice` carries 20 ms Opus frames (48 kHz mono, 24 kbit/s) on an unreliable channel; the server relays
each frame to players within `VoiceRange` (default 40 m, per-player override by the API) in the same dimension, with
`API.setPlayerVoiceChannel(player, channel)` / `mutePlayer` controls; two bots exchange frames through the server and
the test asserts delivery and jitter.

## Files

* Change: `Shared/Packets.cs` (`PacketType.Voice = 43`, `ConnectionChannel.Voice = 13`), `Shared/GTANetworkShared.csproj`
  (Concentus 2.2.2 — netstandard2.0, pure C#), `Server/ProcessMessages.cs` (relay handler: no decode, ≤ 400 B per frame,
  ≤ 60 frames/s per player), `Server/Managers/VoiceRouter.cs` (new: range/channel/mute logic using `Streamer` sets),
  `Server/API.cs` (`setPlayerVoiceChannel`, `setPlayerVoiceRange`, `mutePlayerFor`, event `onPlayerStartTalking/Stop`),
  `Tools/GTANetwork.Bot/Program.cs` (`--voice-send <wav>`, `--voice-expect`: encodes a WAV with Concentus, counts received
  frames, measures inter-arrival jitter), `eng/integration-test.sh` (voice phase), `docs/CODEMAP.md` §10, `CHANGELOG.md`.

## Acceptance criteria

- [ ] Bot A sends 5 s of voice; bot B within range receives ≥ 245 of 250 frames with p99 inter-arrival ≤ 40 ms; bot C out of range receives 0.
- [ ] Server CPU for relaying 100 talkers × 50 frames/s measured with the T-002 harness (record).

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
