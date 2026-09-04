#!/usr/bin/env bash
# Drop a freshly built managed client into an existing GTA Network install so you can test a code
# change without waiting for CI or a release. Managed assemblies only by default (that is all that
# changes when you edit client C#); pass --cef to also refresh the Chromium runtime in cef/ (needed
# only when the CefSharp version or packaging changed).
#
# Usage: eng/dev-sync-client.sh [INSTALL_DIR] [--cef] [--config Release|Debug]
#   INSTALL_DIR   target install; defaults to $GTAN_INSTALL, then ~/GTANetwork
#
# Safe to run on the host (the build output lives in the repo, which the dev container bind-mounts)
# or inside the container (mount your install and set GTAN_INSTALL=/gtanetwork).
set -euo pipefail

config=Release
cef=0
install="${GTAN_INSTALL:-$HOME/GTANetwork}"
while [ $# -gt 0 ]; do
  case "$1" in
    --cef) cef=1; shift ;;
    --config) config="${2:?}"; shift 2 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) install="$1"; shift ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/Client/bin/$config/net48"
[ -f "$out/GTANetwork.dll" ] || { echo "No client build at $out. Build it first: eng/dev-build-client.sh -c $config" >&2; exit 1; }
[ -d "$install/bin" ] || { echo "$install is not a GTA Network install (no bin/). Pass the path or set GTAN_INSTALL." >&2; exit 1; }

scripts="$install/bin/scripts"
mkdir -p "$scripts"

# ScriptHookVDotNet loads every managed DLL in bin/scripts. Copy them all except SHVDN itself
# (the real C++/CLI build lives in bin/ and SHVDN removes any copy from scripts/ anyway).
copied=0
for f in "$out"/*.dll; do
  b="$(basename "$f")"
  [ "$b" = "ScriptHookVDotNet.dll" ] && continue
  cp -f "$f" "$scripts/" && copied=$((copied + 1))
done
cp -f "$out/GTANetwork.dll.config" "$scripts/" 2>/dev/null || true

# ClearScript's native V8 is also loaded from bin/ (JavascriptHook.ConfigureClearScript adds bin\ to
# the auxiliary search path), so keep a copy there too.
[ -f "$out/ClearScriptV8.win-x64.dll" ] && cp -f "$out/ClearScriptV8.win-x64.dll" "$install/bin/"

echo "Synced $copied managed DLLs -> $scripts"

if [ "$cef" = 1 ]; then
  [ -d "$out/cef" ] || { echo "No cef/ in $out." >&2; exit 1; }
  mkdir -p "$install/cef"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'cache/' --exclude '*.pdb' --exclude '*.xml' "$out/cef/" "$install/cef/"
  else
    # rsync-free fallback: rebuild cef/ but keep the existing cache/.
    tmp="$install/cef.tmp.$$"; rm -rf "$tmp"; cp -a "$out/cef" "$tmp"
    find "$tmp" \( -name '*.pdb' -o -name '*.xml' \) -delete
    [ -d "$install/cef/cache" ] && cp -a "$install/cef/cache" "$tmp/cache"
    rm -rf "$install/cef"; mv "$tmp" "$install/cef"
  fi
  echo "Synced cef/ Chromium runtime -> $install/cef (kept cef/cache)"
fi
