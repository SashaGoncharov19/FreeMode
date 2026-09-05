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
# the copy of the server folder brought the bundle cache along: this start must not run Bun again (T-005)
grep -q "cached bundle of client/index.ts -> client/index.js" "$server_dir/it-server.log" || { echo "freeroam's client bundle was not taken from the cache:"; grep "client/index" "$server_dir/it-server.log" || true; exit 1; }

# ---- the resource's CEF page (<file src="ui/..."/>) must be served the way the game client fetches it:
# GET /manifest.json lists it, GET /<resource>/<path> returns it byte for byte, nothing else leaks.
base="http://127.0.0.1:$port"
manifest=$(curl -sS -m 5 "$base/manifest.json" || true)
echo "manifest: $manifest"
[[ "$manifest" == *'"ui/index.html"'* ]] || { echo "FAIL: manifest.json does not list auth/ui/index.html"; exit 1; }
for f in ui/index.html ui/style.css ui/app.js; do
  code=$(curl -sS -m 5 -o "$server_dir/dl.tmp" -w '%{http_code}' "$base/auth/$f" || true)
  [ "$code" = "200" ] || { echo "FAIL: GET /auth/$f answered $code"; exit 1; }
  cmp -s "$server_dir/dl.tmp" "$server_dir/resources/auth/$f" || { echo "FAIL: GET /auth/$f returned different content"; exit 1; }
done
code=$(curl -sS -m 5 -o /dev/null -w '%{http_code}' "$base/auth/auth.cs" || true)
[ "$code" = "404" ] || { echo "FAIL: the server script auth.cs is not exported but GET answered $code"; exit 1; }
code=$(curl -sS -m 5 -o /dev/null -w '%{http_code}' --path-as-is "$base/auth/../settings.xml" || true)
[ "$code" != "200" ] || { echo "FAIL: path traversal /auth/../settings.xml was served"; exit 1; }
echo "HTTP file server: manifest and the three auth files OK, auth.cs and traversal refused"

# The bot sends one line per second, so the order below is the order the server sees.
set +e
client_files="$server_dir/client-files"
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot --download-files "$client_files" \
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
# The bot downloaded the resource files with the same code as the game client: they must be complete copies.
for f in ui/index.html ui/style.css ui/app.js; do
  if ! cmp -s "$client_files/auth/$f" "$server_dir/resources/auth/$f"; then
    echo "FAIL: the bot did not download auth/$f (or it differs)"; rc=1
  fi
done
if [ -e "$client_files/auth/auth.cs" ]; then echo "FAIL: the server script auth.cs ended up on the client"; rc=1; fi

# Second connection: the stored account must accept the password, the wrong one must be rejected.
set +e
# The CEF form goes through RPC (T-008): a wrong password resolves with ok=false and the server's text, the right one logs in.
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --name CIBot2 \
  --say "/login cibot wrongpass" \
  --rpc "auth:login" '{"name":"cibot","password":"wrongpass"}' \
  --rpc "auth:login" '{"name":"cibot","password":"secret123"}' \
  --rpc "freeroam:secret" 'null' \
  --expect "Wrong name or password" \
  --expect 'rpc auth:login ok {"ok":false,"message":"Wrong name or password."}' \
  --expect 'rpc auth:login ok {"ok":true,"message":"Logged in."}' \
  --expect "Logged in as cibot" \
  --expect 'rpc freeroam:secret ok "the secret is 42"' \
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
