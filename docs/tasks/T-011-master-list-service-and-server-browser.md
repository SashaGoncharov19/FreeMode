# T-011 — Master list service, server announce, server list in the client menu

Status: ready
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

- [ ] `docker run gtanetwork-master` + `Tools/GTANetwork.Master/tests/announce.sh` passes (announce → listed after a successful ping; unreachable server not listed).
- [ ] `eng/dev-test.sh` runs the master locally and the server's announce reaches it (log lines both sides).
- [ ] Owner check: the in-game server browser lists the owner's server from the master (with `MasterServerAddress` set).
- [ ] Deployment: `needs owner` — a domain and a host (Q-07); the task documents the one-command deploy.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
