# Changelog

All notable changes to the revived GTA Network are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Two version numbers exist side by side:

* **Release tag** (`v0.1.0-alpha.19`, later `v0.1.0`, `v0.2.0` ...): the version people install. It is chosen by
  hand when a release is published (`build.yml` → *Run workflow* → `release_tag`), and the matching section of
  this file becomes the release notes. A `-` in the tag marks a pre-release.
* **Assembly/file version** (`0.1.<days since 2016-01-01>.<UTC minutes / 2>`, e.g. `0.1.3898.584`): derived from
  the commit date by `eng/version.sh` so that every artifact of one build carries the same number and the
  network protocol version check keeps working. It appears in artifact names and in the game.

## [Unreleased]

Nothing yet.

## [0.2.0] - unreleased

Modern browser and JavaScript runtime. Pre-releases `0.2.0-alpha.N` carry these notes while the branch
`claude/modernize-deps-4d8uyn` is being tested in game.

### Changed
* **TypeScript server resources run in a Bun runtime** (`<script src="server/index.ts" type="server" lang="typescript"/>`):
  the server starts one Bun process (`runtime/main.ts`, shipped next to the server; Bun 1.4.1 from `GTAN_BUN`, `runtime/bun/`
  or `PATH`) and talks to it over a Unix domain socket (loopback TCP on Windows) with MessagePack frames — the resource's
  entry module exports `default function main(gtan)`, `gtan.api.*` is the whole server API (typed by
  `runtime/gtan/api.generated.d.ts`, every call returns a Promise), `gtan.on(event, handler)` the engine's events (cancelable
  ones return `{ cancel: true }`), `gtan.commands.register` the chat commands, `gtan.players` a 10 Hz mirror of the players'
  state pushed as deltas; resources hot-reload on file changes; a dead runtime is restarted (1, 2, 5 s back-off). Measured
  bridge: 2.0 M one-way calls/s, round trip p50 6 µs. `Server/resources/tsdemo` is the example; C# and VB resources are
  unchanged. Bun's own APIs (`Bun.sql`, `Bun.redis`, `Bun.s3`, fetch, WebSocket) are available to gamemodes.
* **Request/response calls between scripts (RPC)**: a client script's `API.rpc.call(name, args)` returns a Promise answered by
  the server handler of that name — `API.registerRpc(name, handler, allow?)` in C#, `gtan.rpc.register(name, handler, { allow })`
  in TypeScript — and `API.callClient(player, name, args)` / `gtan.rpc.callClient` call a handler the client script registered with
  `API.rpc.register`; CEF pages get `gtan.rpc.call(name, args)`, answered by the owning client script's handler or forwarded to the
  server. Arguments and results are one JSON value each (64 KB at most); errors carry a code (`timeout`, `denied`, `unknown`,
  `rate`, `handler`, `size`, `invalid`, `disconnected`) and a message, never a stack trace; each player may make 30 requests per
  second; the default timeout is 10 s (60 s at most). Wire: `RpcRequest`/`RpcResponse` (`Shared/Rpc/`) on the reliable ordered
  channel `Rpc`. The `auth` login form now calls `auth:login` / `auth:register` over RPC and shows the server's reason on a wrong
  password; `freeroam` answers `freeroam:ping` and `freeroam:secret` (logged-in players only); the bot has `--rpc name json` and
  `--rpc-burst name n`, and the integration tests drive a round trip, a denied call, an unknown name and the rate limit.
* **A launcher window** (`Launcher.Gui`, Avalonia 12.1.2, one binary for Linux and Windows, published as `gtanetwork-launcher-gui-*`):
  Home shows what the doctor detects (game folder, Steam, Proton, prefix, ScriptHookV, launch options) and has Play/Stop with
  the launcher's log; Settings edits `settings.xml` (launch method and paths, display name, master server, the CEF switches,
  debug mode) — the same file the in-game client reads; Logs tails `logs/*.log`. The launcher's logic moved into
  `Launcher.Core` (shared by the window and the command line launcher, whose behaviour is unchanged). `eng/setup-linux.sh`
  installs the window into `<install>/gui/` and points the desktop entry at it; `--self-test` builds the window without a
  display, which CI and `eng/dev-test.sh` run.
* **Fix: RPC from client scripts and CEF pages did nothing in game.** The client's RPC helper used `String(x)`, which in the
  game's script engine is the host type `System.String` (registered with `AddHostType`), so calling it made ClearScript throw
  "Invalid generic type argument" inside the promise; the error handler used `String(x)` too and died silently. The helper avoids
  the name now. Found with the new **in-game autotest**: `GTAN_AUTOTEST=host:port[#serverkey][;password]` in the launcher's
  environment (passed to the game by the Proton and direct launch methods) makes the client connect by itself once the game is
  ready, run an RPC from a client script and from a CEF page (`ui/autotest/index.html`), write `autotest: …` lines to
  `Runtime.log` and quit (`GTAN_AUTOTEST_QUIT=0` keeps it running) — the browser and the game can be exercised without a person.
* **Encrypted, authenticated sessions** (T-009): every connection does an X25519 key exchange in the hail (the client sends an
  ephemeral public key, the approval carries the server's static key from `server.key`, created at the first start and shown in
  the banner) and derives a session key with HKDF-SHA256; every data message after that is AES-256-GCM with a per-direction
  counter nonce and a 128-message replay window (24 bytes of overhead per message). The server uses .NET's hardware AES, the
  in-game client BouncyCastle (`BouncyCastle.Cryptography` 2.6.2 ships with it). Clients pin the server's key with
  `host:port#<public key>` in the menu's direct connect (the master list will carry it); a mismatch refuses the connection with
  a clear line in `Runtime.log`. `<RequireEncryption>true</RequireEncryption>` (default) refuses clients without the handshake, so
  clients older than this build cannot join; false lets them join in plaintext. The bot speaks the handshake (`--pin`,
  `--no-encryption`), and the integration test checks the encrypted session, the refusal and the pin mismatch.
