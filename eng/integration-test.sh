#!/usr/bin/env bash
# End-to-end test without the game: starts a server, joins it with the headless bot over the real
# protocol (Lidgren + protobuf), runs freeroam commands and checks the replies.
# Usage: eng/integration-test.sh <server dir (published or bin)> <bot dll/exe>
set -euo pipefail
server_dir="${1:?server directory}"
bot="${2:?bot executable or dll}"
port="${GTAN_TEST_PORT:-4499}"

if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi

pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
rm -f server.log
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

set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot --discover \
  --say "/help" --say "/players" --say "/veh adder" --say "/pos" --say "/weapon carbinerifle 250" --say "hello from the bot" --say "/nonexistent" \
  --expect "Welcome to GTA Network freeroam" \
  --expect "/veh [model]" \
  --expect "Online (1): CIBot" \
  --expect "Spawned Adder" \
  --expect "Given CarbineRifle with 250 rounds" \
  --expect "CIBot: hello from the bot" \
  --expect "Command not found" \
  --duration 3 --timeout 60 | tee "$server_dir/it-bot.log"
rc=${PIPESTATUS[0]}
set -e

echo "---- server log ----"; cat "$server_dir/it-server.log"; echo "--------------------"

grep -q "Connection established: CIBot" "$server_dir/it-server.log" || { echo "server never confirmed the bot"; exit 1; }
if grep -q "Exception in the Netcode" "$server_dir/it-server.log"; then echo "server logged netcode exceptions"; exit 1; fi
grep -q "CIBot: hello from the bot" "$server_dir/it-server.log" || { echo "server did not relay the chat message"; exit 1; }
[ "$rc" -eq 0 ] || { echo "bot exited with $rc"; exit "$rc"; }
echo "integration test passed"
