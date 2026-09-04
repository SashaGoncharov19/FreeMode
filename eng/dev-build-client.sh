#!/usr/bin/env bash
# Build the in-game client (managed GTANetwork.dll) and the browser host (GTANetwork.CefHost.exe) on any OS — no MSVC,
# no Windows needed. This is
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

# The client must be compiled against the REAL ScriptHookVDotNet.dll (C++/CLI, built by the Windows CI job): the
# managed reference stub in Shv.NET/ref is for compile checks only and is not binary-compatible (different
# InputArgument conversions -> MissingMethodException in game). Take it from the install when the repo has none.
real_shvdn="$root/Shv.NET/bin/ScriptHookVDotNet.dll"
if [ ! -f "$real_shvdn" ]; then
  for candidate in "${GTAN_INSTALL:-}/bin/ScriptHookVDotNet.dll" "$HOME/GTANetwork/bin/ScriptHookVDotNet.dll" "${install:-}/bin/ScriptHookVDotNet.dll"; do
    if [ -n "$candidate" ] && [ -f "$candidate" ]; then
      mkdir -p "$(dirname "$real_shvdn")" && cp -f "$candidate" "$real_shvdn"
      echo "Using the real ScriptHookVDotNet.dll from $candidate (copied to Shv.NET/bin, git-ignored)"
      break
    fi
  done
fi
if [ -f "$real_shvdn" ]; then
  echo "ScriptHookVDotNet reference: real build ($real_shvdn)"
else
  echo "WARNING: no real ScriptHookVDotNet.dll found; compiling against the managed stub, which is NOT binary-compatible in game." >&2
fi

echo "Building GTANetwork client ($config)…"
dotnet build "$root/Client/GTANetworkClient.csproj" -c "$config" -nologo ${extra[@]+"${extra[@]}"}
echo "Built $root/Client/bin/$config/net48/GTANetwork.dll"
echo "Building the browser host ($config)…"
dotnet build "$root/Subprocess/GTANetwork.CefHost/GTANetwork.CefHost.csproj" -c "$config" -nologo ${extra[@]+"${extra[@]}"}
echo "Built $root/Subprocess/GTANetwork.CefHost/bin/$config/net48/GTANetwork.CefHost.exe"
# Wine maps PE images from disk only with page-aligned sections; Chromium's are not (eng/pe-realign.py explains).
python3 "$root/eng/pe-realign.py" "$root/Subprocess/GTANetwork.CefHost/bin/$config/net48" | tail -n 1

if [ "$sync" = 1 ]; then
  syncargs=(--config "$config")
  [ "$cef" = 1 ] && syncargs+=(--cef)
  [ -n "$install" ] && syncargs=("$install" "${syncargs[@]}")
  "$root/eng/dev-sync-client.sh" "${syncargs[@]}"
fi
