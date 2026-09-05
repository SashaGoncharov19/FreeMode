#!/usr/bin/env bash
# Starts a published server, checks that the example resource compiles (Roslyn), that the HTTP
# file server answers and that SIGTERM shuts it down cleanly. Usage: eng/smoke-test-server.sh <publish dir>
set -euo pipefail
dir="${1:?publish directory}"
cd "$dir"
if [ -x ./GTANetworkServer ]; then exe=./GTANetworkServer; else exe="dotnet GTANetworkServer.dll"; fi
rm -f server.log
$exe > smoke.log 2>&1 &
pid=$!
ok=0
for i in $(seq 1 30); do
  sleep 1
  if grep -q "Started! Waiting for connections." smoke.log; then ok=1; break; fi
  if ! kill -0 $pid 2>/dev/null; then break; fi
done
echo "---- server output ----"; cat smoke.log; echo "-----------------------"
if [ $ok -ne 1 ]; then echo "server did not start"; kill $pid 2>/dev/null || true; exit 1; fi
grep -q "Resource example started!" smoke.log || { echo "example resource did not start (script compilation failed?)"; kill $pid; exit 1; }
grep -q "Example gamemode started" smoke.log || { echo "example script did not run"; kill $pid; exit 1; }
# the session key (T-009): server.key is created at the first start and the banner shows the public key and the policy
grep -q "= Public key: [0-9a-f]\{64\}" smoke.log || { echo "the banner shows no server public key"; kill $pid; exit 1; }
grep -q "= Encryption: required" smoke.log || { echo "the banner does not say that encryption is required"; kill $pid; exit 1; }
[ -s server.key ] || { echo "server.key was not created"; kill $pid; exit 1; }
# freeroam's client script is TypeScript: the first start bundles it with Bun (T-005)
grep -q "bundled client/index.ts -> client/index.js" smoke.log || { echo "freeroam's TypeScript client script was not bundled (bun missing?)"; grep -i "client/index\|bun" smoke.log || true; kill $pid; exit 1; }
manifest=$(curl -sS -m 5 http://127.0.0.1:4499/manifest.json || true)
echo "manifest: $manifest"
[[ "$manifest" == *exportedFiles* ]] || { echo "HTTP file server did not answer"; kill $pid; exit 1; }
kill -TERM $pid
for i in $(seq 1 15); do sleep 1; kill -0 $pid 2>/dev/null || break; done
if kill -0 $pid 2>/dev/null; then echo "server did not stop on SIGTERM"; kill -9 $pid; exit 1; fi
grep -q "Terminated." smoke.log || { echo "no clean shutdown message"; exit 1; }
echo "smoke test passed"
