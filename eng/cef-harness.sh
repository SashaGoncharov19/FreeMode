#!/usr/bin/env bash
# Run Tools/CefHarness under Proton, in the game's Wine prefix. By default it drives GTANetwork.CefHost.exe (the
# separate browser process) exactly like the in-game client does and checks pixels and the page bridge; with
# --in-process / --appdomain it starts Chromium inside the harness instead (how the client did it before, and how
# it fails in a second AppDomain). No GTA V, DXVK or ScriptHookV involved.
#
# Usage: eng/cef-harness.sh [--build] [--install-cef] [--alone] [harness options...]
#   --build        build the harness first (in the dev container when dotnet is not on the host)
#   --install-cef  use the Chromium runtime of your install (~/GTANetwork/cef) instead of the freshly built one
#   --alone        run a copy of the exe from an otherwise empty folder (the default AppDomain then sees no CefSharp,
#                  as in the game); combine with --appdomain to reproduce the ScriptHookVDotNet situation
#   harness options: --external-pump, --gpu, --gpu-process, --switch k=v, --no-switch k, --timeout s, ... (--help)
#
# Output: the harness lines on the console; harness.log, harness-chromium.log and cache/ in the harness output
# folder; the Wine log (PROTON_LOG=1, same as play.sh --debug) in ~/steam-271590.log.
# Steam, Proton and the prefix are read from <install>/settings.xml (written by setup-linux.sh / the launcher).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install="${GTAN_INSTALL:-$HOME/GTANetwork}"
out="$root/Tools/CefHarness/bin/Release/net48"
exe="$out/CefHarness.exe"

build=0; install_cef=0; alone=0
while [ $# -gt 0 ]; do
  case "$1" in
    --build) build=1; shift ;;
    --install-cef) install_cef=1; shift ;;
    --alone) alone=1; shift ;;
    *) break ;;
  esac
done
winpath() { printf 'Z:%s' "$1" | sed 's|/|\\|g'; }

host_out="$root/Subprocess/GTANetwork.CefHost/bin/Release/net48"
if [ "$build" = 1 ]; then
  if command -v dotnet >/dev/null 2>&1; then
    dotnet build "$root/Tools/CefHarness/CefHarness.csproj" -c Release -nologo
    dotnet build "$root/Subprocess/GTANetwork.CefHost/GTANetwork.CefHost.csproj" -c Release -nologo
  else
    (cd "$root" && docker compose run --rm dev bash -c "dotnet build Tools/CefHarness/CefHarness.csproj -c Release -nologo && dotnet build Subprocess/GTANetwork.CefHost/GTANetwork.CefHost.csproj -c Release -nologo")
  fi
fi
[ -f "$exe" ] || { echo "No harness build at $exe. Run: eng/cef-harness.sh --build" >&2; exit 1; }

# The prefix is the game's: never start a second Wine session in it while GTA V runs.
if pgrep -f '[G]TA5\.exe|[P]layGTAV\.exe|[G]TAVLauncher\.exe' >/dev/null 2>&1; then
  echo "GTA V is running in this prefix; close it before running the harness." >&2; exit 1
fi

xml() { sed -n "s|.*<$1>\(.*\)</$1>.*|\1|p" "$install/settings.xml" 2>/dev/null | head -1; }
steam="$(xml SteamPath)"; proton="$(xml ProtonPath)"; prefix="$(xml ProtonPrefixPath)"
: "${steam:=$HOME/.steam/debian-installation}"
: "${prefix:=$steam/steamapps/compatdata/271590/pfx}"
if [ -z "$proton" ]; then
  for d in "$steam/steamapps/common/Proton - Experimental" "$steam"/steamapps/common/Proton*; do
    [ -f "$d/proton" ] && { proton="$d"; break; }
  done
fi
[ -f "$proton/proton" ] || { echo "No Proton found (settings.xml <ProtonPath> or $steam/steamapps/common/Proton*)" >&2; exit 1; }
[ -d "$prefix/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319" ] || echo "warning: .NET Framework 4.x not found in $prefix; the harness is a .NET Framework 4.8 program" >&2

args=("$@")
in_process=0
for a in "${args[@]}"; do case "$a" in --in-process|--appdomain) in_process=1 ;; esac; done
if [ "$install_cef" = 1 ]; then
  args=(--cef-dir "$(winpath "$install/cef")" --host "$(winpath "$install/cef/GTANetwork.CefHost.exe")" "${args[@]}")
  case " ${args[*]} " in *" --ui-root "*) ;; *) [ -d "$install/ui" ] && args=(--ui-root "$(winpath "$install/ui")" "${args[@]}") ;; esac
elif [ "$in_process" = 0 ]; then
  [ -f "$host_out/GTANetwork.CefHost.exe" ] || { echo "No host build at $host_out. Run: eng/cef-harness.sh --build" >&2; exit 1; }
  args=(--host "$(winpath "$host_out/GTANetwork.CefHost.exe")" "${args[@]}")
  # the client's pages (ui/loader): the harness checks that https://gtan/loader/index.html renders
  case " ${args[*]} " in *" --ui-root "*) ;; *) [ -d "$root/ui" ] && args=(--ui-root "$(winpath "$root/ui")" "${args[@]}") ;; esac
fi
if [ "$alone" = 1 ]; then
  # The exe alone in a folder, as GTA5.exe is: the default AppDomain can resolve none of our assemblies; the
  # harness resolves them itself from --deps-dir (like ScriptHookVDotNet does for bin\scripts).
  mkdir -p "$out/alone"
  cp -f "$out/CefHarness.exe" "$out/CefHarness.exe.config" "$out/alone/"
  cp -f "$out/CefHarness.pdb" "$out/alone/" 2>/dev/null || true
  exe="$out/alone/CefHarness.exe"
  args=(--deps-dir "$(winpath "$out")" --cef-dir "$(winpath "$out/cef")" --log-dir "$(winpath "$out")" "${args[@]}")
fi

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam"
export STEAM_COMPAT_DATA_PATH="$(dirname "$prefix")"
export SteamAppId=271590 SteamGameId=271590
export PROTON_LOG="${PROTON_LOG:-1}"

echo "== CefHarness under $(basename "$proton"), prefix $prefix"
echo "== $exe ${args[*]:-}"
cd "$out"
set +e
"$proton/proton" run "$exe" "${args[@]}"
rc=$?
set -e
echo "== harness exit code $rc (0 ok, 2 Chromium did not start, 3 no pixels, 4 exception, 5 host died or hung, 7 no resourceCall)"
if [ -f "$HOME/steam-271590.log" ]; then
  cp -f "$HOME/steam-271590.log" "$out/harness-wine.log"   # Proton overwrites it on the next start
  echo "== Wine log ($out/harness-wine.log): $(wc -l < "$out/harness-wine.log") lines; exceptions:"
  grep -E 'Unhandled exception|seh:dispatch_exception code=(e0434352|c0000005|c00000fd|c0000409|80000003)|info\[0\]=0000000080070002' "$out/harness-wine.log" | sort | uniq -c | sort -rn | head -8 || true
fi
echo "== logs: $out/harness.log, $out/harness-host.log, $out/harness-chromium.log, $out/harness-wine.log"
exit $rc
