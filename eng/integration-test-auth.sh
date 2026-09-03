#!/usr/bin/env bash
# Second end-to-end test: the "auth" resource gates chat and commands until the player logged in.
# Copies the server folder, enables the resource, starts the server and drives it with the headless bot.
# Usage: eng/integration-test-auth.sh <server dir (published or bin)> <bot dll/exe>
set -euo pipefail
src_dir="${1:?server directory}"
bot="${2:?bot executable or dll}"
port="${GTAN_TEST_PORT:-4499}"

if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi

server_dir="$(mktemp -d)/server"
cp -r "$src_dir" "$server_dir"
rm -f "$server_dir/resources/auth/accounts.json"
python3 - "$server_dir/settings.xml" <<'PY'
import sys, re
p = sys.argv[1]
s = open(p).read()
s = s.replace('<!-- <resource src="auth" /> -->', '<resource src="auth" />')
if '<resource src="auth" />' not in s:
    s = s.replace('<resource src="freeroam" />', '<resource src="auth" />\n  <resource src="freeroam" />', 1)
open(p, 'w').write(s)
PY
grep -q '<resource src="auth" />' "$server_dir/settings.xml" || { echo "could not enable the auth resource in settings.xml"; exit 1; }

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
grep -q "auth: 0 account(s) loaded" "$server_dir/it-server.log" || { echo "the auth resource did not start:"; grep -i -E "auth|exception|error" "$server_dir/it-server.log" || true; exit 1; }

# The bot sends one line per second, so the order below is the order the server sees.
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot \
  --say "/veh adder" \
  --say "hello before login" \
  --say "/register cibot secret123" \
  --say "/login cibot secret123" \
  --say "/register cibot secret123" \
  --say "/veh adder" \
  --say "hello after login" \
  --expect "Log in first" \
  --expect "Account cibot created" \
  --expect "already logged in" \
  --expect "Spawned Adder" \
  --expect "CIBot: hello after login" \
  --duration 3 --timeout 60 | tee "$server_dir/it-bot.log"
rc=${PIPESTATUS[0]}
set -e

if grep -q "CIBot: hello before login" "$server_dir/it-bot.log"; then
  echo "FAIL: the chat message before login was not cancelled"; rc=1
fi
if ! grep -q '"Name": "cibot"' "$server_dir/resources/auth/accounts.json" 2>/dev/null; then
  echo "FAIL: accounts.json does not contain the new account"; rc=1
fi

# Second connection: the stored account must accept the password, the wrong one must be rejected.
set +e
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot2 \
  --say "/login cibot wrongpass" \
  --say "/login cibot secret123" \
  --expect "Wrong name or password" \
  --expect "Logged in as cibot" \
  --duration 2 --timeout 40 | tee "$server_dir/it-bot2.log"
rc2=${PIPESTATUS[0]}
set -e

if grep -E "Exception|ERROR STARTING|Unhandled" "$server_dir/it-server.log"; then
  echo "server log contains errors"; rc=1
fi

echo "--------------------"
if [ "$rc" = "0" ] && [ "$rc2" = "0" ]; then
  echo "auth integration test passed"
else
  echo "auth integration test FAILED (rc=$rc rc2=$rc2)"; echo "---- server log ----"; cat "$server_dir/it-server.log"; exit 1
fi
