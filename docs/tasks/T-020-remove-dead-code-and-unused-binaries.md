# T-020 — Remove dead code and unused binaries

Status: ready
Epic: E-02 Agent framework (hygiene)
Size: S
Branch: task/T-020-cleanup from the integration branch
Depends on: none
PR: yes

## Goal

The excluded legacy client files, unused `libs/` binaries and the root duplicates are gone; the build and tests are unchanged.

## Files

* Delete: `Client/Networking/{PedThread,StreamedItems,Streamer,SyncEventWatcher,SyncPed,SyncSender,UnoccupiedVehicleSync,WeaponManager}.cs`,
  `Client/Chat.cs`, `Client/ClassicChat.cs` (root copies), `Client/Main/Math.cs`, `Client/Misc/Program.cs`, `Client/Util/DebugWindow.cs`,
  `Client/GUI/DirectXHook/Hook/DXHookD3D10.cs`, `DXHookD3D10_1.cs` (all `Compile Remove`d in `Client/GTANetworkClient.csproj`),
  `libs/{EasyHook.dll,Interop.WMPLib.dll,Ionic.Zip.dll,Microsoft.Owin*.dll,Nancy*.dll,NAudio.WindowsMediaFormat.dll,Newtonsoft.Json.dll,Owin.dll,protobuf-net.dll}`
  (verify with `grep -r` that no csproj or script references them; `Ionic.Zip` is a NuGet reference in the classic launchers, not the libs copy),
  `natives.txt` at the root (the embedded `Client/natives.txt` is the one used — confirm `eng/` does not read the root copy), `whitelist.txt` (1-byte placeholder) if unused.
* Change: `Client/GTANetworkClient.csproj` (drop the `Compile Remove` list), `docs/CODEMAP.md` §12, `README.md` layout table if it mentions removed items.
* Do not touch: `Subprocess/GTANSubprocess`, `PlayGTANetwork*` (classic Windows launcher — Q-13 decides).

## Acceptance criteria

- [ ] `dotnet build GTANetwork.sln -c Release` and `eng/dev-test.sh` pass; `eng/package-client.ps1` still finds every file it copies (run its checks in the container with pwsh: `pwsh eng/package-client.ps1 -WhatIf` if supported, else read the script).
- [ ] `git grep` for each deleted file name returns nothing.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
