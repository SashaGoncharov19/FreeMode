#!/usr/bin/env bash
# =====================================================================================================
#  GTA Network on Linux - one-shot installer (Steam + Proton, GTA V Legacy)
#
#    curl -fsSL https://raw.githubusercontent.com/SashaGoncharov19/FreeMode/master/eng/setup-linux.sh \
#      | bash -s -- --name YourNick --shv ~/Downloads/ScriptHookV_1.0.XXXX.X.zip
#
#  What it does:
#    1. downloads the latest GitHub release (client package, Linux launcher, server, bot) into ~/GTANetwork
#       (or builds launcher/server/bot from this checkout with --build)
#    2. copies ScriptHookV.dll + dinput8.dll out of the ScriptHookV zip you downloaded from dev-c.com
#    3. writes settings.xml (launch method proton, your name, 127.0.0.1:4499 in the favourites)
#    4. installs .NET Framework 4.8 + VC++ runtime into the game's Proton prefix with protontricks
#    5. creates play.sh / server/start.sh / bot.sh and a desktop entry, then runs the launcher's doctor
#  Everything lives in one folder; delete it to uninstall (and run "GTANetwork.Launcher restore" first
#  if the game folder still contains the deployed files).
# =====================================================================================================
set -euo pipefail

REPO="${GTAN_REPO:-SashaGoncharov19/FreeMode}"
DIR="$HOME/GTANetwork"
RELEASE="latest"
SHV_ZIP=""
BUILD=0
NAME="${USER:-Player}"
GAME_PATH=""
METHOD="proton"
RUN_PROTONTRICKS=1
SERVER_ADDR="127.0.0.1:4499"
ASSUME_YES=0
CLIENT_ZIP=""

c_info=$'\033[1;34m'; c_ok=$'\033[1;32m'; c_warn=$'\033[1;33m'; c_err=$'\033[1;31m'; c_off=$'\033[0m'
info() { echo "${c_info}==>${c_off} $*"; }
ok()   { echo "${c_ok} ok ${c_off} $*"; }
warn() { echo "${c_warn}warn${c_off} $*"; }
die()  { echo "${c_err}error${c_off} $*" >&2; exit 1; }

usage() {
  cat <<USAGE
GTA Network Linux installer

  --dir <path>        install folder (default: $DIR)
  --release <tag>     GitHub release to install: "latest" (default, newest incl. pre-releases) or a tag like v0.1.0-alpha.1
  --client-zip <zip>  use an already downloaded gtanetwork-client-win64-*.zip instead of fetching it
  --build             build launcher/server/bot from this git checkout instead (needs the .NET 8 SDK);
                      the client package is still downloaded (it needs the Windows-built ScriptHookVDotNet)
  --shv <zip>         ScriptHookV archive from http://www.dev-c.com/gtav/scripthookv/ (default: newest ~/Downloads/ScriptHookV*.zip)
  --name <nick>       in-game name (default: $NAME)
  --game-path <dir>   folder with GTA5.exe if Steam auto-detection fails
  --method <m>        proton (default, no Steam launch options needed) | steam
  --server <ip:port>  server to put into the favourites (default: $SERVER_ADDR)
  --no-protontricks   do not install .NET Framework / VC++ runtime into the Proton prefix
  --yes               do not ask before long steps (protontricks)
  --repo <owner/repo> GitHub repository (default: $REPO)
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --dir) DIR="$2"; shift 2 ;;
    --release) RELEASE="$2"; shift 2 ;;
    --client-zip) CLIENT_ZIP="$2"; shift 2 ;;
    --build) BUILD=1; shift ;;
    --shv) SHV_ZIP="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --game-path) GAME_PATH="$2"; shift 2 ;;
    --method) METHOD="$2"; shift 2 ;;
    --server) SERVER_ADDR="$2"; shift 2 ;;
    --no-protontricks) RUN_PROTONTRICKS=0; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --repo) REPO="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "unknown option: $1" ;;
  esac
