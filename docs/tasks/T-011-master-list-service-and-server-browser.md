# T-011 — Master list service, server announce, server list in the client menu

Status: needs owner (implemented; Q-07 domain + host, then the in-game list check)
Epic: E-07 Master list
Size: L
Branch: task/T-011-master-list from the integration branch
Depends on: T-001
PR: yes

## Goal

`Tools/GTANetwork.Master` (ASP.NET Core minimal API, .NET 10, SQLite): servers announce every 60 s with a token,
`GET /servers` lists them (name, address, players/max, gamemode, map, version, public key, verified), the master pings
the announced UDP port before listing, `GET /verified`, `GET /stats`, `GET /welcome.json`; the server announces to a
configurable address; the in-game server browser and (later) the launcher read the list.

## Files

* New: `Tools/GTANetwork.Master/` (`Program.cs` minimal API, `Db.cs` SQLite via `Microsoft.Data.Sqlite`, `Dockerfile`,
  `README.md` with the deploy command), `Tools/GTANetwork.Master/tests/announce.sh` (curl-based test used by `eng/dev-test.sh`).
* Change: `Shared/ServerSettings.cs` (`MasterServer` field, default `https://master.<owner's domain>` — Q-07: the domain is
  the owner's; until then empty = no announce), `Server/GameServer.cs:115` (read from settings instead of the hardcoded
  `http://master.gtanet.work`), `:263` (`AnnounceSelfToMaster`: new JSON fields, token from `settings.xml`, 60 s interval),
  `Shared/MasterServerAnnounce.cs`, `Client/Main/Menu.cs:234–:292` (endpoint shapes, verified/players columns),
  `Shared/PlayerSettings.cs` (`MasterServerAddress` default = the same domain when set), `Subprocess/GTANSubprocess/EntryPoint.cs`
  and `PlayGTANetworkUpdater/Program.cs` (dead master URLs → the setting or removed with Q-13), `docs/CODEMAP.md` §10, `CHANGELOG.md`.

## Acceptance criteria

- [x] `Tools/GTANetwork.Master/tests/announce.sh` passes against a running master (announce → listed after a successful ping; unreachable server not listed; another token refused). The Dockerfile builds the same binary.
- [x] `eng/dev-test.sh` (and CI) run `eng/integration-test-master.sh`: a master and a game server announcing to it; the server is listed with its public key, `master.token` is created and logged.
- [ ] Owner check: the in-game server browser lists the owner's server from the master (with `MasterServerAddress` set).
- [ ] Deployment: `needs owner` — a domain and a host (Q-07); the task documents the one-command deploy.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 06:30 agent — service, server announce, client list and the integration test written; `eng/dev-test.sh` green; PR opened. Needs the owner for Q-07 and the in-game check.

## Result

* `Tools/GTANetwork.Master`: minimal API + SQLite (`servers` table), `POST /addserver` (and `/servers/announce`) with a
  Lidgren discovery ping before listing, token ownership per address (403), 10 s announce floor (429), `GET /servers`,
  `/verified`, `/stats`, `/welcome.json` in the 2016 shapes, `GET /servers/full` rich rows, `/health`; `MASTER_DATA`,
  `MASTER_PING`, `MASTER_TTL_SECONDS`; `verified.txt`; Dockerfile; published as `gtanetwork-master-linux-x64` by CI.
* Server: `<master>` in `settings.xml` (empty = no announce, the hardcoded `master.gtanet.work` is gone), announce every 60 s
  with `PublicKey` and the `master.token` token; the master's refusal or "not listed" answer is logged.
* Client: `/servers/full` feeds `CefMenu` (names, players, version, a lock icon for servers whose key the master knows); the
  key is pinned when connecting from the list. `PlayerSettings.MasterServerAddress` stays empty until Q-07 gives a domain.
* Not done here: the classic Windows launcher's updater URLs (Q-13 removes those projects); the launcher window does not
  read the list yet (the in-game menu does).

### Owner check

1. Run a master somewhere reachable (the README's `docker run ... gtanetwork-master` line, or `dotnet GTANetwork.Master.dll`
   from the `gtanetwork-master-linux-x64` artifact with `ASPNETCORE_URLS=http://0.0.0.0:8080`).
2. In `~/GTANetwork/server/settings.xml`: `<announce>true</announce>` and `<master>http://<host>:8080</master>`; restart the
   server. Its log must show `master.token created` once; `curl http://<host>:8080/servers/full` lists the server with its
   `publicKey`. Bad: `Master list refused the announce` / `does not list this server` (the UDP port is not reachable from the master).
3. In `~/GTANetwork/settings.xml`: `<MasterServerAddress>http://<host>:8080</MasterServerAddress>`; start the game. The menu's
   status line says `N server(s) on the master list`, the server appears with a lock icon and its version; connect from the
   list — `Runtime.log` shows `session: ... pinned` for that connection. Bad: `master: /servers/full not available` in `Runtime.log`.
4. Decide Q-07: the domain and the host that become the defaults for `<master>` and `MasterServerAddress`.
