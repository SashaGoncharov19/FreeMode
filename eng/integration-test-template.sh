#!/usr/bin/env bash
# Third end-to-end test: `gtanetwork create` writes a resource skeleton, the server starts it (the client script is bundled
# with Bun, the server script runs in the Bun runtime) and the headless bot gets its greeting, its command and its RPC answer.
# Usage: eng/integration-test-template.sh <server dir (published or bin)> <bot dll/exe> <gtanetwork cli>
set -euo pipefail
src_dir="${1:?server directory}"
bot="${2:?bot executable or dll}"
cli="${3:?gtanetwork executable}"
port="${GTAN_TEST_PORT:-4499}"

if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi

server_dir="$(mktemp -d)/server"
cp -r "$src_dir" "$server_dir"

"$cli" create demo --dir "$server_dir/resources"
[ -f "$server_dir/resources/demo/meta.xml" ] || { echo "the CLI did not create resources/demo"; exit 1; }
grep -q '"demo:time"' "$server_dir/resources/demo/server/index.ts" || { echo "__NAME__ was not substituted in server/index.ts"; exit 1; }
[ -f "$server_dir/resources/demo/types/api.generated.d.ts" ] || { echo "the typings were not copied into the resource"; exit 1; }

if command -v bun >/dev/null 2>&1; then
  (cd "$server_dir/resources/demo" && bun install --silent && bun run --silent check) || { echo "bun run check failed for the created resource"; exit 1; }
  echo "created resource type-checks (bun run check)"
else
  echo "bun not found: skipping the type check of the created resource"
fi

python3 - "$server_dir/settings.xml" <<'PY'
import sys
p = sys.argv[1]
s = open(p).read()
if '<resource src="demo" />' not in s:
    s = s.replace('<resource src="freeroam" />', '<resource src="freeroam" />\n  <resource src="demo" />', 1)
open(p, 'w').write(s)
PY
grep -q '<resource src="demo" />' "$server_dir/settings.xml" || { echo "could not enable the demo resource in settings.xml"; exit 1; }

pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
"${server_cmd[@]}" > it-server.log 2>&1 &
server_pid=$!
popd >/dev/null

cleanup() {
  if kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM "$server_pid" 2>/dev/null || true
    for _ in $(seq 1 15); do sleep 1; kill -0 "$server_pid" 2>/dev/null || break; done
    kill -9 "$server_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

for _ in $(seq 1 40); do
  sleep 1
  grep -q "Started! Waiting for connections." "$server_dir/it-server.log" && break
  kill -0 "$server_pid" 2>/dev/null || { echo "server died:"; cat "$server_dir/it-server.log"; exit 1; }
done
grep -q "Started! Waiting for connections." "$server_dir/it-server.log" || { echo "server did not start:"; cat "$server_dir/it-server.log"; exit 1; }
sleep 2
grep -q "Resource demo: bundled client/index.ts -> client/index.js" "$server_dir/it-server.log" || { echo "the demo client script was not bundled:"; grep -i "demo" "$server_dir/it-server.log" || true; exit 1; }
grep -q "\[demo\] started server/index.ts" "$server_dir/it-server.log" || { echo "the demo server script did not start in the runtime:"; grep -i "demo\|runtime" "$server_dir/it-server.log" || true; exit 1; }

set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot \
  --say "/hello" --say "/panel" \
  --rpc "demo:time" 'null' \
  --expect "demo: welcome, CIBot" \
  --expect "demo: hello, CIBot" \
  --expect 'rpc demo:time ok {"t":' \
  --duration 3 --timeout 60 | tee "$server_dir/it-bot.log"
rc=${PIPESTATUS[0]}
set -e

if grep -q "Command not found" "$server_dir/it-bot.log"; then echo "FAIL: /panel or /hello answered 'Command not found'"; rc=1; fi
grep -q 'client script "client/index.js" from "demo"' "$server_dir/it-bot.log" || { echo "FAIL: the bot did not receive the demo client bundle"; rc=1; }
if grep -E "Exception|ERROR STARTING|Unhandled" "$server_dir/it-server.log"; then echo "server log contains errors"; rc=1; fi

echo "--------------------"
if [ "$rc" = "0" ]; then
  echo "template integration test passed"
else
  echo "template integration test FAILED (rc=$rc)"; echo "---- server log ----"; cat "$server_dir/it-server.log"; exit 1
fi
