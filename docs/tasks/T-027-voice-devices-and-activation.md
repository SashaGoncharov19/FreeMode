# T-027 — Voice: input device selection, voice activation, a local talking indicator

Status: draft
Epic: E-09 Voice
Size: S
Branch: task/T-027-voice-devices from the integration branch
Depends on: T-016 (the owner's in-game check first)
PR: yes

## Goal

Settings → Voice lists the capture devices (WASAPI endpoints) and remembers the chosen one (`VoiceInputDevice`); an optional
voice-activation mode (RMS gate with a threshold setting) replaces push-to-talk; the HUD shows a small "talking" marker for the
local player while frames are sent.

## Files

* Change: `Client/Voice/VoiceCapture.cs` (device by id, the gate), `Shared/PlayerSettings.cs` (`VoiceInputDevice`, `VoiceActivation`,
  `VoiceThreshold`), `Client/Main/Menu.cs` (the items), `Client/Util/DebugInfo.cs` or the HUD script (the marker), `CHANGELOG.md`.

## Acceptance criteria

- [ ] Owner check: a second microphone is selectable and used; voice activation sends frames only while speaking.

## Log

* 2026-09-05 20:10 agent — created as draft from T-016's follow-ups; ready after the owner's T-016 check.
* 2026-09-05 14:40 agent — the local talking indicator is done ahead of the task (`Client/Voice/VoiceHud.cs`, asked for by the owner); device selection and voice activation remain.

## Result

(empty)