* **`gtanetwork create <name>`** (`Tools/GTANetwork.Cli`, published for Linux and Windows with every build) writes a self-contained
  resource skeleton in TypeScript: `server/index.ts` for the Bun runtime, `client/index.ts` bundled by the server, a CEF page
  talking to both through `gtan.rpc.call`, the typings it type-checks against (`types/`), `package.json` with `bun run check`.
  **freeroam is TypeScript on both sides now** (`server/index.ts` + `client/index.ts`; `freeroam.cs` is gone) with the same
  commands and messages. The runtime library gained `gtan.enums` (the enums the server API uses, generated as runtime tables:
  `gtan.enums.VehicleHash.Adder`) and `gtan.parseEnum` / `gtan.enumName` for chat arguments (`/veh adder` → `Adder`).
* **Client scripts in TypeScript** (`<script src="client/index.ts" type="client" lang="typescript"/>`): at resource start the server
  bundles the entry with Bun (`bun build --target=browser --format=iife`; imports resolved, types erased) into the one JavaScript
  text the in-game engine already runs, and caches it under `resources/.cache/<resource>/` by the hash of the resource's sources
  (the second start reads the cache). Bun does not type-check: a resource with its own `typescript` in `node_modules` and a
  `tsconfig.json` is checked with `tsc --noEmit` first, and a type error fails the start with its file:line; a syntax error fails
  it in any case. Without Bun the client script is skipped with one error line and the rest of the resource starts, so the server
  machine needs Bun (`GTAN_BUN`, `runtime/bun/`, or on `PATH`) for TypeScript resources. `freeroam`'s `client.js` is now
  `client/index.ts` with a `tsconfig.json` against `types/`.
* **The main menu is a CEF page** (`ui/menu`, `<CefMenu>`, default true): at game start and after a disconnect the player sees the
  servers found — favourites and recent from `settings.xml`, LAN discovery, the master list when one is configured — with gamemode,
  players and a password mark, a filter, favourites (★), direct connect (host, port, password) and the settings the NativeUI menu had
  (display name, chat, FPS counter, the CEF switches, the master server URL), and Quit. The browser host starts with the game so the
  page is up within a second or two; the NativeUI menu stays on the pause key (host tab, debug switches) and takes over when the page
  does not come up within 30 s. `eng/cef-harness.sh` renders the page (`menu page OK`). After the owner's first run: the menu
  browser now receives the mouse and the keyboard (it was missing from the input list), it is created during the game's loading
  so the menu is on screen the moment the game is ready, and it stays alive while the classic menu is on top (a new browser per
  toggle meant a new renderer and a second-long stall); the client logs every hop of an RPC in `Runtime.log` (`rpc: …`).
  Second run: the page now comes up the moment the client starts, as a loading screen over the game's own loading ("Grand
  Theft Auto V is loading…"), and becomes the server list when the game is ready; the RPC helper inside the script engine traces
  its steps too (`rpc: [js …]`).
* **TypeScript typings of the scripting APIs** (`types/`): `client.d.ts`, `server.d.ts`, `shared.d.ts` are generated from the
  built assemblies by `Tools/GTANetwork.TypeGen` (441 client members, 414 server members, events as `HostEvent<…>` with
  `connect`/`disconnect`), `cef.d.ts` describes the page bridge, `api-catalogue.json` lists every server API member for the
  coming Bun runtime. CI regenerates them and fails on stale typings; `samples/ts-resource/` is a resource type-checked against them.
* **Loading screen from "connect" until the server's resources are in** (`<CefLoader>`, default true): a CEF page shipped
  with the client (`ui/loader/`, served by the browser host as `https://gtan/loader/index.html`) covers the game from the
  connection attempt through the handshake and the file download (with the file index and the current file) until the
  client scripts start, then fades out; the browser host is starting at the same moment, so the page appears about a
  second after "connect". Errors keep the existing warning dialogs. `Runtime.log` notes `loader: shown …` / `loader: hidden after N ms`.
* **.NET 10** for the server, launcher, headless bot and Map2Resource (was .NET 8, whose support ends on
  10 Nov 2026); server resources are compiled with Roslyn 5.9. The dev container and CI build with the .NET 10 SDK.
