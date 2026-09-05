# GTA Network master list

Servers announce themselves here every minute; the launcher and the in-game menu read the list.

| Endpoint | What |
| --- | --- |
| `POST /addserver` (also `/servers/announce`) | The announce: the JSON of `Shared/MasterServerAnnounce.cs` (name, players, max, gamemode, map, port, passworded, `fqdn`, version, `PublicKey`, `Token`). The address is `fqdn:port` when a host name is given, else the caller's IP and the port. The first token announcing an address owns it; another token gets 403. At most one announce per 10 s per address. Before listing, the master sends a Lidgren discovery request to the announced UDP port (`MASTER_PING=0` skips that). |
| `GET /servers` | `{ "list": ["host:port", …] }` — the 2016 shape the clients read. Only servers that answered the ping and announced within `MASTER_TTL_SECONDS` (180). |
| `GET /servers/full` | `[{address, name, players, maxPlayers, gamemode, map, passworded, version, publicKey, verified, lastSeen}]` — what the CEF menu shows; `publicKey` lets the client pin the server's key (T-009) without typing it. |
| `GET /verified` | `{ "list": [...] }` of the addresses in `verified.txt` (one `host:port` per line, reloaded every 30 s). |
| `GET /stats` | `{ TotalPlayers, TotalServers }`. |
| `GET /welcome.json` | `welcome.json` from the data folder, or a default `{Title, Message, Picture}`. |
| `GET /health` | `{ ok, uptimeSeconds, servers }`. |

Configuration: `MASTER_DATA` (folder for `master.db`, `verified.txt`, `welcome.json`; default `./data`), `ASPNETCORE_URLS`
(default `http://0.0.0.0:8080`), `MASTER_PING` (`0` = list without the discovery check), `MASTER_TTL_SECONDS`.

Run it: `dotnet run --project Tools/GTANetwork.Master` (development), or the Docker image (see `Dockerfile`: build from the
repository root, run with a volume on `/data`, put TLS in front). Then in the game server's `settings.xml`:
`<master>https://master.example.org</master>` (the server announces every 60 s and logs the master's answer), and in the
players' `settings.xml` `<MasterServerAddress>https://master.example.org</MasterServerAddress>` — the launcher and the menu read it.

`tests/announce.sh <master url> <game server host:port>` announces a real server and a fake one and checks the lists
(`eng/integration-test-master.sh` runs it against a freshly started master and server).
