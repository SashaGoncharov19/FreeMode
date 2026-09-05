#!/usr/bin/env bash
# Announces a running game server and a fake one to a master and checks what gets listed.
# Usage: tests/announce.sh <master url> <game server host:port> [token]
set -euo pipefail
master="${1:?master url}"; server="${2:?game server host:port}"; token="${3:-test-token-12345678}"
host="${server%:*}"; port="${server##*:}"
curl -fsS -m 5 "$master/health" >/dev/null || { echo "master not reachable at $master"; exit 1; }

echo "-- announce the real server ($server)"
# the master takes one announce per address per 10 s (429 otherwise): a server that has just announced by itself makes us wait
body="{\"ServerName\":\"Announce test\",\"CurrentPlayers\":1,\"MaxPlayers\":32,\"Gamemode\":\"freeroam\",\"Map\":\"\",\"Port\":$port,\"Passworded\":false,\"fqdn\":\"$host\",\"ServerVersion\":\"0.2.0\",\"PublicKey\":\"00ff\",\"Token\":\"$token\"}"
for attempt in 1 2 3 4 5 6; do
  code=$(curl -s -m 15 -o /tmp/announce-real.$$ -w '%{http_code}' -X POST "$master/addserver" -H 'Content-Type: application/json' -d "$body")
  [ "$code" = "429" ] || break
  echo "   (429: announced less than 10 s ago, waiting)"; sleep 3
done
real=$(cat /tmp/announce-real.$$); rm -f /tmp/announce-real.$$
echo "$real"
[ "$code" = "200" ] || { echo "FAIL: the real announce returned HTTP $code"; exit 1; }
[[ "$real" == *'"listed":true'* ]] || { echo "FAIL: the real server was not listed after the announce"; exit 1; }

echo "-- announce a fake server (nothing listens on 127.0.0.1:4599)"
fake=$(curl -fsS -m 15 -X POST "$master/addserver" -H 'Content-Type: application/json' \
  -d '{"ServerName":"Nobody","CurrentPlayers":0,"MaxPlayers":8,"Gamemode":"x","Port":4599,"Passworded":false,"fqdn":"127.0.0.1","ServerVersion":"0","Token":"fake-token-1234"}')
echo "$fake"
[[ "$fake" == *'"listed":false'* ]] || { echo "FAIL: the fake server was listed"; exit 1; }

echo "-- another token for the real address must be refused"
code=$(curl -s -m 15 -o /dev/null -w '%{http_code}' -X POST "$master/addserver" -H 'Content-Type: application/json' \
  -d "{\"ServerName\":\"Hijack\",\"CurrentPlayers\":0,\"MaxPlayers\":8,\"Gamemode\":\"x\",\"Port\":$port,\"Passworded\":false,\"fqdn\":\"$host\",\"ServerVersion\":\"0\",\"Token\":\"other-token-9999\"}")
[ "$code" = "403" ] || { echo "FAIL: a different token was accepted (HTTP $code)"; exit 1; }

list=$(curl -fsS -m 5 "$master/servers"); echo "servers: $list"
[[ "$list" == *"\"$server\""* ]] || { echo "FAIL: /servers does not contain $server"; exit 1; }
[[ "$list" != *'127.0.0.1:4599'* ]] || { echo "FAIL: /servers contains the fake server"; exit 1; }
full=$(curl -fsS -m 5 "$master/servers/full"); echo "full: $full"
[[ "$full" == *'"name":"Announce test"'* && "$full" == *'"publicKey":"00ff"'* ]] || { echo "FAIL: /servers/full lacks the announced fields"; exit 1; }
stats=$(curl -fsS -m 5 "$master/stats"); echo "stats: $stats"
[[ "$stats" == *'"TotalServers":1'* ]] || { echo "FAIL: /stats does not count one server"; exit 1; }
echo "master announce test passed"
