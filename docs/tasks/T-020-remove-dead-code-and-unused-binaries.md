# T-020 — Remove dead code and unused binaries

Status: done
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

- [x] `dotnet build GTANetwork.sln -c Release`, `eng/dev-build-client.sh` and `eng/dev-test.sh` pass; `eng/package-client.ps1` copies only files that still exist (read: `libs/` — `EasyHook64.dll`, `EasyLoad64.dll`, the two `sharpdx_*_x64.dll`; `whitelist.txt` removed from its copy line; no pwsh in the container).
- [x] `git grep` for each deleted file name returns nothing (only `CHANGELOG.md`, `docs/CODEMAP.md` and this task mention them as removed).

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 12:10 agent — started.
* 2026-09-05 12:40 agent — done; PR opened.

## Result

* **Changed**: deleted 15 legacy client sources (`Client/Networking/{PedThread,StreamedItems,Streamer,SyncEventWatcher,SyncPed,SyncSender,UnoccupiedVehicleSync,WeaponManager}.cs`,
  `Client/Chat.cs`, `Client/ClassicChat.cs`, `Client/Main/Math.cs`, `Client/Misc/Program.cs`, `Client/Util/DebugWindow.cs`,
  `Client/GUI/DirectXHook/Hook/DXHookD3D10.cs`, `DXHookD3D10_1.cs`), 12 unused `libs/` DLLs (`EasyHook.dll`, `Interop.WMPLib.dll`,
  `Ionic.Zip.dll`, `Microsoft.Owin*.dll` ×3, `Nancy*.dll` ×2, `NAudio.WindowsMediaFormat.dll`, `Newtonsoft.Json.dll`, `Owin.dll`,
  `protobuf-net.dll`), the root `natives.txt` and `whitelist.txt`; `Client/GTANetworkClient.csproj` lost its `Compile Remove` list;
  `eng/package-client.ps1` no longer copies `whitelist.txt`; `docs/CODEMAP.md` §12; `CHANGELOG.md` (Removed).
* **Verified**: `git grep` of every deleted name → only the changelog, the code map and this file; `eng/dev-build-client.sh` (the
  client against the real SHVDN build) and `eng/dev-test.sh` green in the dev container. `Ionic.Zip.dll` in `eng/package-client.ps1`
  is the classic launcher's NuGet output, not the `libs/` copy; `Newtonsoft.Json.dll`/`protobuf-net.dll` in `eng/dev-sync-client.sh`
  are the browser host's build output.
* **Not done / follow-ups**: `Subprocess/GTANSubprocess`, `PlayGTANetwork*` untouched (Q-13); `Client/Networking/DeltaCompressor.cs`
  and `DownloadManager.cs` stay (compiled).
