#!/usr/bin/env bash
# Drop a freshly built managed client into an existing GTA Network install so you can test a code
# change without waiting for CI or a release. By default: the managed client assemblies (bin/scripts) and the
# managed part of the browser host (cef/GTANetwork.CefHost.exe and the assemblies it needs), which is all that
# changes when you edit C#. --cef also refreshes the whole Chromium runtime in cef/ (needed only when the
# CefSharp version changed).
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
host_out="$root/Subprocess/GTANetwork.CefHost/bin/$config/net48"
[ -f "$out/GTANetwork.dll" ] || { echo "No client build at $out. Build it first: eng/dev-build-client.sh -c $config" >&2; exit 1; }
[ -d "$install/bin" ] || { echo "$install is not a GTA Network install (no bin/). Pass the path or set GTAN_INSTALL." >&2; exit 1; }

scripts="$install/bin/scripts"
mkdir -p "$scripts"

# Copy through a temporary file and rename: a game that is still running keeps its mapped copies (Wine maps the
# old inode), and the next start picks up the new files.
put() { cp -f "$1" "$2.tmp.$$" && mv -f "$2.tmp.$$" "$2"; }

# ScriptHookVDotNet loads every managed DLL in bin/scripts. Copy them all except SHVDN itself
# (the real C++/CLI build lives in bin/ and SHVDN removes any copy from scripts/ anyway).
copied=0
for f in "$out"/*.dll; do
  b="$(basename "$f")"
  [ "$b" = "ScriptHookVDotNet.dll" ] && continue
  put "$f" "$scripts/$b" && copied=$((copied + 1))
done
[ -f "$out/GTANetwork.dll.config" ] && put "$out/GTANetwork.dll.config" "$scripts/GTANetwork.dll.config"
# Assemblies the build no longer produces (e.g. CefSharp, which moved into the browser host) must not linger:
# ScriptHookVDotNet loads every DLL it finds in bin/scripts.
removed=0
for f in "$scripts"/*.dll; do
  b="$(basename "$f")"
  [ -f "$out/$b" ] || { rm -f "$f"; removed=$((removed + 1)); }
done

# ClearScript's native V8 is also loaded from bin/ (JavascriptHook.ConfigureClearScript adds bin\ to
# the auxiliary search path), so keep a copy there too.
[ -f "$out/ClearScriptV8.win-x64.dll" ] && put "$out/ClearScriptV8.win-x64.dll" "$install/bin/ClearScriptV8.win-x64.dll"

echo "Synced $copied managed DLLs -> $scripts${removed:+ (removed $removed stale)}"

# The browser host (Chromium in its own process) lives in cef/ next to the runtime. Its managed files are small
# and change with the shared protocol, so they are synced every time; the runtime itself only with --cef.
if [ -f "$host_out/GTANetwork.CefHost.exe" ]; then
  mkdir -p "$install/cef"
  hcopied=0
  for b in GTANetwork.CefHost.exe GTANetwork.CefHost.exe.config GTANetwork.CefHost.pdb GTANetworkShared.dll Newtonsoft.Json.dll protobuf-net.dll \
           CefSharp.dll CefSharp.Core.dll CefSharp.OffScreen.dll SharpDX.dll SharpDX.DXGI.dll SharpDX.Direct3D11.dll; do
    [ -f "$host_out/$b" ] && put "$host_out/$b" "$install/cef/$b" && hcopied=$((hcopied + 1))
  done
  echo "Synced $hcopied browser host files -> $install/cef"
else
  echo "warning: no browser host build at $host_out (dotnet build Subprocess/GTANetwork.CefHost); cef/GTANetwork.CefHost.exe not updated" >&2
fi

realign() { # Wine maps PE images from disk only with page-aligned sections (eng/pe-realign.py); harmless elsewhere
  command -v python3 >/dev/null 2>&1 || { echo "warning: python3 not found; cef/ DLLs stay 512-byte aligned (Wine copies them per process)" >&2; return 0; }
  python3 "$root/eng/pe-realign.py" "$1" | tail -n 1
}
[ -d "$install/cef" ] && realign "$install/cef"

if [ "$cef" = 1 ]; then
  [ -f "$host_out/libcef.dll" ] || { echo "No Chromium runtime in $host_out (build Subprocess/GTANetwork.CefHost)." >&2; exit 1; }
  mkdir -p "$install/cef"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'cache/' --exclude '*.xml' --exclude 'CefSharp.*.pdb' "$host_out/" "$install/cef/"
  else
    # rsync-free fallback: rebuild cef/ but keep the existing cache/.
    tmp="$install/cef.tmp.$$"; rm -rf "$tmp"; cp -a "$host_out" "$tmp"
    find "$tmp" \( -name 'CefSharp.*.pdb' -o -name '*.xml' \) -delete
    [ -d "$install/cef/cache" ] && cp -a "$install/cef/cache" "$tmp/cache"
    rm -rf "$install/cef"; mv "$tmp" "$install/cef"
  fi
  echo "Synced cef/ (browser host + Chromium runtime) -> $install/cef (kept cef/cache)"
  realign "$install/cef"
fi