* **Browser: CEF 3.2987 (Chromium 57, 2017, single-process inside the game) → CefSharp.OffScreen 151 (Chromium 151,
  2026) in its own process, `cef\GTANetwork.CefHost.exe`.** The game starts the host with the first browser a
  resource creates (`<CefPreload>true</CefPreload>` starts it at game start), sends it commands over its stdin and
  receives events over its stdout (length-prefixed JSON, `GTANetworkShared.Cef`), and reads every browser's pixels
  from a shared-memory frame buffer into the same DirectX overlay as before. Page rendering and the GPU run in the
  host's `CefSharp.BrowserSubprocess.exe` processes. Nothing of CefSharp or libcef is loaded into `GTA5.exe`:
  CefSharp is C++/CLI and only works in the default AppDomain, while ScriptHookVDotNet runs the client in a second
  one — the reason alpha.1 to alpha.5, which hosted Chromium inside the game, died during `Cef.Initialize` under
  Proton whatever the switches (details and the reproduction: `docs/CEF-UPGRADE.md`, `eng/cef-harness.sh`).
  The runtime (~350 MB) lives in `<install>/cef` and comes from NuGet; `libs/cef` and `libs/Xilium.CefGlue.dll`
  left the repository (144 MB). Settings: `<CefGpu>` (GPU in the host, default off = software rendering),
  `<CefFrameRate>` (default 60), `<CEFDevtool>` (remote debugger on port 9222), `<CefInProcessGpu>` (GPU service
  inside the host, default true; false = a further GPU process), `<CefSharedTexture>` (frames as D3D11 shared
  textures with the GPU on, default true), `<CefIdleExitSeconds>` (stop the host without browsers, default 60). Chromium runs with the Alloy runtime style, without
  DirectComposition, window occlusion tracking, renderer code integrity, extensions, background networking and
  component updates, with the network service in-process and, without `<CefGpu>`, in display-compositor-only mode
  (`--use-gl=disabled --disable-software-rasterizer`). It is kept small: one renderer process for all pages
  (`--renderer-process-limit=1 --process-per-site`, no spare renderer), the network and audio services inside the
  host, no metrics, media-router, optimization-hints, translate or autofill services, a 32 MB disk cache — three
  processes (host, renderer, storage service) instead of eight. Under Proton the host runs without Wine tracing
  even when the game has it (`WINEDEBUG=-all`; `GTAN_CEF_WINEDEBUG` overrides). The host starts when a connection
  to a server is initiated, so a page opened on join is drawn at once. Logs: `logs/CEF.log` (game side),
  `logs/CEF-host.log` (the host), `logs/CEF-chromium.log` (Chromium). If the host dies mid-session a new one is started
  (below).
* **Browser frames**: the host publishes only the changed rectangle of each paint; the game copies just that into a
  staging image and uploads it into one persistent texture (no bitmap and no texture re-creation per frame, as
  before); a frame is on screen within a few milliseconds. Browsers paint at 60 fps by default (`<CefFrameRate>`).
  With the browser in its own process `<CefGpu>true</CefGpu>` works under Proton (ANGLE on D3D11 through DXVK):
  the harness delivers 60 frames/s of an animated 1280x720 page either way, with accelerated canvas and steadier
  frame pacing on the GPU. With the GPU on, frames travel as **D3D11 shared textures** (`<CefSharedTexture>`, default
  on): the host copies each of Chromium's paints into a ring of four textures it owns (Chromium's own texture
  handles are only valid inside its paint callback), announces a slot once the copy has executed on the GPU, and the
  overlay copies that slot GPU-side into its own texture — no CPU work per frame (0.02 ms of GPU copy instead of a
  0.4 ms memcpy per 720p frame; the harness measures 8 ms from a page change to the frame showing it, 3 ms over
  shared memory). If the host has no D3D11 device or a texture cannot be opened on the game's device the browser
  silently falls back to CPU frames.
* **Memory under Wine**: the PE files of the browser host are page-aligned (`eng/pe-realign.py`, at packaging and on
  Linux installs). Wine maps a DLL from disk only with page-aligned sections; Chromium's are linked with a 512-byte
  file alignment, so Wine used to read `libcef.dll` (272 MB) into every process separately. Chromium's resident memory
  for an idle page: 2.9 GB in alpha.5 → 0.86 GB (1.12 GB with the GPU on).
* **Chromium leaves when it is not needed**: with no browser open for `<CefIdleExitSeconds>` (default 60; 0 = keep it)
  the game stops the browser host, giving Chromium's ~0.9 GB back to a machine that may be short of it; the next
  browser a resource creates starts a fresh host (a second or so). A host that dies mid-session no longer freezes
  browsers for the rest of the session either: a new host is started and the open browsers are created again on it
  with their pages (three times per session at most).
* **Hitch diagnostics**: a frame that takes longer than 50 ms is logged in `logs/Runtime.log` as `[HITCH]` with the
  millisecond, how much of it our overlay took, garbage collections and browser frames around it, so a freeze can be
  told apart from the game's own stalls (ScriptHookVDotNet's log has the script side). The 10-second overlay profile in
  debug mode counts frames over 33/50/100 ms. On Linux the launcher's `--debug` also writes `logs/hitch-monitor.log`:
  one line per second with swap traffic, memory-pressure stall time, GTA5.exe page faults and memory, Chromium's
  memory, CPU and GPU clocks and temperatures, to match against the hitch lines.
* **Keyboard in pages**: Caps Lock no longer types a character; modifier, lock, function and navigation keys and
  Ctrl+letter shortcuts send no text; Caps Lock state goes through the keyboard layout (Shift+Caps Lock = lowercase).
* **Page ↔ script bridge**: `resourceCall(name, ...args)` and `resourceEval(code)` still exist in every page
  (also as `gtan.call`/`gtan.eval`) but are one-way now: the page runs in another process, so there is no return
  value. The bridge is the first script of every page served from the resource files (and of pages given to
  `loadHtmlCefBrowser`), so page scripts can call it while the document loads. `browser.call()`/`browser.eval()`
  from client scripts are unchanged. Local browsers only see `https://<resource>/<file>` from the downloaded
  resource files; pop-ups navigate the same browser.
* **JavaScript runtime: ClearScript 5.4.9 (V8 5.5) → ClearScript 7.5 (V8 12)** from NuGet; modern JavaScript
  (ES2023) in client scripts. The V8 inspector (port 9222) is only opened with `<DebugMode>true</DebugMode>`.
* Closing a browser removes its image from the overlay (it used to be added a second time).
* The dead Social Club avatar host (`a.rsg.sc`) is one `Runtime.log` line now instead of a stack trace in
  `Error.log` at every start.

