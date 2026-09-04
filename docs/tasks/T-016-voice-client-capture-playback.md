# T-016 — Voice chat in the client: capture, encode, positional playback, push-to-talk, indicator

Status: ready
Epic: E-09 Voice
Size: L
Branch: task/T-016-voice-client from the integration branch
Depends on: T-015
PR: no

## Goal

Push-to-talk (default `N`, settable) captures the microphone, encodes with Concentus and sends `Voice` packets;
received frames are decoded per speaker and played with distance attenuation and stereo panning from the speaker's
synced position; a talking indicator shows over the speaker's nametag; a settings page selects devices and volume.

## Files

* Change: `Client/GTANetworkClient.csproj` (NAudio 3.0.1 from NuGet replacing `libs/NAudio.dll` 1.7.3 if binary-compatible with
  `Client/Javascript/LoopAudioStream.cs`; else keep 1.7.3 and add NAudio 3 side by side is not possible — evaluate first),
  `Client/Main/Network/ProcessMessages.cs` (`Voice` handler → `VoicePlayback`), new `Client/Voice/VoiceCapture.cs`
  (`WasapiCapture` 48 kHz mono, fallback `WaveInEvent`; 20 ms frames; VAD gate), `Client/Voice/VoicePlayback.cs`
  (one `BufferedWaveProvider` + `PanningSampleProvider` per speaker, mixed by `MixingSampleProvider` into one `WasapiOut`),
  `Client/Sync/Nametag.cs` (indicator), `Shared/PlayerSettings.cs` (`VoiceKey`, `VoiceInputDevice`, `VoiceVolume`, `VoiceEnabled`),
  `Client/Main/Menu.cs` (settings items), `docs/CODEMAP.md`, `CHANGELOG.md`.

## Acceptance criteria

- [ ] Owner check under Proton: the microphone is captured (Wine's `winepulse`/PipeWire), a bot with `--voice-expect` receives the owner's frames.
- [ ] A bot sending a WAV is heard positionally in game (owner check); end-to-end latency measured with a clap test ≤ 250 ms.

## Risks and notes

Audio capture under Wine is the risk: measure it first (a 30-line WASAPI capture test in the harness process) before the UI.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
