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
  --expect "crypto: encrypted session" \
  --duration 3 --timeout 60 | tee "$server_dir/it-bot.log"
rc=${PIPESTATUS[0]}
set -e

# ---- phase 1b (T-009): a client without the session handshake is refused; a client with the wrong pinned key leaves by itself
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name OldClient --no-encryption --duration 1 --timeout 20 > "$server_dir/it-old.log" 2>&1
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Pinned --pin 0000000000000000000000000000000000000000000000000000000000000000 --duration 1 --timeout 20 > "$server_dir/it-pin.log" 2>&1
set -e
grep -q "requires an encrypted session" "$server_dir/it-old.log" || { echo "a client without the handshake was not refused:"; cat "$server_dir/it-old.log"; exit 1; }
grep -q "server key mismatch" "$server_dir/it-pin.log" || { echo "the bot did not refuse a server with the wrong pinned key:"; cat "$server_dir/it-pin.log"; exit 1; }
if grep -q "\[joined\]" "$server_dir/it-old.log" "$server_dir/it-pin.log"; then echo "a refused client joined anyway"; exit 1; fi
echo "encryption: an old client is refused, a wrong pin is refused"

# ---- phase 2: two players at once, so that the server relays sync packets and entity events between them
echo "---- phase 2: two bots ----"
# freeroam spawns at one of four points up to 2.2 km apart; players beyond the 2000 m streaming range only exchange a position every
# 3 s (T-003), so both teleport to the same spot first - the relay of pure sync is what this phase checks.
# T-026: Carol spawns a vehicle 4 km away; Bob must not receive its create, Alice gets it when she teleports next to it.
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Alice --verbose --say "/tp 200 200 72" --say "/players" --say "hi Bob" --say "/tp 2990 3000 72" --duration 6 --timeout 40 > "$server_dir/it-alice.log" 2>&1 &
alice_pid=$!
sleep 2
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Carol --say "/tp 3000 3000 72" --say "/veh adder" --duration 5 --timeout 40 > "$server_dir/it-carol.log" 2>&1 &
carol_pid=$!
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Bob --say "/tp 210 200 72" --say "/veh zentorno" --say "hi Alice" --duration 3 --timeout 40 > "$server_dir/it-bob.log" 2>&1
rc_bob=$?
wait "$alice_pid"; rc_alice=$?
wait "$carol_pid"; rc_carol=$?
set -e
grep -E "\[(chat|entity|player|result)\]" "$server_dir/it-alice.log" | grep -v "\[sync\]" || true
phase2_ok=1
grep -q "Bob joined the server" "$server_dir/it-alice.log" || { echo "Alice did not see Bob join"; phase2_ok=0; }
grep -q "Bob: hi Alice" "$server_dir/it-alice.log" || { echo "Alice did not get Bob's chat"; phase2_ok=0; }
grep -q "create Vehicle .* model Zentorno" "$server_dir/it-alice.log" || { echo "Alice did not see Bob's vehicle"; phase2_ok=0; }
grep -q "\[sync\] ped #" "$server_dir/it-alice.log" || { echo "Alice did not receive relayed position sync from Bob"; phase2_ok=0; }
grep -q 'player #[0-9]* "Alice"' "$server_dir/it-bob.log" || { echo "Bob's map did not contain Alice"; phase2_ok=0; }
grep -q "create Vehicle .* model Adder" "$server_dir/it-alice.log" || { echo "Alice did not get Carol's vehicle after teleporting next to it (T-026 catch-up)"; phase2_ok=0; }
if grep -q "model Adder" "$server_dir/it-bob.log"; then echo "Bob, 4 km away, received Carol's vehicle: entity broadcasts do not follow the range (T-026)"; phase2_ok=0; fi
[ "$rc_carol" -eq 0 ] || { echo "Carol failed ($rc_carol)"; phase2_ok=0; }
[ "$rc_alice" -eq 0 ] && [ "$rc_bob" -eq 0 ] || { echo "a bot failed (alice=$rc_alice bob=$rc_bob)"; phase2_ok=0; }

echo "---- server log ----"; cat "$server_dir/it-server.log"; echo "--------------------"
[ "$phase2_ok" -eq 1 ] || { echo "phase 2 failed"; exit 1; }

# ---- phase 3 (T-015): voice - a talker 5 m from a listener and 990 m from a third player; 5 s of Opus frames at 50/s
echo "---- phase 3: voice ----"
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Listener --say "/tp 205 200 72" --voice-expect 245 --voice-jitter 40 --duration 10 --timeout 40 > "$server_dir/it-listener.log" 2>&1 &
listener_pid=$!
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name FarAway --say "/tp 900 900 72" --voice-max 0 --duration 10 --timeout 40 > "$server_dir/it-far.log" 2>&1 &
far_pid=$!
sleep 3
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name Talker --say "/tp 200 200 72" --voice-send 5 --duration 1 --timeout 40 > "$server_dir/it-talker.log" 2>&1
rc_talker=$?
wait "$listener_pid"; rc_listener=$?
wait "$far_pid"; rc_far=$?
set -e
grep -h "\[voice\]\|\[result\]" "$server_dir/it-talker.log" "$server_dir/it-listener.log" "$server_dir/it-far.log" || true
[ "$rc_talker" -eq 0 ] && [ "$rc_listener" -eq 0 ] && [ "$rc_far" -eq 0 ] || { echo "voice phase failed (talker=$rc_talker listener=$rc_listener far=$rc_far)"; tail -n 5 "$server_dir/it-talker.log" "$server_dir/it-listener.log" "$server_dir/it-far.log"; exit 1; }
grep -q "\[voice\] sent 250 frames" "$server_dir/it-talker.log" || { echo "the talker did not send 250 frames"; exit 1; }
echo "voice: the listener heard the talker, the far player did not"

grep -q "Connection established: CIBot" "$server_dir/it-server.log" || { echo "server never confirmed the bot"; exit 1; }
# freeroam's client script is TypeScript (T-005): the server bundles it (or reads the bundle the smoke test cached) and the bot gets client/index.js
grep -qE "(bundled|cached bundle of) client/index.ts -> client/index.js" "$server_dir/it-server.log" || { echo "freeroam's TypeScript client script was not bundled"; exit 1; }
grep -q 'client script "client/index.js" from "freeroam"' "$server_dir/it-bot.log" || { echo "the bot did not receive freeroam's bundled client script"; exit 1; }
if grep -q "Exception in the Netcode" "$server_dir/it-server.log"; then echo "server logged netcode exceptions"; exit 1; fi
grep -q "Refused 127.0.0.1: the client sent no session key" "$server_dir/it-server.log" || { echo "the server did not log the refusal of the old client"; exit 1; }
if grep -q "failed authentication" "$server_dir/it-server.log"; then echo "the server dropped messages that failed authentication"; exit 1; fi
if grep -q "EXCEPTION IN RESOURCE" "$server_dir/it-server.log"; then echo "a resource script threw (see server log above)"; exit 1; fi
grep -q "CIBot: hello from the bot" "$server_dir/it-server.log" || { echo "server did not relay the chat message"; exit 1; }
[ "$rc" -eq 0 ] || { echo "bot exited with $rc"; exit "$rc"; }
echo "integration test passed"