### Added
* **Custom DLC packs, first half** (T-014, D-10): a server declares packs in `settings.xml` (`<dlcpack name url sha256 size
  required/>`) and serves them as `GET /dlcpacks.json`; the launcher downloads and verifies them — `GTANetwork.Launcher prepare
  <host:port>` or the window's *Packs* page — into `<install>/dlcpacks/<name>/dlc.rpf` (SHA-256 and size checked, a wrong file is
  never kept); the client reports the packs applied for the session (`dlcpacks/mounted.json`) and a server refuses a player
  missing a required pack, naming it. Applying the packs to the game at start (the `update.rpf` overlay) waits for the owner's
  decision Q-15. `eng/integration-test-dlc.sh` covers the list, the refusal, the join with the pack and the launcher's download.
* **Interest management on the server** (T-003): who receives whose sync, and how often, is decided per sender every 250 ms
  from a grid of 200 m cells per dimension — the nearest players within 50 m (at most 64) get every pure packet (10 Hz), those
  within 200 m every third, the rest within 2000 m (at most 250 in the three tiers) every tenth, everyone else one position every
  3 s; a player receives at most 30 KB/s of others' sync (farther tiers dropped first). `<interest cell full medium range budget
  maxfull maxnear/>` in `settings.xml`; `/metrics.json` → `interest` (per-tier packets/s, budget drops). Measured with the load
  harness: 1000 players now hold (tick p99 10 ms, 15 KB/s per player, all 1000 stayed connected; before, 437 timed out); 300 players 120 → 32.5 KB/s per player, Lidgren refusing nothing (it dropped 25 % before).
* **Relay workers** (T-023): the per-player copy and AES-GCM sealing of every message moved off the server's tick thread onto
  1–4 relay threads (`<relaythreads>` in `settings.xml`, 0 = automatic); each client stays on one worker so its message order is
  unchanged; when a worker's queue is full, unreliable sync is dropped instead of stalling the tick (`/metrics.json` → `relay`).
  Measured with the load harness at 300 players: tick p50 66 → 0.5 ms, p99 135 → 20 ms, 11 → 51 ticks/s; the tick thread no longer pays for the cipher (`docs/SYNC.md` §6).
* **Load harness** (T-002): `GTANetwork.Bot --bots N --move <m> --report <file>` holds N simulated players in one process
  (each joins like a client and sends pure sync every 100 ms and light sync every 1500 ms while walking within the radius);
  the server exposes `GET /metrics.json` (tick p50/p99/max, packets and bytes in/out, GC, near-set sizes, players, RSS) when
  `<httpserver>` is on; `eng/load-test.sh <players> <seconds>` runs both and prints the baseline table (`docs/SYNC.md` §6).
* **Master list** (`Tools/GTANetwork.Master`: ASP.NET Core minimal API on .NET 10, SQLite, Docker image): servers announce
  themselves every 60 s (`POST /addserver`; `settings.xml` `<announce>true</announce>` and `<master>https://...</master>`) with
  their public key and a token from `master.token` that owns the address; the master pings the announced UDP port with a Lidgren
  discovery request before listing a server. `GET /servers`, `/verified`, `/stats` and `/welcome.json` keep the 2016 shapes,
  `GET /servers/full` adds name, players, gamemode, map, version, public key and the verified flag. The in-game menu reads
  `/servers/full` when `MasterServerAddress` is set and pins the listed server's key when connecting from the list. The 2016
  master's address (`master.gtanet.work`) is gone from the server: with an empty `<master>` nothing is announced.
  `eng/integration-test-master.sh` runs a master and a server together (in `eng/dev-test.sh` and CI).