done

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required. Debian/Ubuntu: sudo apt install $2"; }
need curl curl
need unzip unzip
need python3 python3

ask() { # ask "question" -> 0 = yes
  [ "$ASSUME_YES" -eq 1 ] && return 0
  if [ -r /dev/tty ]; then
    read -r -p "$1 [Y/n] " answer < /dev/tty || answer="n"
    [[ -z "$answer" || "$answer" =~ ^[Yy] ]]
  else
    return 1
  fi
}

mkdir -p "$DIR/downloads" "$DIR/server" "$DIR/bin" "$DIR/logs"
DIR="$(cd "$DIR" && pwd)"
info "Installing into $DIR"

# ---------------------------------------------------------------------------------------------------
# 1. Release assets
# ---------------------------------------------------------------------------------------------------
release_file=""
resolve_release() {
  local url
  if [ "$RELEASE" = "latest" ]; then
    url="https://api.github.com/repos/$REPO/releases?per_page=10"
  else
    url="https://api.github.com/repos/$REPO/releases/tags/$RELEASE"
  fi
  release_file="$DIR/downloads/release.json"
  curl -fsSL -H 'Accept: application/vnd.github+json' -H 'User-Agent: gtan-setup' -o "$release_file" "$url" \
    || die "could not query GitHub releases of $REPO ($url)"
  RELEASE_TAG="$(python3 -c '
import json, sys
data = json.load(open(sys.argv[1]))
if isinstance(data, list):
    data = [r for r in data if not r.get("draft")]
    if not data: sys.exit("no release found; tag one (vX.Y.Z) or use --build")
    data = data[0]
print(data["tag_name"])' "$release_file")" || die "no usable release"
  info "Using release $RELEASE_TAG"
}

asset_url() { # asset_url <name prefix>
  python3 -c '
import json, sys
data = json.load(open(sys.argv[1])); prefix = sys.argv[2]
if isinstance(data, list):
    data = [r for r in data if not r.get("draft")][0]
for a in data.get("assets", []):
    if a["name"].startswith(prefix):
        print(a["browser_download_url"]); break' "$release_file" "$1"
}

download() { # download <prefix> <target file>
  local url; url="$(asset_url "$1")"
  [ -n "$url" ] || die "release $RELEASE_TAG has no asset starting with '$1'"
  if [ -f "$2" ] && [ "$(cat "$2.url" 2>/dev/null)" = "$url" ]; then ok "$(basename "$2") already downloaded"; return; fi
  info "Downloading $(basename "$url")"
  curl -fL --retry 3 --progress-bar -o "$2" "$url"
  echo "$url" > "$2.url"
}

if [ -z "$CLIENT_ZIP" ] || [ "$BUILD" -eq 0 ]; then
  resolve_release
fi
if [ -n "$CLIENT_ZIP" ]; then
  [ -f "$CLIENT_ZIP" ] || die "client zip not found: $CLIENT_ZIP"
  cp -f "$CLIENT_ZIP" "$DIR/downloads/client.zip"
else
  download "gtanetwork-client-win64-" "$DIR/downloads/client.zip"
fi
info "Unpacking the client package (bin/scripts, cef, images, Windows launchers)"
unzip -oq "$DIR/downloads/client.zip" -d "$DIR"

if [ "$BUILD" -eq 1 ]; then
  command -v dotnet >/dev/null 2>&1 || die "--build needs the .NET 8 SDK (https://dotnet.microsoft.com/download)"
  src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  [ -f "$src/GTANetwork.sln" ] || die "--build must be run from a git checkout (GTANetwork.sln not found next to eng/)"
  info "Building launcher, server and bot from $src"
  dotnet publish "$src/Launcher/GTANetwork.Launcher.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$DIR" -v quiet
  dotnet publish "$src/Tools/GTANetwork.Bot/GTANetwork.Bot.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$DIR" -v quiet
  dotnet publish "$src/Server/GTANetworkServer.csproj" -c Release -r linux-x64 --self-contained true -o "$DIR/server" -v quiet
  cp "$src/vehicleData.json" "$DIR/server/"
