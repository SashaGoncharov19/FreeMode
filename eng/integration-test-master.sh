#!/usr/bin/env bash
# Fourth end-to-end test: a master list and a game server that announces to it. The master must list the server after its
# announce (a discovery ping to the server's UDP port), refuse a fake server and a second token for the same address.
# Usage: eng/integration-test-master.sh <server dir (published or bin)> <master dir (published)>
set -euo pipefail
src_dir="${1:?server directory}"
master_dir="${2:?master directory}"
port="${GTAN_TEST_PORT:-4499}"
master_port="${GTAN_TEST_MASTER_PORT:-5890}"

work="$(mktemp -d)"
server_dir="$work/server"
cp -r "$src_dir" "$server_dir"
python3 - "$server_dir/settings.xml" "http://127.0.0.1:$master_port" <<'PY'
import sys, re
p, master = sys.argv[1], sys.argv[2]
s = open(p).read()
s = re.sub(r'<master>.*?</master>\s*', '', s)
s = re.sub(r'<announce>.*?</announce>', '<announce>true</announce>\n  <master>' + master + '</master>', s, count=1)
if '<master>' not in s:
    s = s.replace('</config>', '  <announce>true</announce>\n  <master>' + master + '</master>\n</config>')
open(p, 'w').write(s)
PY
grep -q "<master>http://127.0.0.1:$master_port</master>" "$server_dir/settings.xml" || { echo "could not set the master in settings.xml"; exit 1; }

export MASTER_DATA="$work/master-data" ASPNETCORE_URLS="http://127.0.0.1:$master_port" MASTER_TTL_SECONDS=180
pushd "$master_dir" >/dev/null
if [ -x ./GTANetwork.Master ]; then master_cmd=(./GTANetwork.Master); else master_cmd=(dotnet GTANetwork.Master.dll); fi
"${master_cmd[@]}" > "$work/master.log" 2>&1 &
master_pid=$!
popd >/dev/null

pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
"${server_cmd[@]}" > it-server.log 2>&1 &
server_pid=$!
popd >/dev/null

cleanup() {
  for pid in "$server_pid" "$master_pid"; do
    if kill -0 "$pid" 2>/dev/null; then kill -TERM "$pid" 2>/dev/null || true; fi
  done
  for _ in $(seq 1 15); do sleep 1; kill -0 "$server_pid" 2>/dev/null || kill -0 "$master_pid" 2>/dev/null || break; done
  kill -9 "$server_pid" "$master_pid" 2>/dev/null || true
}
trap cleanup EXIT

for _ in $(seq 1 30); do sleep 1; curl -fsS -m 2 "http://127.0.0.1:$master_port/health" >/dev/null 2>&1 && break; done
curl -fsS -m 2 "http://127.0.0.1:$master_port/health" >/dev/null || { echo "the master did not start:"; cat "$work/master.log"; exit 1; }
for _ in $(seq 1 40); do
  sleep 1
  grep -q "Started! Waiting for connections." "$server_dir/it-server.log" && break
  kill -0 "$server_pid" 2>/dev/null || { echo "server died:"; cat "$server_dir/it-server.log"; exit 1; }
done
grep -q "Started! Waiting for connections." "$server_dir/it-server.log" || { echo "server did not start:"; cat "$server_dir/it-server.log"; exit 1; }

# the server announces at start; the master pings it and lists it
listed=0
for _ in $(seq 1 20); do
  sleep 1
  if curl -fsS -m 2 "http://127.0.0.1:$master_port/servers" | grep -q "\"127.0.0.1:$port\""; then listed=1; break; fi
done
echo "servers: $(curl -fsS -m 2 "http://127.0.0.1:$master_port/servers")"
[ "$listed" = 1 ] || { echo "FAIL: the game server did not appear on the master after its announce"; echo "---- master log ----"; cat "$work/master.log"; echo "---- server log ----"; grep -i "master\|announc" "$server_dir/it-server.log" || true; exit 1; }
full=$(curl -fsS -m 2 "http://127.0.0.1:$master_port/servers/full"); echo "full: $full"
[[ "$full" == *'"publicKey":"'* && "$full" != *'"publicKey":""'* ]] || { echo "FAIL: the announce carried no public key"; exit 1; }
[ -s "$server_dir/master.token" ] || { echo "FAIL: master.token was not created next to the server"; exit 1; }
grep -q "master.token created" "$server_dir/it-server.log" || { echo "FAIL: the server did not log the token creation"; exit 1; }
if grep -q "Failed to announce\|refused the announce\|does not list this server" "$server_dir/it-server.log"; then echo "FAIL: the server logged an announce problem:"; grep -i "master" "$server_dir/it-server.log"; exit 1; fi

# the curl cases: a second real announce with another token is refused, a fake server is not listed
"$(dirname "$0")/../Tools/GTANetwork.Master/tests/announce.sh" "http://127.0.0.1:$master_port" "127.0.0.1:$port" "$(cat "$server_dir/master.token")"
echo "master integration test passed"
