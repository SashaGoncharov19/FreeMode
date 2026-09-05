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
  --say "/help" --say "/players" --say "/veh adder" --say "/pos" --say "/weapon carbinerifle 250" --say "hello from the bot" --say "/nonexistent" --say "/tsping abc" --say "tsdemo?" \
  --rpc "freeroam:ping" '{"n":1}' --rpc "freeroam:secret" 'null' --rpc "tsdemo:echo" '{"x":1}' --rpc "no:such" 'null' --rpc-burst "freeroam:ping" 40 \
  --expect "Welcome to GTA Network freeroam" \
  --expect "/veh [model]" \
  --expect "Online (1): CIBot" \
  --expect "Spawned Adder" \
  --expect "Given CarbineRifle with 250 rounds" \
  --expect "CIBot: hello from the bot" \
  --expect "Command not found" \
  --expect "hello from Bun" \
  --expect "tsdemo: pong abc" \
  --expect "tsdemo: yes" \
  --expect 'rpc freeroam:ping ok {"t":' \
  --expect '"echo":{"n":1},"player":"CIBot"}' \
  --expect "rpc freeroam:secret error denied" \
  --expect 'rpc tsdemo:echo ok {"from":"bun","player":' \
  --expect '"args":{"x":1},"players":1}' \
  --expect "rpc no:such error unknown" \
  --expect "rpc freeroam:ping error rate" \
  --duration 3 --timeout 60 | tee "$server_dir/it-bot.log"
rc=${PIPESTATUS[0]}
set -e

# ---- phase 2: two players at once, so that the server relays sync packets and entity events between them
echo "---- phase 2: two bots ----"
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Alice --verbose --say "/players" --say "hi Bob" --duration 6 --timeout 40 > "$server_dir/it-alice.log" 2>&1 &
alice_pid=$!
sleep 2
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Bob --say "/veh zentorno" --say "hi Alice" --duration 3 --timeout 40 > "$server_dir/it-bob.log" 2>&1
rc_bob=$?
wait "$alice_pid"; rc_alice=$?
set -e
grep -E "\[(chat|entity|player|result)\]" "$server_dir/it-alice.log" | grep -v "\[sync\]" || true
phase2_ok=1
grep -q "Bob joined the server" "$server_dir/it-alice.log" || { echo "Alice did not see Bob join"; phase2_ok=0; }
grep -q "Bob: hi Alice" "$server_dir/it-alice.log" || { echo "Alice did not get Bob's chat"; phase2_ok=0; }
grep -q "create Vehicle .* model Zentorno" "$server_dir/it-alice.log" || { echo "Alice did not see Bob's vehicle"; phase2_ok=0; }
grep -q "\[sync\] ped #" "$server_dir/it-alice.log" || { echo "Alice did not receive relayed position sync from Bob"; phase2_ok=0; }
grep -q 'player #[0-9]* "Alice"' "$server_dir/it-bob.log" || { echo "Bob's map did not contain Alice"; phase2_ok=0; }
[ "$rc_alice" -eq 0 ] && [ "$rc_bob" -eq 0 ] || { echo "a bot failed (alice=$rc_alice bob=$rc_bob)"; phase2_ok=0; }

echo "---- server log ----"; cat "$server_dir/it-server.log"; echo "--------------------"
[ "$phase2_ok" -eq 1 ] || { echo "phase 2 failed"; exit 1; }

grep -q "Connection established: CIBot" "$server_dir/it-server.log" || { echo "server never confirmed the bot"; exit 1; }
if grep -q "Exception in the Netcode" "$server_dir/it-server.log"; then echo "server logged netcode exceptions"; exit 1; fi
if grep -q "EXCEPTION IN RESOURCE" "$server_dir/it-server.log"; then echo "a resource script threw (see server log above)"; exit 1; fi
grep -q "CIBot: hello from the bot" "$server_dir/it-server.log" || { echo "server did not relay the chat message"; exit 1; }
[ "$rc" -eq 0 ] || { echo "bot exited with $rc"; exit "$rc"; }
echo "integration test passed"