else
  download "gtanetwork-launcher-linux-x64-" "$DIR/downloads/launcher.zip"
  download "gtanetwork-bot-linux-x64-" "$DIR/downloads/bot.zip"
  download "gtanetwork-server-linux-x64-" "$DIR/downloads/server.zip"
  unzip -oq "$DIR/downloads/launcher.zip" -d "$DIR"
  unzip -oq "$DIR/downloads/bot.zip" -d "$DIR"
  unzip -oq "$DIR/downloads/server.zip" -d "$DIR/server"
fi
chmod +x "$DIR/GTANetwork.Launcher" "$DIR/GTANetwork.Bot" "$DIR/server/GTANetworkServer" 2>/dev/null || true
ok "Files in place: $(ls "$DIR" | tr '\n' ' ')"

# ---------------------------------------------------------------------------------------------------
# 2. ScriptHookV (not redistributable, the user downloads it)
# ---------------------------------------------------------------------------------------------------
if [ -z "$SHV_ZIP" ]; then
  SHV_ZIP="$(ls -t "$HOME"/Downloads/ScriptHookV*.zip 2>/dev/null | grep -v SDK | head -1 || true)"
fi
if [ -n "$SHV_ZIP" ] && [ -f "$SHV_ZIP" ]; then
  info "Taking ScriptHookV.dll and dinput8.dll from $SHV_ZIP"
  tmp="$(mktemp -d)"
  unzip -oq "$SHV_ZIP" -d "$tmp"
  for f in ScriptHookV.dll dinput8.dll; do
    found="$(find "$tmp" -iname "$f" | head -1)"
    [ -n "$found" ] || die "$f not found inside $SHV_ZIP"
    cp "$found" "$DIR/bin/$f"
  done
  rm -rf "$tmp"
  ok "ScriptHookV installed into $DIR/bin"
fi
if [ ! -f "$DIR/bin/ScriptHookV.dll" ] || [ ! -f "$DIR/bin/dinput8.dll" ]; then
  warn "ScriptHookV.dll / dinput8.dll are missing in $DIR/bin."
  warn "Download the ScriptHookV zip from http://www.dev-c.com/gtav/scripthookv/ (a browser is needed, the site blocks scripts)"
  warn "and re-run this script with --shv <zip>, or copy the two DLLs into $DIR/bin yourself."
fi

# ---------------------------------------------------------------------------------------------------
# 3. settings.xml
# ---------------------------------------------------------------------------------------------------
info "Detecting Steam / GTA V / Proton"
launcher_args=(--install-dir "$DIR" --method "$METHOD" --save)
[ -n "$GAME_PATH" ] && launcher_args+=(--game-path "$GAME_PATH")
doctor_out="$("$DIR/GTANetwork.Launcher" "${launcher_args[@]}" doctor 2>&1 || true)"
echo "$doctor_out" | sed 's/^/    /'

python3 - "$DIR/settings.xml" "$NAME" "$SERVER_ADDR" <<'PY'
import sys, xml.etree.ElementTree as ET
path, name, server = sys.argv[1:4]
tree = ET.parse(path); root = tree.getroot()
def elem(tag):
    e = root.find(tag)
    if e is None:
        e = ET.SubElement(root, tag)
    return e
elem("DisplayName").text = name
fav = elem("FavoriteServers")
if server not in [s.text for s in fav.findall("string")]:
    ET.SubElement(fav, "string").text = server
tree.write(path, encoding="utf-8", xml_declaration=True)
PY
ok "settings.xml: name \"$NAME\", launch method $METHOD, favourite server $SERVER_ADDR"

prefix="$(echo "$doctor_out" | sed -n 's/^Wine prefix:[[:space:]]*//p' | head -1)"
game_dir="$(echo "$doctor_out" | sed -n 's/^GTA V folder:[[:space:]]*//p' | head -1)"

