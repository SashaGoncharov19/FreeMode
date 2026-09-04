#!/usr/bin/env bash
# Build the in-game client (managed GTANetwork.dll) on any OS — no MSVC, no Windows needed. This is
# the fast inner loop for client code: it compiles against the managed ScriptHookVDotNet reference
# stub, so you never wait for the Windows CI job just to try a change in game.
#
# Usage: eng/dev-build-client.sh [-c Release|Debug] [--sync [INSTALL_DIR]] [--cef] [-- <extra dotnet args>]
#   --sync [DIR]   after building, copy the result into a GTA Network install (eng/dev-sync-client.sh)
#   --cef          with --sync, also refresh the Chromium runtime (rarely needed)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
config=Release
sync=0
cef=0
install=""
extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    -c|--config) config="${2:?}"; shift 2 ;;
    --sync) sync=1; shift; if [ $# -gt 0 ] && [ "${1#-}" = "$1" ]; then install="$1"; shift; fi ;;
    --cef) cef=1; shift ;;
    --) shift; extra=("$@"); break ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

echo "Building GTANetwork client ($config)…"
dotnet build "$root/Client/GTANetworkClient.csproj" -c "$config" -nologo ${extra[@]+"${extra[@]}"}
echo "Built $root/Client/bin/$config/net48/GTANetwork.dll"

if [ "$sync" = 1 ]; then
  syncargs=(--config "$config")
  [ "$cef" = 1 ] && syncargs+=(--cef)
  [ -n "$install" ] && syncargs=("$install" "${syncargs[@]}")
  "$root/eng/dev-sync-client.sh" "${syncargs[@]}"
fi
