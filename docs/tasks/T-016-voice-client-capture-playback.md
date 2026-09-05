# T-016 — Voice chat in the client: capture, encode, positional playback, push-to-talk, indicator

Status: needs owner (implemented; the microphone and the positional playback must be checked in game under Proton)
Epic: E-09 Voice
Size: L
Branch: task/T-016-voice-client from the integration branch
Depends on: T-015
PR: yes

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
* 2026-09-05 16:40 agent — started (branched from the T-015 branch; the PR targets the integration branch after the T-015 PR).
* 2026-09-05 17:30 agent — capture, playback, push-to-talk, indicator, settings and the harness capture test done; PR opened; needs the owner in game.

## Result

* **Decisions inside the task**: NAudio stays at the vendored 1.7.3 (`libs/NAudio.dll`): it has `WasapiCapture`, `WaveInEvent`,
  `BufferedWaveProvider`, `MixingSampleProvider`, `PanningSampleProvider`, `VolumeSampleProvider` and `WaveOutEvent`, which is all the
  client needs, so the NuGet 3.0 upgrade in the task's Files list was not necessary. Output through WinMM (`WaveOutEvent`, 80 ms) —
  the most compatible path under Wine; capture through WASAPI shared mode with WinMM as the fallback. Push-to-talk only (no VAD);
  the device stays open between presses while on a server and closes on disconnect. Attenuation is linear to silence at 45 m (the
  server's default range is 40 m); pan = dot(camera right, direction to the talker). The talking indicator is a green `*` after
  the nametag for 300 ms after each frame.
* **Changed**: `Client/Voice/VoiceCapture.cs`, `VoicePlayback.cs`, `VoiceKeys.cs`, `SyncPedVoice.cs` (new), `Client/Main.cs`
  (push-to-talk key down/up, the playback tick), `Client/Main/Network/ProcessMessages.cs` (the `Voice` case),
  `Client/Main/Cleanup.cs` (close on disconnect), `Client/Sync/Nametag.cs` (indicator), `Client/Main/Menu.cs` (Settings → Voice:
  on/off, key, volume), `Shared/PlayerSettings.cs` (`VoiceEnabled`, `VoiceKey`, `VoiceVolume`), `Tools/CefHarness/CaptureTest.cs`
  (new, `--capture-test <seconds>`), `Tools/CefHarness/CefHarness.csproj` (NAudio), `CHANGELOG.md`, `docs/CODEMAP.md`.
* **Verified**: the client builds against the real ScriptHookVDotNet build (`eng/dev-build-client.sh`); `Concentus.dll` lands in
  `bin/scripts` with the other client DLLs; `eng/dev-test.sh` green. Capture under Wine on the owner's machine, measured with
  `eng/cef-harness.sh --capture-test 3` (the harness process in the game's Proton prefix, no game): __T016_CAPTURE__
* **Owner check** (in game, under Proton): (1) join the local server, hold `N`: `Runtime.log` gets `voice: capture open, <format>`;
  a bot started with `GTANetwork.Bot --voice-expect 100 --duration 15 --say "/tp <your x> <your y> <your z>"` near you must end with
  `[voice] received N frames` (N ≥ 100 for 3 s of talking). Bad: `voice: capture could not start` or `WASAPI capture unavailable` +
  a WinMM failure — then the fix is on the Wine side (winepulse / PipeWire), the numbers from the capture test tell which.
  (2) `GTANetwork.Bot --voice-send 10 --say "/tp <near you>"`: a 440 Hz tone is heard, louder when closer, moving between the
  ears as you turn the camera, gone beyond ~45 m; a green `*` shows after the bot's nametag while it talks. (3) The clap test
  for latency (≤ 250 ms) needs two people or a recording; not done here.
* **Not done / follow-ups**: device selection (the default capture device is used; `VoiceInputDevice` from the task's Files is not
  implemented — the Wine prefix exposes one PulseAudio source anyway); voice activation; a local "you are talking" HUD hint.