# ---------------------------------------------------------------------------------------------------
# 4. .NET Framework + VC++ runtime inside the Proton prefix (ScriptHookVDotNet needs them)
# ---------------------------------------------------------------------------------------------------
if [ "$RUN_PROTONTRICKS" -eq 1 ]; then
  if [ -z "$prefix" ] || [ ! -d "$prefix" ]; then
    warn "No Proton prefix for GTA V found yet. Start the game once through Steam, then re-run this script."
  elif [ -f "$prefix/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319/clr.dll" ]; then
    ok ".NET Framework 4.x already present in the prefix"
  else
    pt=""
    if command -v protontricks >/dev/null 2>&1; then pt="protontricks";
    elif command -v flatpak >/dev/null 2>&1 && flatpak info com.github.Matoking.protontricks >/dev/null 2>&1; then pt="flatpak run com.github.Matoking.protontricks"; fi
    if [ -z "$pt" ]; then
      warn "protontricks is not installed. Install it and run:  protontricks 271590 -q dotnet48 vcrun2022"
      warn "  Debian/Ubuntu: sudo apt install protontricks    Arch: pacman -S protontricks    any: pipx install protontricks"
      warn "  Flatpak:       flatpak install flathub com.github.Matoking.protontricks"
    elif ask "Install .NET Framework 4.8 + VC++ 2022 runtime into the GTA V prefix now with protontricks (takes several minutes)?"; then
      info "Running: $pt 271590 -q dotnet48 vcrun2022"
      $pt 271590 -q dotnet48 vcrun2022 || warn "protontricks reported an error; check the output above"
    else
      warn "Skipped. Run later:  $pt 271590 -q dotnet48 vcrun2022"
    fi
  fi
fi

# ---------------------------------------------------------------------------------------------------
# 5. Helper scripts + desktop entry
# ---------------------------------------------------------------------------------------------------
cat > "$DIR/play.sh" <<SH
#!/usr/bin/env bash
# Deploys GTA Network into the game folder, starts GTA V through $METHOD and cleans up when the game exits.
exec "$DIR/GTANetwork.Launcher" "\$@"
SH
cat > "$DIR/server/start.sh" <<'SH'
#!/usr/bin/env bash
# Starts the dedicated server (settings.xml and resources/ live next to this script). UDP+TCP 4499.
cd "$(dirname "$0")" && exec ./GTANetworkServer "$@"
SH
cat > "$DIR/bot.sh" <<SH
#!/usr/bin/env bash
# Headless test client: joins the local server and lets you type chat / commands (no game needed).
exec "$DIR/GTANetwork.Bot" --host 127.0.0.1 --port 4499 --name "${NAME}-bot" --interactive "\$@"
SH
chmod +x "$DIR/play.sh" "$DIR/server/start.sh" "$DIR/bot.sh"

mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/gtanetwork.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=GTA Network
Comment=Multiplayer for GTA V (Legacy) through Proton
Exec=$DIR/play.sh
Path=$DIR
Icon=$DIR/images/logo64.ico
Terminal=true
Categories=Game;
DESKTOP
ok "Created play.sh, server/start.sh, bot.sh and a desktop entry"

# ---------------------------------------------------------------------------------------------------
# 6. Summary
# ---------------------------------------------------------------------------------------------------
echo
info "Done. Next steps:"
echo "  1. Start a server:            $DIR/server/start.sh"
echo "  2. (optional, no game) test:  $DIR/bot.sh        then type /help"
echo "  3. Play:                      $DIR/play.sh       (or the GTA Network desktop entry)"
echo "     In the game menu open Favorites and pick $SERVER_ADDR."
if [ "$METHOD" = "steam" ]; then
  echo "  Steam launch options for GTA V must be:  WINEDLLOVERRIDES=\"dinput8=n,b\" %command%"
fi
echo "  Check anytime:                $DIR/GTANetwork.Launcher doctor"
echo "  Logs:                         $DIR/logs/  (launcher.log, ScriptHookVDotNet-*.log, client logs)"
[ -n "$game_dir" ] && echo "  Game folder:                  $game_dir"