* **Debug mode**: one switch for all diagnostic log lines (client-script API probe, overlay frame geometry,
  `[PROFILE] Present hook overlay`, CEF frames, request traces, `resourceCall`s, page console output below
  warning level). On in Debug builds, with `<DebugMode>true</DebugMode>` in `settings.xml` (also the "Enable
  Debug mode" checkbox in the in-game settings) or with `GTAN_DEBUG=1` in the environment. The launcher's
  `--debug` sets `GTAN_DEBUG=1` and, through Proton, `PROTON_LOG=1` with
  `WINEDEBUG=+timestamp,+pid,+tid,+seh,+threadname,+loaddll,+mscoree,-keyboard` (exceptions and module loads in
  `~/steam-271590.log`, without the stack-walk tracing and the keyboard-layout spam of GTA V's loading screen that
  made Proton's default a gigabyte per session; `GTAN_WINEDEBUG` overrides), so a crash report no longer needs log
  lines to be added and removed by hand. `Runtime.log` starts with a `Debug mode: on/off` line. Debug mode costs
  frame rate and is not meant for playing.
* **CEF harness** (`Tools/CefHarness`, `eng/cef-harness.sh`): starts the browser host the way the game does — under
  Proton in the game's Wine prefix on Linux — creates a browser, serves a local resource page, reads its pixels
  from shared memory and waits for the page's `resourceCall`; the acceptance test of the browser without a game.
  Its `--in-process` and `--appdomain` modes reproduce the in-game crash of the in-process design in a second;
  `--bench <s> --size WxH` measures frames/s, copy cost and CPU on an animated page, with `--gpu` for comparison.
* **Dev container** (`.devcontainer/`, `docker-compose.yml`): the .NET 8 SDK plus `eng/dev-build-client.sh`
  and `eng/dev-sync-client.sh` rebuild the managed client and the browser host on Linux in seconds and drop
  them into an existing `~/GTANetwork` install, so a change can be tried in game without waiting for CI or a
  release. `eng/dev-test.sh` runs the Linux CI checks (server smoke test + headless-bot integration) locally.
  See `docs/DEVCONTAINER.md`.

### Removed
* Dead code and unused binaries (T-020): the legacy client sources that were excluded from the build (`Client/Networking/*Sync*`,
  root `Chat.cs`/`ClassicChat.cs`, `Main/Math.cs`, `Misc/Program.cs`, `Util/DebugWindow.cs`, the D3D10 hooks), the unused
  `libs/` DLLs (`EasyHook.dll`, `Interop.WMPLib.dll`, `Ionic.Zip.dll`, Owin/Nancy, `NAudio.WindowsMediaFormat.dll`,
  `Newtonsoft.Json.dll`, `protobuf-net.dll` — NuGet supplies them), the root `natives.txt` copy and the placeholder `whitelist.txt`.

### Not yet
* Server, launcher and bot stay on .NET 8; the client stays on .NET Framework 4.8 (ScriptHookVDotNet hosts the
  desktop CLR). The route to .NET 10 for the client is in `docs/ROADMAP.md`.

## [0.1.1] - 2026-09-04

Resource files (`<file src="..."/>` in `meta.xml`: CEF pages, images, sounds) now actually reach the client, and
the CEF overlay no longer takes the game down, so browser UIs such as the `auth` login form work. **Verified in
game** (GTA V Legacy 1.0.3889 under Proton): the login form appears over the world, the cursor moves, an account
was registered through the form, the player is released after login. Found with the first in-game CEF test of
0.1.0: the browser was created, `https://auth/ui/index.html` was "File does not exist" and nothing was drawn.
Pre-releases `0.1.1-alpha.1` … `alpha.3` were the steps to get here.

### Fixed: resource files never reached the client
* **HTTP file server mode** (`<httpserver>true</httpserver>`, the default): the download thread was created
  but never started; a file in a sub-folder (`ui/index.html`) had no folder to be written to; errors of the
  async download were dropped. The download now runs on a started background thread, creates the folders, logs a
  summary to `Runtime.log` and shows the progress in the loading prompt. An inherited upstream bug: the 2017
  client had the same code.
* **Client scripts started before the files arrived**: the end-of-transfer marker (UDP: map + scripts) now waits
  for the HTTP download to finish before `onResourceStart` runs, both on connect and when a resource starts while
  playing (`RedownloadManifest`). A failed download no longer blocks the scripts; it is reported instead.
* **CEF overlay stayed off**: browsers and the cursor are drawn only while the global draw switch is on, and only
  `API.setCefDrawState(true)` turned it on. `createCefBrowser` enables it now (`setCefDrawState(false)` still hides
  everything); the `auth` script also calls it explicitly for older clients.
* `waitUntilCefBrowserInit` gives up after 15 s with a log line instead of spinning forever.
* The mime whitelist for downloaded files listed names the sniffer never produces (`audio/wav`, `video/avi`);
  WAV, AVI, OGG and ICO files were always rejected. Both spellings are accepted now.
* Downloads that would leave `resources/<resource>/` (`..`, rooted paths, drive letters) are refused on both the
  UDP and the HTTP path.

### Fixed: the game died a few seconds after the first CEF page (alpha.1)
* The overlay that draws browsers and the cursor runs in the game's present callback and wrapped the game's swap
  chain in a new SharpDX object every frame without owning a reference, while
  `Configuration.EnableReleaseOnFinalizer` made the finalizer call `Release()` on each of them: the first garbage
  collection after the page appeared released the swap chain from under the game (fatal at once under DXVK, a
  latent bug on Windows). One wrapper per swap chain now, with its own reference, and both SharpDX debugging
  switches (object tracking, release on finalizer) are off.
* The overlay leaked one texture reference per drawn image per frame (`ResourceAs` without dispose), kept a view
  on the back buffer across frames and copied the CEF paint buffer by pointer although CEF owns it only during the
  paint callback. It now creates the render target view per frame, releases it before `Present` returns and copies
  the paint buffer. (alpha.2 also disposed the device's cached immediate context wrapper after the first frame,
  which turned the screen black; alpha.3 undid that.)
* An exception in a `Present` handler of a script ended the process; ScriptHookVDotNet now logs it (first five)
  and continues, and the overlay handler checks for a missing menu or warning object.

### Added
* `GTANetworkShared.ResourceFileDownloader`: the manifest download shared by the game client and the headless
  bot (`--download-files <dir>`), so CI runs the same code as the game.
* `eng/integration-test-auth.sh` checks the HTTP file server directly (manifest lists the page, the three files
  are served byte for byte, the server script and a path traversal are refused) and that the bot ends up with
  complete copies of the `auth` UI files.
* Diagnostics in `Runtime.log`: `Resource files from http://...: N downloaded ...`, `CEF overlay: initialised on
  swap chain ... (feature level, back buffer, context)`, the geometry of the first three overlay frames and
  `[PROFILE] Present hook overlay: ... errors so far` every 10 s; `CEF.log` logs the first three paints of a
  browser; overlay exceptions go to `Error.log` (first five).

### Docs and release process
* README (EN/UK): how resource files travel and where they land; `docs/ROADMAP.md` phase 0 status and the plan
  for the next updates (dependency modernisation, Linux GUI launcher, debug logging mode, CEF connect screen).
* A pre-release tag without its own changelog section (`v0.1.1-alpha.1`) gets the notes of its base version
  (`## [0.1.1]`) as release body instead of a placeholder.

## [0.1.0] - 2026-09-04

The first stable release of the revived GTA Network. It sums up the `0.1.0-alpha.1` … `0.1.0-alpha.24`
pre-releases listed below; nothing changed between alpha.24 and this tag.

**Verified in game** on GTA V Legacy 1.0.3889 under Proton (Debian 13): install with one command, connect to a
local server, player and vehicle sync, chat, commands, client-side JavaScript with the `API` object, 60 fps with
2.5 ms of script time per frame. Multi-player relay and the server API are covered by the headless bot in CI.
Not yet verified: two real players on one server, the Windows installer path, the CEF browser UI in game.

### What you get
* **Server** on .NET 8, native on Linux (and Windows): C#/VB resources compiled at startup with Roslyn,
  `freeroam` gamemode, `example` resource, `auth` accounts resource (disabled by default), HTTP file server,
  clean shutdown on signals.
* **Client** for GTA V Legacy through ScriptHookV + a ScriptHookVDotNet fork that survives game updates
  (missing memory patterns disable a feature instead of crashing; fallback patterns for builds since
  1.0.3788), with a per-script profiler in the log.
* **Cross-platform launcher** (Linux/Proton and Windows) that deploys the mod into the game folder with a
  rollback manifest and restores it afterwards.
* **Linux installer and updater** (`setup-linux.sh`): installs client, launcher, server and bot from a GitHub
  release into `~/GTANetwork`, puts .NET Framework 4.8 and VC++ 2022 into the game's Proton prefix (all known
  Proton pitfalls handled), and updates itself and everything else before every start.
* **Headless bot** that joins a server over the real protocol; used by the CI integration tests.
* **Docs**: README (EN/UK), architecture and flow, `docs/ROADMAP.md`, `docs/SYNC.md`, `docs/CEF-UPGRADE.md`.

### Fixed since the 2019-2020 code base
* Crash a few seconds after the main menu (dead master server answered with HTML on a background thread).
* Client-side JavaScript never worked: the managed ClearScript, its native bridge and the V8 library were from
  three different builds; the official ClearScript.V8 5.4.9 set is shipped now.
* Once-per-second stutter (debug overlay pool scans, weapon sweep); thread hand-offs poll before blocking.
* Server relay sent every packet to every player regardless of distance and dimension; near/far by distance.
* Vehicles and peds were never deleted on stream-out; streaming died on the first exception; several
  divisions by zero, clock mismatches and leaks in the remote player code (see `docs/SYNC.md`).
* Server: broadcast native calls threw with two players, infinite recursion in one API overload, AFK players
  leaked entities, race on the player list, `Client` without `GetHashCode`.

### Known limitations
* ScriptHookV is not redistributable: download it from dev-c.com yourself.
* The master server (`master.gtanet.work`) is gone; the address is empty by default (issue #1).
* The bundled browser is CEF 3.2987 (Chromium 57, 2017).
* Two memory patterns are still missing on 1.0.3889 (euphoria, unused; "force offline" patch).
* GTA V Enhanced is not supported.

## [0.1.0-alpha.24] - 2026-09-04

### Fixed
* Client-side JavaScript could not see any member of any host object (`API.sendNotification` was `undefined`,
  so every client script failed at its first line): `libs/v8-x64.dll` was V8 5.4.500.40 from ClearScript
  5.4.6/5.4.7 while the native bridge `ClearScriptV8-64.dll` was a 5.4.9 build made for V8 5.5.372.40. The
  three files are now the official ClearScript.V8 5.4.9 package (managed assembly, bridge, V8 5.5.372.40).

## [0.1.0-alpha.23] - 2026-09-03

### Added
* `auth` resource (`Server/resources/auth`, disabled by default in `settings.xml`): accounts with registration
  and login through a CEF form (`client.js` + `ui/`) or `/register` and `/login`; chat and every other command
  are cancelled and the player stays frozen until logged in. Passwords are stored as salted PBKDF2-SHA256
  hashes in `accounts.json`; other resources read `API.getEntityData(player, "auth:account")`.
  `eng/integration-test-auth.sh` drives it with the bot in CI.

* `docs/SYNC.md`: review of the synchronization, relay and streaming code with the fixes below and the open
  items; `docs/CEF-UPGRADE.md`: plan for replacing the 2017 CEF build.

### Fixed
* Resource scripts under `Server/resources` were also compiled into the server assembly by the SDK's default
  source glob; they are now excluded and only compiled by the server at runtime.
* Docs: the bundled browser is CEF 3.2987 (Chromium 57, 2017), not CEF 85.
* Server sync relay: near/far recipients are chosen by distance (2500 m) and dimension instead of rank, so
  a full-map server no longer relays every packet to every player; the basic packet is only built when
  someone is far; `Fake`/null-connection guards in every relay; the throttle uses a monotonic clock and is
  pruned on disconnect; AFK players are torn down like real disconnects (entities leaked before);
  `sendNativeToPlayersInRangeInDimension(Hash)` recursed forever; `Client` got a `GetHashCode`;
  `getAllPlayers()` copies under the lock; unoccupied-vehicle packets are parsed in O(n).
* Client sync/streaming: vehicles and peds were never deleted on stream-out (`Prop.Exists()` type check);
  `Count(Type)` always returned 0; four stream-out filters had a precedence bug; an exception in the
  streamer ended streaming for the session; stale-data guard in `Vehicle.cs` compared two different clocks;
  divisions by zero before the second packet; unbounded ragdoll/parachute extrapolation; `AimPlayer` null
  dereference; leaked aim prop; nametag line-of-sight traces for every player every frame; sender thread
  spinning a core while connecting; racy counters; duplicate natives in the ped collector; `ModelRequest`
  stuck after an exception; lazy `ClientMap` enumeration across yields.

## [0.1.0-alpha.22] - 2026-09-03

### Added
* `[PROFILE] Present hook overlay` line in `Runtime.log` every 10 s: time spent in the DirectX overlay inside
  `Present` (render thread), the part of the frame the script profiler cannot see.
* The API probe enumerates the members JavaScript can see on `API` and compares with a one-method probe host
  object, to tell "ClearScript exposes nothing" from "ScriptContext is special".

## [0.1.0-alpha.21] - 2026-09-03

### Changed
* ScriptHookVDotNet thread hand-offs poll for up to 100 µs before blocking. Every native call from a script
  and every script tick is a round trip between the game thread and the script thread; with kernel events
  each round trip paid the wake-up latency of a sleeping thread (tens of microseconds, more under wine),
  which multiplied by a few hundred native calls per frame was a large share of the frame time.

## [0.1.0-alpha.20] - 2026-09-03

### Added
* `CHANGELOG.md` (this file) and `docs/ROADMAP.md` (the plan towards a RAGE Multiplayer 0.3.7-class platform).
* The GitHub release body is taken from the changelog section that matches the release tag.
* ScriptHookVDotNet profile summary: every 10 s one `[PROFILE]` line with frames, fps, script time per frame and
  the ten most expensive scripts (average and worst tick, native calls per tick), so a performance report can
  quote numbers instead of impressions.
* Client-side script errors now log the V8 error details (script line and stack) and the resource/file name; an
  "API probe" line in `Runtime.log` says whether `API` and its events are visible to JavaScript before a script
  starts.

## [0.1.0-alpha.19] - 2026-09-03

### Fixed
* freeroam `client.js`: the `onChatCommand` handler declared `(command, cancel)` although the API delivers one
  argument (the chat line) and cannot cancel it; typing `/ping` threw a TypeError in the game. `/ping` is now
  a server-side command and the client script reports "Client-side script is running" in chat through a
  `freeroam:clientReady` event, so a working JavaScript chain is visible.

## [0.1.0-alpha.18] - 2026-09-03

### Fixed
* The once-per-second stutter reported in game. The slow-tick profiler (alpha.15) blamed two scripts:
  `DebugInfo` ran five full entity pool scans every 500 ms (~170 ms per refresh) for numbers only shown by the
  streamer overlay, and `WeaponManager` removed every weapon hash in one go every 500 ms (~100 native calls,
  25-30 ms). The scans now run only while the overlay is on; the weapon sweep covers eight hashes per update.

### Changed
* README: in-game verification on GTA V Legacy 1.0.3889 under Proton, memory pattern fallbacks documented.

## [0.1.0-alpha.17] - 2026-09-03

### Fixed
* ScriptHookVDotNet on game build 1.0.3889: the classic memory patterns for the entity pool, the camera pool and
  the loading-text hook no longer matched, so `World.GetAllVehicles/Peds/Props` returned nothing. The scan now
  tries the classic pattern first and then the variant upstream ScriptHookVDotNet uses since build 1.0.3788;
  the text hook falls back to the hash-based label lookup with image-range checks. The log says which variant
  matched. Still missing on 1.0.3889: the euphoria functions (unused by GTA Network) and the "force offline" patch.

## [0.1.0-alpha.16] - 2026-09-03

### Fixed
* Client-side JavaScript never started: the ClearScript.V8 NuGet package (5.4.6) shipped a managed
  `ClearScript.dll` whose version did not match the native bridge `ClearScriptV8-64.dll` (5.4.9) from `libs/`,
  so `V8ScriptEngine` threw `FileLoadException` ("CLIENTSIDE SCRIPT ERROR" for every resource with JS). The client
  now references `libs/ClearScript.dll` 5.4.9.

## [0.1.0-alpha.15] - 2026-09-03

### Added
* ScriptHookVDotNet slow-tick profiler: a script that holds the game thread for 20 ms or more in one tick is
  named in `ScriptHookVDotNet-*.log` ("held the game thread for N ms"), at most once per 5 s per script.

## [0.1.0-alpha.14] - 2026-09-03

### Fixed
* Linux updater stuck on an old release: GitHub lists releases by tag name, so `alpha.9` sorted after
  `alpha.13`. The newest non-draft release by `published_at` is used now.

## [0.1.0-alpha.13] - 2026-09-03

### Changed
* `MasterServerAddress` in `settings.xml` is the only source of the master server address (the menu ignored it
  in favour of a constant) and it is empty by default: `master.gtanet.work` is gone. Favourites, recent servers
  and LAN discovery keep working without a master. The Linux installer blanks the old default in existing
  settings. Rebuilding the master server is tracked in issue #1.

## [0.1.0-alpha.12] - 2026-09-03

### Fixed
* The game crashed a few seconds after the main menu appeared: the welcome-message fetch caught `WebException`
  only, and the Cloudflare page now served under the old master address produced a JSON parse error on a
  background thread, which terminates GTA5.exe. Every background thread (welcome message, whitelist,
  screenshots) now catches everything, and `AppDomain.UnhandledException` is written to `logs/Error.log`.
* The blind write to script global 2576573 ("enable MP vehicles") used a 2016 index and corrupts memory on
  current builds; it is behind the new `EnableMpVehiclesGlobal` setting, off by default.

## [0.1.0-alpha.11] - 2026-09-03

### Changed
* Linux installer: the game's own Proton is tried first for the .NET install again (an old cabinet failure in
  the log no longer skips it), because the failure was caused by Proton's symlinks, which are now replaced.

## [0.1.0-alpha.10] - 2026-09-03

### Fixed
* Linux installer: every file symlink in `system32`, `syswow64` and `Microsoft.NET` of the prefix is replaced by
  a real copy before the .NET install (Proton links them to its builtin DLLs and the installer cannot overwrite
  them: "Failed to extract cabinet netfx_core.mzz"). The install runs with `WINEDEBUG=warn+msi` so the file the
  installer could not write is named after a failed attempt.

## [0.1.0-alpha.9] - 2026-09-03

### Fixed
* Linux installer: first version of the symlink fix (a hand-picked list of `Microsoft.NET` support files).

## [0.1.0-alpha.8] - 2026-09-03

### Fixed
* Linux installer: a freshly unpacked GE-Proton has no Steam Runtime container yet; protontricks is retried
  with `--no-runtime`.

## [0.1.0-alpha.7] - 2026-09-03

### Added
* Linux installer: downloads GE-Proton8-32 (checksum verified) into `compatibilitytools.d` when no stable Proton
  is installed and the .NET installer cannot extract its cabinets; `--dotnet-proton <name|auto>`; the Proton
  that worked is remembered in `setup.conf`; the prefix's Windows version is reset to 10 after winetricks.

## [0.1.0-alpha.6] - 2026-09-03

### Added
* Linux installer: retry the .NET install with Proton 8.0 / 9.0 / 10.0 from any Steam library when the cabinet
  extraction fails; free-space check; `logs/protontricks.log`.

## [0.1.0-alpha.5] - 2026-09-03

### Fixed
* Linux installer: leftover `GTA5.exe` / Rockstar Launcher processes in the prefix made `wineserver -w` wait
  forever; they are listed and can be stopped first. Retry without the Steam Runtime container; quieter
  `/proc` scan.

## [0.1.0-alpha.4] - 2026-09-03

### Changed
* Linux installer: protontricks and winetricks are installed into a private Python venv first (installing
  `python3-venv` and `cabextract` when needed); the Debian `contrib` route reports every APT source it examined.

## [0.1.0-alpha.3] - 2026-09-03

### Added
* Linux auto-updater: `update.sh` compares `release.txt` with the newest GitHub release and installs it;
  `play.sh`, `server/start.sh` and `bot.sh` update first (`GTAN_NO_UPDATE=1` skips). The installer keeps a copy
  of itself and its options (`setup.conf`) and updates itself before applying an update. Nothing is replaced
  while a server, bot or launcher from the install folder is running; `server/settings.xml`, the player name and
  ScriptHookV are kept.
* protontricks is installed on demand (Debian `contrib` handling, venv and Flatpak fallbacks).
* The Legacy `ScriptHookV.dll`/`dinput8.dll` is preferred when the ScriptHookV archive ships both builds.

## [0.1.0-alpha.2] - 2026-09-03

### Fixed
* Linux installer: the release JSON was read from stdin that a heredoc had shadowed.

## [0.1.0-alpha.1] - 2026-09-03

The first build of the revival. Everything below compares with the 2019-2020 code base.

### Added
* **Build system**: SDK-style projects with NuGet references and one `GTANetwork.sln`; the version is computed
  from the commit date (`eng/version.sh`, `Directory.Build.props`). Client, NativeUI and the classic launchers
  (net48) compile on any OS through the reference assemblies; `Shv.NET/ref` is a managed stub of the
  ScriptHookVDotNet API so the in-game client builds without MSVC.
* **Server on .NET 8**, native on Linux: Roslyn for runtime C#/VB resources, POSIX signals for clean shutdown,
  an `HttpListener` file server (replaces Nancy/OWIN), no `Thread.Abort`, SHA256 file hashes, a
  `settings.xml`, the `example` and `freeroam` resources.
* **Cross-platform launcher** (net8.0): finds Steam, GTA V, the Proton prefix and Proton from the VDF files,
  deploys the ASI loader files with a manifest for rollback, patches the profile, starts the game through
  Steam or Proton, restores the folder afterwards; `doctor` command.
* **ScriptHookVDotNet fork**: builds without the official SDK (`Shv.NET/sdk-compat`), VS2022 toolset; every
  memory pattern goes through a checked lookup, so a pattern that no longer matches the installed game build is
  logged and its feature disabled instead of crashing the game.
* **Headless bot** (`Tools/GTANetwork.Bot`): joins a server over the real protocol (discovery, handshake, file
  transfer, chat, commands, position sync) and decodes what the server pushes; interactive stdin mode.
* **CI** (`.github/workflows/build.yml`): Linux job (build, publish, server smoke test, bot integration test
  with one and two players), Windows job (C++/CLI hook, client package, NSIS installer), releases from tags or
  a manual run with `release_tag`; Linux builds are self-contained.
* **Linux one-shot installer** `eng/setup-linux.sh`: installs client, launcher, server and bot from a GitHub
  release into `~/GTANetwork`, extracts ScriptHookV from the user's zip, writes `settings.xml`, installs
  .NET 4.8 and VC++ 2022 into the game prefix, creates `play.sh`, `server/start.sh`, `bot.sh` and a desktop entry.
* README in English and Ukrainian: architecture, flow, building, Linux/Proton guide.

### Fixed
* Server: broadcasting a native call to all players reused one Lidgren message and threw on the second
  recipient, so any `setEntityPosition`/`setTime`-style API call failed with two players online.
* Server: the sync relay threw with an empty recipient list when a single player was online.

[Unreleased]: https://github.com/SashaGoncharov19/FreeMode/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0
[0.1.0-alpha.24]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.24
[0.1.0-alpha.23]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.23
[0.1.0-alpha.22]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.22
[0.1.0-alpha.21]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.21
[0.1.0-alpha.20]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.20
[0.1.0-alpha.19]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.19
[0.1.0-alpha.18]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.18
[0.1.0-alpha.17]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.17
[0.1.0-alpha.16]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.16
[0.1.0-alpha.15]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.15
[0.1.0-alpha.14]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.14
[0.1.0-alpha.13]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.13
[0.1.0-alpha.12]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.12
[0.1.0-alpha.11]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.11
[0.1.0-alpha.10]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.10
[0.1.0-alpha.9]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.9
[0.1.0-alpha.8]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.8
[0.1.0-alpha.7]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.7
[0.1.0-alpha.6]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.6
[0.1.0-alpha.5]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.5
[0.1.0-alpha.4]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.1
