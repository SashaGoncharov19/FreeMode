#!/usr/bin/env bash
# T-006 stage 1: measure the engine <-> Bun runtime bridge (docs/tasks/T-006-server-runtime-on-bun-bridge.md).
# Runs the .NET bench engine (Tools/GTANetwork.BridgeBench) and the Bun bench client (runtime/bench/bench.ts) over a
# Unix domain socket and over loopback TCP, and prints their lines. Meant for the dev container:
#   docker compose run --rm dev eng/bench-bridge.sh [--players 1000] [--seconds 10] [--oneway 1000000]
# Bun: uses the one on PATH, else downloads the pinned version (runtime/.bun-version) into /tmp.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bun="$(command -v bun || true)"
if [ -z "$bun" ]; then
  # no Bun in this container image: keep a downloaded copy in artifacts/ (git-ignored) so repeated runs do not fetch it again
  ver="$(cat runtime/.bun-version)"
  if [ ! -x "$root/artifacts/bun-linux-x64/bun" ]; then
    echo "== downloading bun $ver into artifacts/"
    mkdir -p "$root/artifacts"
    curl -fsSL "https://github.com/oven-sh/bun/releases/download/bun-v${ver}/bun-linux-x64.zip" -o "$root/artifacts/bun.zip"
    (cd "$root/artifacts" && unzip -q -o bun.zip && rm -f bun.zip)
  fi
  bun="$root/artifacts/bun-linux-x64/bun"
fi
echo "== bun: $($bun --version) ($bun)"

echo "== building the bench engine"
dotnet build Tools/GTANetwork.BridgeBench/GTANetwork.BridgeBench.csproj -c Release -nologo -v quiet
engine="$(ls Tools/GTANetwork.BridgeBench/bin/Release/net*/GTANetwork.BridgeBench.dll | head -1)"
(cd runtime/bench && "$bun" install --silent)

run() { # transport [bench args...]
  local listen="$1"; shift
  local log="$root/artifacts/bridge-engine-${listen//[:\/]/_}.log"
  dotnet "$engine" --listen "$listen" > "$log" 2>&1 &
  local pid=$!
  for _ in 1 2 3 4 5 6 7 8 9 10; do grep -q "listening" "$log" 2>/dev/null && break; kill -0 $pid 2>/dev/null || { echo "engine died:"; cat "$log"; return 1; }; sleep 0.5; done
  (cd runtime/bench && timeout 300 "$bun" bench.ts "$listen" "$@") | sed "s/^/  /"
  kill $pid 2>/dev/null; wait $pid 2>/dev/null || true
}

echo "== unix domain socket"
run unix:/tmp/gtan-bridge.sock "$@"
echo "== loopback tcp"
run tcp:47000 "$@"
