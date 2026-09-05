#!/usr/bin/env bash
# Fifth end-to-end test (T-014): a server that declares custom DLC packs. GET /dlcpacks.json lists them; a client without a
# required pack is refused with the pack's name; a client claiming it joins; the launcher's `prepare` downloads and verifies the
# packs (and reports the one whose declared hash is wrong).
# Usage: eng/integration-test-dlc.sh <server dir (published or bin)> <bot dll/exe> <launcher dll/exe>
set -euo pipefail
src_dir="${1:?server directory}"; bot="${2:?bot executable or dll}"; launcher="${3:?launcher executable or dll}"
port="${GTAN_TEST_PORT:-4499}"; http_port="${GTAN_TEST_DLC_PORT:-4598}"
if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi
if [[ "$launcher" == *.dll ]]; then launcher_cmd=(dotnet "$launcher"); else launcher_cmd=("$launcher"); fi

work="$(mktemp -d)"
server_dir="$work/server"; cp -r "$src_dir" "$server_dir"
mkdir -p "$work/packs" "$work/install"
head -c 65536 /dev/urandom > "$work/packs/dlc.rpf"
sha=$(sha256sum "$work/packs/dlc.rpf" | cut -d' ' -f1)
size=$(stat -c %s "$work/packs/dlc.rpf")
python3 - "$server_dir/settings.xml" "$http_port" "$sha" "$size" <<'PY'
import sys, re
p, http_port, sha, size = sys.argv[1:5]
s = open(p).read()
s = re.sub(r'<httpserver>.*?</httpserver>', '<httpserver>true</httpserver>', s)
s = s.replace('</config>', '  <dlcpack name="testpack" url="http://127.0.0.1:%s/dlc.rpf" sha256="%s" size="%s" required="true"/>\n'
                           '  <dlcpack name="badpack" url="http://127.0.0.1:%s/dlc.rpf" sha256="%s" size="%s" required="false"/>\n</config>' % (http_port, sha, size, http_port, '0' * 64, size))
open(p, 'w').write(s)
PY

# the pack file over HTTP (a CDN in real life)
(cd "$work/packs" && python3 -m http.server "$http_port" --bind 127.0.0.1 > "$work/http.log" 2>&1) &
http_pid=$!
pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
"${server_cmd[@]}" > it-server.log 2>&1 &
server_pid=$!
popd >/dev/null
cleanup() {
  for pid in "$server_pid" "$http_pid"; do kill -TERM "$pid" 2>/dev/null || true; done
  for _ in $(seq 1 15); do sleep 1; kill -0 "$server_pid" 2>/dev/null || break; done
  kill -9 "$server_pid" "$http_pid" 2>/dev/null || true
}
trap cleanup EXIT
for _ in $(seq 1 40); do
  sleep 1
  grep -q "Started! Waiting for connections." "$server_dir/it-server.log" && break
  kill -0 "$server_pid" 2>/dev/null || { echo "server died:"; cat "$server_dir/it-server.log"; exit 1; }
done
grep -q "Started! Waiting for connections." "$server_dir/it-server.log" || { echo "server did not start:"; cat "$server_dir/it-server.log"; exit 1; }
grep -q "DLC packs declared: testpack, badpack (optional)" "$server_dir/it-server.log" || { echo "FAIL: the server did not log the declared packs:"; grep -i dlc "$server_dir/it-server.log" || true; exit 1; }

list=$(curl -fsS -m 5 "http://127.0.0.1:$port/dlcpacks.json"); echo "dlcpacks.json: $list"
[[ "$list" == *"\"name\":\"testpack\""* && "$list" == *"\"sha256\":\"$sha\""* && "$list" == *"\"required\":true"* ]] || { echo "FAIL: /dlcpacks.json lacks the declared pack"; exit 1; }

echo "-- a client without the pack is refused"
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name NoPack --duration 1 --timeout 20 > "$work/bot-nopack.log" 2>&1
code=$?
set -e
grep -q "needs the DLC packs: testpack" "$work/bot-nopack.log" || { echo "FAIL: the refusal did not name the pack:"; cat "$work/bot-nopack.log"; exit 1; }
[ "$code" -ne 0 ] || { echo "FAIL: the bot without the pack joined"; exit 1; }
grep -q "missing DLC packs testpack" "$server_dir/it-server.log" || { echo "FAIL: the server did not log the refusal"; exit 1; }

echo "-- a client with the pack joins"
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name HasPack --dlc testpack --duration 1 --timeout 30 > "$work/bot-pack.log" 2>&1 || { echo "FAIL: the bot with the pack could not join:"; tail -n 20 "$work/bot-pack.log"; exit 1; }
grep -q "\[result\] OK" "$work/bot-pack.log" || { echo "FAIL: no OK result for the bot with the pack"; exit 1; }

echo "-- the launcher prepares the packs (testpack downloads, badpack's hash is wrong)"
set +e
"${launcher_cmd[@]}" prepare "127.0.0.1:$port" --install-dir "$work/install" > "$work/prepare.log" 2>&1
code=$?
set -e
cat "$work/prepare.log" | tail -n 8
[ "$code" -eq 1 ] || { echo "FAIL: prepare should exit 1 because badpack's hash is wrong (exit $code)"; exit 1; }
[ -f "$work/install/dlcpacks/testpack/dlc.rpf" ] || { echo "FAIL: testpack was not downloaded"; exit 1; }
[ "$(sha256sum "$work/install/dlcpacks/testpack/dlc.rpf" | cut -d' ' -f1)" = "$sha" ] || { echo "FAIL: the downloaded testpack differs from the served file"; exit 1; }
[ ! -f "$work/install/dlcpacks/badpack/dlc.rpf" ] || { echo "FAIL: a pack with a wrong hash was kept"; exit 1; }
grep -q "badpack: FAILED.*sha256 mismatch" "$work/prepare.log" || { echo "FAIL: the hash mismatch was not reported"; exit 1; }
grep -q "testpack: downloaded and verified" "$work/prepare.log" || { echo "FAIL: the successful download was not reported"; exit 1; }

echo "-- a second prepare finds testpack up to date"
set +e
"${launcher_cmd[@]}" prepare "127.0.0.1:$port" --install-dir "$work/install" > "$work/prepare2.log" 2>&1
set -e
grep -q "testpack: up to date" "$work/prepare2.log" || { echo "FAIL: the second prepare downloaded again:"; cat "$work/prepare2.log"; exit 1; }
rm -rf "$work"
trap - EXIT; cleanup
echo "dlc integration test passed"
