#!/usr/bin/env bash
# Sixth end-to-end test (T-017): a server with <anticheat action="kick"> kicks a bot that moves at 200 m/s on foot and one that
# teleports, raises the event (the freeroam TypeScript gamemode logs it), and leaves a normal bot alone.
# Usage: eng/integration-test-anticheat.sh <server dir (published or bin)> <bot dll/exe>
set -euo pipefail
src_dir="${1:?server directory}"; bot="${2:?bot executable or dll}"
port="${GTAN_TEST_PORT:-4499}"
if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi
work="$(mktemp -d)"; server_dir="$work/server"; cp -r "$src_dir" "$server_dir"
python3 - "$server_dir/settings.xml" <<'PY'
import sys, re
p = sys.argv[1]; s = open(p).read()
s = re.sub(r'\s*<anticheat[^>]*/>', '', s)
s = s.replace('</config>', '  <anticheat action="kick" footspeed="60" speedfactor="1.3" teleport="200" integrity="report"/>\n</config>')
open(p, 'w').write(s)
PY
pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
"${server_cmd[@]}" > it-server.log 2>&1 &
server_pid=$!
popd >/dev/null
cleanup() { kill -TERM "$server_pid" 2>/dev/null || true; for _ in $(seq 1 15); do sleep 1; kill -0 "$server_pid" 2>/dev/null || break; done; kill -9 "$server_pid" 2>/dev/null || true; }
trap cleanup EXIT
for _ in $(seq 1 40); do sleep 1; grep -q "Started! Waiting for connections." "$server_dir/it-server.log" && break; kill -0 "$server_pid" 2>/dev/null || { echo "server died:"; cat "$server_dir/it-server.log"; exit 1; }; done
grep -q "Started! Waiting for connections." "$server_dir/it-server.log" || { echo "server did not start:"; cat "$server_dir/it-server.log"; exit 1; }

echo "-- a speed hacker (200 m/s on foot) is kicked"
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Speedy --cheat speed --duration 8 --timeout 30 > "$work/speedy.log" 2>&1
set -e
grep -q "Cheat detected: speed by Speedy" "$server_dir/it-server.log" || { echo "FAIL: the server did not detect the speed hack:"; grep -i "cheat\|Speedy" "$server_dir/it-server.log" | head; exit 1; }
grep -q "\[disconnected\] Cheat detected: speed" "$work/speedy.log" || { echo "FAIL: the speed hacker was not kicked with the reason:"; grep -i "disconnected\|result" "$work/speedy.log"; exit 1; }
grep -q "\[freeroam\] cheat detected: speed" "$server_dir/it-server.log" || { echo "FAIL: the freeroam gamemode did not log the event:"; grep -i "freeroam" "$server_dir/it-server.log" | tail -n 5; exit 1; }

echo "-- a teleporter (500 m jumps) is kicked"
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Blinky --cheat teleport --duration 8 --timeout 30 > "$work/blinky.log" 2>&1
set -e
grep -q "Cheat detected: teleport by Blinky" "$server_dir/it-server.log" || { echo "FAIL: the server did not detect the teleport:"; grep -i "cheat" "$server_dir/it-server.log" | head; exit 1; }
grep -q "\[disconnected\] Cheat detected: teleport" "$work/blinky.log" || { echo "FAIL: the teleporter was not kicked"; exit 1; }

echo "-- a normal bot walks for 8 s and is left alone"
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Honest --duration 8 --timeout 30 > "$work/honest.log" 2>&1 || { echo "FAIL: the honest bot could not stay:"; tail -n 5 "$work/honest.log"; exit 1; }
if grep -q "by Honest" "$server_dir/it-server.log"; then echo "FAIL: the honest bot was flagged:"; grep "by Honest" "$server_dir/it-server.log"; exit 1; fi
detections=$(grep -c "Cheat detected:" "$server_dir/it-server.log" || true)
echo "detections logged: $detections (speed, teleport)"
rm -rf "$work"; trap - EXIT; cleanup
echo "anticheat integration test passed"
