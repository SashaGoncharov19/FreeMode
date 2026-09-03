#!/usr/bin/env bash
# GTA Network - Linux one-shot installer and updater.
#
#   curl -fsSL https://raw.githubusercontent.com/SashaGoncharov19/FreeMode/master/eng/setup-linux.sh \
#      | bash -s -- --name YourNick [--shv ~/Downloads/ScriptHookV_XXXX.zip]
#
# What it does:
#    1. downloads the client package + the self-contained Linux launcher, server and bot from a GitHub release
#    2. copies ScriptHookV.dll + dinput8.dll out of the ScriptHookV zip you downloaded from dev-c.com
#    3. finds Steam / GTA V / Proton and writes settings.xml (name, launch method, favourite server)
#    4. installs protontricks when needed (Debian: enables "contrib") and .NET Framework 4.8 + the VC++
#       runtime into the game's Proton prefix (ScriptHookVDotNet needs them)
#    5. creates play.sh, server/start.sh, bot.sh, update.sh and a desktop entry
#    6. keeps a copy of itself in the install folder: play.sh & co run "update.sh --quiet" first, which asks
#       GitHub for a newer release and installs it (switch off with --auto-update off)
#
# Re-running is safe: downloads are cached, settings.xml, ScriptHookV and the server's settings.xml are kept.
set -euo pipefail

REPO="${GTAN_REPO:-SashaGoncharov19/FreeMode}"
DIR="$HOME/GTANetwork"
RELEASE="latest"
SHV_ZIP=""
BUILD=0
NAME="${USER:-Player}"
NAME_SET=0
GAME_PATH=""
METHOD="proton"
RUN_PROTONTRICKS=1
SERVER_ADDR="127.0.0.1:4499"
ASSUME_YES=0
CLIENT_ZIP=""
AUTO_UPDATE=1
DOTNET_PROTON="auto"
MODE="install"                 # install | update | check
QUIET=0
SUDO="sudo"; [ "$(id -u)" = 0 ] && SUDO=""
APT_SOURCES_LIST="${APT_SOURCES_LIST:-/etc/apt/sources.list}"      # overridable for tests
APT_SOURCES_DIR="${APT_SOURCES_DIR:-/etc/apt/sources.list.d}"
APT_LISTS_DIR="${APT_LISTS_DIR:-/var/lib/apt/lists}"
CONF_KEYS="REPO RELEASE METHOD SERVER_ADDR NAME GAME_PATH AUTO_UPDATE RUN_PROTONTRICKS DOTNET_PROTON"
ORIG_ARGS=("$@")

c_info=$'\033[1;34m'; c_ok=$'\033[1;32m'; c_warn=$'\033[1;33m'; c_err=$'\033[1;31m'; c_off=$'\033[0m'
say()  { echo "${c_info}==>${c_off} $*"; }
info() { [ "$QUIET" -eq 1 ] || say "$@"; }
ok()   { [ "$QUIET" -eq 1 ] || echo "${c_ok} ok ${c_off} $*"; }
warn() { echo "${c_warn}warn${c_off} $*"; }
die()  { echo "${c_err}error${c_off} $*" >&2; exit 1; }

usage() {
  cat <<USAGE
GTA Network Linux installer / updater

  --dir <path>        install folder (default: $DIR)
  --release <tag>     GitHub release to install: "latest" (default, newest incl. pre-releases) or a tag like v0.1.0-alpha.2
  --client-zip <zip>  use an already downloaded gtanetwork-client-win64-*.zip instead of fetching it
  --build             build launcher/server/bot from this git checkout instead (needs the .NET 8 SDK);
                      the client package is still downloaded (it needs the Windows-built ScriptHookVDotNet)
  --shv <zip>         ScriptHookV archive from http://www.dev-c.com/gtav/scripthookv/ (default: newest ~/Downloads/ScriptHookV*.zip)
  --name <nick>       in-game name (default: $NAME)
  --game-path <dir>   folder with GTA5.exe if Steam auto-detection fails
  --method <m>        proton (default, no Steam launch options needed) | steam
  --server <ip:port>  server to put into the favourites (default: $SERVER_ADDR)
  --no-protontricks   do not install protontricks / .NET Framework / VC++ runtime into the Proton prefix
  --dotnet-proton <p> Proton to install .NET with: auto (default: the game's Proton, then a stable one when the
                      .NET installer breaks), or a name such as "Proton 8.0" or GE-Proton8-32 (downloaded if missing)
  --auto-update on|off  play.sh, server/start.sh and bot.sh check GitHub for a newer release first (default: on)
  --update            update mode (what update.sh does): install the newest release only if it differs from the
                      installed one; keeps settings.xml, ScriptHookV and server/settings.xml
  --check             only report whether a newer release exists (exit code 10 = update available)
  --quiet             less output (used by the pre-launch update check)
  --yes               do not ask before long steps (protontricks, apt)
  --repo <owner/repo> GitHub repository (default: $REPO)

Options are remembered in <dir>/setup.conf, so "update.sh" needs none of them.
USAGE
}

# --dir first, so that the saved options of an existing installation become the defaults
for ((i = 1; i <= $#; i++)); do
  if [ "${!i}" = "--dir" ]; then j=$((i + 1)); DIR="${!j:-$DIR}"; fi
done
load_conf() {
  local k v
  while IFS='=' read -r k v || [ -n "$k" ]; do
    case " $CONF_KEYS " in *" $k "*) printf -v "$k" '%s' "$v" ;; esac
  done < "$1"
}
[ -f "$DIR/setup.conf" ] && load_conf "$DIR/setup.conf"

while [ $# -gt 0 ]; do
  case "$1" in
    --dir) DIR="$2"; shift 2 ;;
    --release) RELEASE="$2"; shift 2 ;;
    --client-zip) CLIENT_ZIP="$2"; shift 2 ;;
    --build) BUILD=1; shift ;;
    --shv) SHV_ZIP="$2"; shift 2 ;;
    --name) NAME="$2"; NAME_SET=1; shift 2 ;;
    --game-path) GAME_PATH="$2"; shift 2 ;;
    --method) METHOD="$2"; shift 2 ;;
    --server) SERVER_ADDR="$2"; shift 2 ;;
    --no-protontricks) RUN_PROTONTRICKS=0; shift ;;
    --dotnet-proton) DOTNET_PROTON="$2"; shift 2 ;;
    --auto-update) case "$2" in on|1|true) AUTO_UPDATE=1 ;; off|0|false) AUTO_UPDATE=0 ;; *) die "--auto-update on|off" ;; esac; shift 2 ;;
    --update) MODE="update"; shift ;;
    --check) MODE="check"; shift ;;
    --quiet|-q) QUIET=1; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --repo) REPO="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "unknown option: $1" ;;
  esac
done
[ "$METHOD" = "proton" ] || [ "$METHOD" = "steam" ] || die "--method must be proton or steam"

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

save_conf() { local k; { for k in $CONF_KEYS; do echo "$k=${!k}"; done; } > "$DIR/setup.conf"; }

write_script() { # write_script <path>   (content on stdin); atomic, so a running copy is not disturbed
  cat > "$1.tmp" && chmod +x "$1.tmp" && mv -f "$1.tmp" "$1"
}

running_from_dir() { # prints programs of this installation that are running right now
  local p exe
  for p in /proc/[0-9]*; do
    exe="$(readlink "$p/exe" 2>/dev/null)" || continue
    case "$exe" in "$DIR"/*) echo "${exe% (deleted)}" ;; esac
  done | sort -u
}

mkdir -p "$DIR/downloads" "$DIR/server" "$DIR/bin" "$DIR/logs"
DIR="$(cd "$DIR" && pwd)"
[ "$MODE" = "install" ] && info "Installing into $DIR"

if [ "$MODE" = "update" ] && [ "$AUTO_UPDATE" -eq 0 ] && [ "$QUIET" -eq 1 ]; then
  save_conf   # remember options given on the command line (e.g. --auto-update off)
  exit 0      # pre-launch check with auto-update switched off
fi

# ---------------------------------------------------------------------------------------------------
# 1. Release assets
# ---------------------------------------------------------------------------------------------------
release_file="$DIR/downloads/release.json"
RELEASE_TAG=""
resolve_release() {
  local url
  if [ "$RELEASE" = "latest" ]; then
    url="https://api.github.com/repos/$REPO/releases?per_page=10"
  else
    url="https://api.github.com/repos/$REPO/releases/tags/$RELEASE"
  fi
  if ! curl -fsSL --max-time 30 -H 'Accept: application/vnd.github+json' -H 'User-Agent: gtan-setup' -o "$release_file.tmp" "$url"; then
    rm -f "$release_file.tmp"
    [ "$MODE" = "install" ] && die "could not query GitHub releases of $REPO ($url)"
    warn "could not reach GitHub to check for updates ($url)"
    return 1
  fi
  mv -f "$release_file.tmp" "$release_file"
  RELEASE_TAG="$(python3 -c '
import json, sys
data = json.load(open(sys.argv[1]))
if isinstance(data, list):
    data = [r for r in data if not r.get("draft")]
    if not data: sys.exit("no release found; tag one (vX.Y.Z) or use --build")
    data = data[0]
print(data["tag_name"])' "$release_file")" || die "no usable release"
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
  say "Downloading $(basename "$url")"
  curl -fL --retry 3 --progress-bar -o "$2" "$url"
  echo "$url" > "$2.url"
}

self_update() { # update mode: fetch the setup script shipped with the new release and re-run it if it changed
  [ "${GTAN_SETUP_REEXEC:-0}" = 1 ] && return 0
  local url tmp; url="${GTAN_SETUP_URL:-$(asset_url "setup-linux.sh")}"
  [ -n "$url" ] || return 0
  tmp="$DIR/setup-linux.sh.new"
  curl -fsSL --max-time 60 -o "$tmp" "$url" || { rm -f "$tmp"; return 0; }
  if ! grep -q -- '--update' "$tmp" || { [ -f "$DIR/setup-linux.sh" ] && cmp -s "$tmp" "$DIR/setup-linux.sh"; }; then
    rm -f "$tmp"; return 0
  fi
  chmod +x "$tmp" && mv -f "$tmp" "$DIR/setup-linux.sh"
  say "Setup script updated, re-running it"
  GTAN_SETUP_REEXEC=1 exec bash "$DIR/setup-linux.sh" ${ORIG_ARGS[@]+"${ORIG_ARGS[@]}"}
}

installed_tag="$(cat "$DIR/release.txt" 2>/dev/null || true)"
if [ "$BUILD" -eq 0 ] || [ -z "$CLIENT_ZIP" ]; then
  if ! resolve_release; then exit 0; fi   # only reachable in update/check mode: start without updating
fi
if [ "$MODE" = "check" ]; then
  say "installed: ${installed_tag:-none}, newest: $RELEASE_TAG"
  [ "$installed_tag" = "$RELEASE_TAG" ] && exit 0 || exit 10
fi
if [ "$MODE" = "update" ]; then
  if [ "$installed_tag" = "$RELEASE_TAG" ] && [ -z "$SHV_ZIP" ] && [ "$NAME_SET" -eq 0 ]; then
    save_conf
    say "GTA Network is up to date ($RELEASE_TAG)"
    exit 0
  fi
  busy="$(running_from_dir)"
  if [ -n "$busy" ]; then
    warn "release $RELEASE_TAG is available, but this is still running: $(echo "$busy" | tr '\n' ' ')"
    warn "stop it and run $DIR/update.sh"
    exit 0
  fi
  if [ "$installed_tag" != "$RELEASE_TAG" ]; then
    [ "${GTAN_SETUP_REEXEC:-0}" = 1 ] || say "Updating GTA Network ${installed_tag:-(unknown)} -> $RELEASE_TAG"
    self_update
  fi
else
  say "Using release $RELEASE_TAG"
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
  AUTO_UPDATE=0   # a build from source must not be replaced by a release behind your back
else
  download "gtanetwork-launcher-linux-x64-" "$DIR/downloads/launcher.zip"
  download "gtanetwork-bot-linux-x64-" "$DIR/downloads/bot.zip"
  download "gtanetwork-server-linux-x64-" "$DIR/downloads/server.zip"
  unzip -oq "$DIR/downloads/launcher.zip" -d "$DIR"
  unzip -oq "$DIR/downloads/bot.zip" -d "$DIR"
  if [ -f "$DIR/server/settings.xml" ]; then
    unzip -oq "$DIR/downloads/server.zip" -x settings.xml -d "$DIR/server"   # keep the server configuration
  else
    unzip -oq "$DIR/downloads/server.zip" -d "$DIR/server"
  fi
fi
chmod +x "$DIR/GTANetwork.Launcher" "$DIR/GTANetwork.Bot" "$DIR/server/GTANetworkServer" 2>/dev/null || true
ok "Files in place: $(ls "$DIR" | tr '\n' ' ')"

# ---------------------------------------------------------------------------------------------------
# 2. ScriptHookV (not redistributable, the user downloads it)
# ---------------------------------------------------------------------------------------------------
if [ -z "$SHV_ZIP" ] && { [ ! -f "$DIR/bin/ScriptHookV.dll" ] || [ ! -f "$DIR/bin/dinput8.dll" ]; }; then
  SHV_ZIP="$(ls -t "$HOME"/Downloads/ScriptHookV*.zip 2>/dev/null | grep -v SDK | head -1 || true)"
fi
if [ -n "$SHV_ZIP" ] && [ -f "$SHV_ZIP" ]; then
  say "Taking ScriptHookV.dll and dinput8.dll from $SHV_ZIP"
  tmp="$(mktemp -d)"
  unzip -oq "$SHV_ZIP" -d "$tmp"
  for f in ScriptHookV.dll dinput8.dll; do
    # Newer archives (ScriptHookV_<legacy build>_<enhanced build>.zip) may ship the Legacy and the
    # Enhanced build side by side. GTA Network works with GTA V Legacy only, so prefer that one.
    candidates="$(find "$tmp" -iname "$f" | sort)"
    [ -n "$candidates" ] || die "$f not found inside $SHV_ZIP"
    found="$(printf '%s\n' "$candidates" | grep -i legacy | head -1 || true)"
    [ -n "$found" ] || found="$(printf '%s\n' "$candidates" | grep -v -i -E 'enhanced|/en[_/-]|_en[_/.-]' | head -1 || true)"
    [ -n "$found" ] || found="$(printf '%s\n' "$candidates" | head -1)"
    if [ "$(printf '%s\n' "$candidates" | wc -l)" -gt 1 ]; then
      warn "$f appears more than once in the archive, using ${found#"$tmp"/} (Legacy). All copies:"
      printf '%s\n' "$candidates" | sed "s#^$tmp/#       #"
    else
      info "  $f <- ${found#"$tmp"/}"
    fi
    cp "$found" "$DIR/bin/$f"
  done
  rm -rf "$tmp"
  ok "ScriptHookV installed into $DIR/bin"
elif [ -n "$SHV_ZIP" ]; then
  die "ScriptHookV archive not found: $SHV_ZIP"
fi
if [ ! -f "$DIR/bin/ScriptHookV.dll" ] || [ ! -f "$DIR/bin/dinput8.dll" ]; then
  warn "ScriptHookV.dll / dinput8.dll are missing in $DIR/bin."
  warn "Download the ScriptHookV zip from http://www.dev-c.com/gtav/scripthookv/ (a browser is needed, the site blocks scripts)"
  warn "into ~/Downloads and run $DIR/update.sh, or pass --shv <zip>, or copy the two DLLs into $DIR/bin yourself."
fi

# ---------------------------------------------------------------------------------------------------
# 3. settings.xml
# ---------------------------------------------------------------------------------------------------
info "Detecting Steam / GTA V / Proton"
launcher_args=(--install-dir "$DIR" --method "$METHOD" --save)
[ -n "$GAME_PATH" ] && launcher_args+=(--game-path "$GAME_PATH")
doctor_out="$("$DIR/GTANetwork.Launcher" "${launcher_args[@]}" doctor 2>&1 || true)"
if [ "$QUIET" -eq 1 ]; then
  echo "$doctor_out" | grep -E '^\[WARN\]' | sed 's/^/    /' || true
else
  echo "$doctor_out" | sed 's/^/    /'
fi

NAME="$(python3 - "$DIR/settings.xml" "$NAME" "$NAME_SET" "$SERVER_ADDR" <<'PY'
import sys, xml.etree.ElementTree as ET
path, name, name_set, server = sys.argv[1:5]
tree = ET.parse(path); root = tree.getroot()
def elem(tag):
    e = root.find(tag)
    if e is None:
        e = ET.SubElement(root, tag)
    return e
dn = elem("DisplayName")
if name_set == "1" or not (dn.text or "").strip():
    dn.text = name           # never overwrite a name that was changed in the game menu
fav = elem("FavoriteServers")
if server not in [s.text for s in fav.findall("string")]:
    ET.SubElement(fav, "string").text = server
ms = elem("MasterServerAddress")
if "master.gtanet.work" in (ms.text or ""):
    ms.text = ""              # the original master server is gone; empty = do not contact any
tree.write(path, encoding="utf-8", xml_declaration=True)
print(dn.text)
PY
)"
ok "settings.xml: name \"$NAME\", launch method $METHOD, favourite server $SERVER_ADDR"

prefix="$(echo "$doctor_out" | sed -n 's/^Wine prefix:[[:space:]]*//p' | head -1)"
mapfile -t steam_libs < <(echo "$doctor_out" | sed -n 's/^Steam libraries:[[:space:]]*//p' | head -1 | sed 's/, /\n/g')
steam_root="$(echo "$doctor_out" | sed -n 's/^Steam root:[[:space:]]*//p' | head -1)"
case "$steam_root" in "not found"*) steam_root="" ;; esac
game_dir="$(echo "$doctor_out" | sed -n 's/^GTA V folder:[[:space:]]*//p' | head -1)"
case "$prefix" in "not found"*|"NOT FOUND"*) prefix="" ;; esac
case "$game_dir" in "not found"*|"NOT FOUND"*) game_dir="" ;; esac

# ---------------------------------------------------------------------------------------------------
# 4. protontricks + .NET Framework + VC++ runtime inside the Proton prefix (ScriptHookVDotNet needs them)
# ---------------------------------------------------------------------------------------------------
dotnet_present() { [ -f "$prefix/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319/clr.dll" ]; }

pt=""
find_protontricks() {
  pt=""
  if [ -x "$DIR/tools/bin/protontricks" ]; then pt="$DIR/tools/bin/protontricks"
  elif command -v protontricks >/dev/null 2>&1; then pt="protontricks"
  elif command -v flatpak >/dev/null 2>&1 && flatpak info com.github.Matoking.protontricks >/dev/null 2>&1; then
    pt="flatpak run com.github.Matoking.protontricks"
  fi
}

apt_candidate() { apt-cache policy "$1" 2>/dev/null | sed -n 's/^ *Candidate: *//p' | head -1; }
is_debian() { [ -r /etc/os-release ] && grep -q -E '^ID=debian' /etc/os-release; }

enable_debian_contrib() { # adds "contrib" to every APT source whose Release file advertises it; 0 = changed something
  local plan orig tmp changed=0
  plan="$(python3 - "$APT_LISTS_DIR" "$APT_SOURCES_LIST" "$APT_SOURCES_DIR" "$DIR/downloads" <<'PY'
import glob, os, re, sys
lists_dir, sources_list, sources_dir, out_dir = sys.argv[1:5]

def has_contrib(uri, suite):
    """(True/False, reason): does the repository <uri> <suite> offer a "contrib" component? Official
    debian.org hosts always do; any other repository is checked against its cached Release file."""
    if re.match(r'^https?://([^/]*\.)?debian\.org/', uri):
        return True, 'official Debian repository'
    hp = re.sub(r'^[a-z][a-z0-9+.-]*://', '', uri).rstrip('/').replace('/', '_')
    for name in (hp + '_dists_' + suite + '_InRelease', hp + '_dists_' + suite + '_Release'):
        path = os.path.join(lists_dir, name)
        if os.path.isfile(path):
            for line in open(path, errors='replace'):
                if line.startswith('Components:'):
                    comps = [c.rsplit('/', 1)[-1] for c in line.split()[1:]]
                    if 'contrib' in comps:
                        return True, 'its Release file lists contrib'
                    return False, 'its Release file has no contrib (' + ' '.join(comps) + ')'
            return False, 'its Release file has no Components line'
    return False, 'no cached Release file for it (apt update?) and not a debian.org host'

def diag(path, what, comps, reason):
    print('[apt] %s: %s [%s] -> %s' % (path, what, ' '.join(comps), reason), file=sys.stderr)

def emit(path, lines):
    tmp = os.path.join(out_dir, 'apt-' + os.path.basename(path) + '.new')
    with open(tmp, 'w') as fh:
        fh.write('\n'.join(lines))
    print(path + '\t' + tmp)

classic = re.compile(r'^(deb(?:-src)?\s+(?:\[[^\]]*\]\s+)?(\S+)\s+(\S+)\s+)([^#]*?)(\s*(?:#.*)?)$')
for path in [sources_list] + sorted(glob.glob(os.path.join(sources_dir, '*.list'))):
    if not os.path.isfile(path):
        continue
    lines = open(path, errors='replace').read().split('\n')
    changed = False
    for i, line in enumerate(lines):
        m = classic.match(line)
        if not m:
            continue
        uri, suite, comps = m.group(2), m.group(3), m.group(4).split()
        if not comps or suite.endswith('/'):
            diag(path, uri + ' ' + suite, comps, 'flat repository, no components')
            continue
        if 'contrib' in comps:
            diag(path, uri + ' ' + suite, comps, 'already has contrib')
            continue
        okay, reason = has_contrib(uri, suite)
        diag(path, uri + ' ' + suite, comps, ('adding contrib: ' if okay else 'skipped: ') + reason)
        if not okay:
            continue
        lines[i] = m.group(1) + ' '.join(comps + ['contrib']) + m.group(5)
        changed = True
    if changed:
        emit(path, lines)

for path in sorted(glob.glob(os.path.join(sources_dir, '*.sources'))):
    lines = open(path, errors='replace').read().split('\n')
    changed = False
    stanza = []
    def process(st):
        global changed
        fields = {}
        for idx, l in st:
            mm = re.match(r'^([A-Za-z-]+):\s*(.*?)\s*$', l)
            if mm:
                fields[mm.group(1).lower()] = (idx, mm.group(2))
        if not all(k in fields for k in ('uris', 'suites', 'components')):
            return
        if fields.get('enabled', (0, 'yes'))[1].lower() == 'no':
            return
        comps = fields['components'][1].split()
        uris, suites = fields['uris'][1].split(), fields['suites'][1].split()
        what = ' '.join(uris) + ' ' + ' '.join(suites)
        if 'contrib' in comps:
            diag(path, what, comps, 'already has contrib')
            return
        if not uris or not suites:
            return
        verdicts = [has_contrib(u, s) for u in uris for s in suites]
        if all(v[0] for v in verdicts):
            diag(path, what, comps, 'adding contrib: ' + verdicts[0][1])
            lines[fields['components'][0]] = 'Components: ' + ' '.join(comps + ['contrib'])
            changed = True
        else:
            diag(path, what, comps, 'skipped: ' + next(v[1] for v in verdicts if not v[0]))
    for idx, l in enumerate(lines):
        if l.strip() == '':
            if stanza:
                process(stanza)
            stanza = []
        else:
            stanza.append((idx, l))
    if stanza:
        process(stanza)
    if changed:
        emit(path, lines)
PY
)"
  [ -n "$plan" ] || return 1
  while IFS=$'\t' read -r orig tmp; do
    [ -n "$orig" ] || continue
    [ -e "$orig.gtanetwork.bak" ] || $SUDO cp "$orig" "$orig.gtanetwork.bak"
    $SUDO cp "$tmp" "$orig" && rm -f "$tmp"
    info "Enabled contrib in $orig (backup: $orig.gtanetwork.bak)"; changed=1
  done <<< "$plan"
  [ "$changed" -eq 1 ]
}

install_protontricks() {
  local cand
  # a) newest protontricks from PyPI + newest winetricks from GitHub in a private venv. Needs only
  #    python3-venv and cabextract from the distribution (Debian: section "main", no contrib required).
  mkdir -p "$DIR/tools/bin"
  if ! python3 -m venv "$DIR/tools/venv" >/dev/null 2>&1 && command -v apt-get >/dev/null 2>&1; then
    rm -rf "$DIR/tools/venv"
    say "Installing python3-venv and cabextract with apt (needs sudo)"
    $SUDO apt-get install -y -qq python3-venv cabextract || warn "apt-get install python3-venv cabextract failed"
  fi
  if [ -x "$DIR/tools/venv/bin/pip" ] || python3 -m venv "$DIR/tools/venv"; then
    say "Installing protontricks into $DIR/tools (python venv) and winetricks from GitHub"
    if "$DIR/tools/venv/bin/pip" install -q --disable-pip-version-check protontricks \
       && curl -fsSL --max-time 120 -o "$DIR/tools/bin/winetricks" https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks; then
      chmod +x "$DIR/tools/bin/winetricks"
      write_script "$DIR/tools/bin/protontricks" <<SH
#!/usr/bin/env bash
export PATH="$DIR/tools/bin:\$PATH"
exec "$DIR/tools/venv/bin/protontricks" "\$@"
SH
      if ! command -v cabextract >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then
          say "Installing cabextract with apt (needs sudo)"
          $SUDO apt-get install -y -qq cabextract || warn "apt-get install cabextract failed; winetricks needs it for some installers"
        else
          warn "winetricks also wants 'cabextract'; install it with your package manager"
        fi
      fi
      return 0
    fi
    warn "python venv installation failed"
  fi
  # b) distribution package (Debian keeps it in "contrib", Ubuntu in "universe")
  if command -v apt-get >/dev/null 2>&1; then
    cand="$(apt_candidate protontricks)"
    if { [ -z "$cand" ] || [ "$cand" = "(none)" ]; } && is_debian; then
      say "protontricks lives in Debian's 'contrib' section, which is not enabled in your APT sources"
      if ask "Enable 'contrib' for the Debian repositories in /etc/apt now (backups are kept, needs sudo)?"; then
        if enable_debian_contrib; then $SUDO apt-get update -qq || warn "apt-get update reported errors"; else warn "no APT source found where contrib could be enabled (see the [apt] lines above)"; fi
        cand="$(apt_candidate protontricks)"
      fi
    fi
    if [ -n "$cand" ] && [ "$cand" != "(none)" ]; then
      say "Installing protontricks $cand with apt (needs sudo)"
      $SUDO apt-get install -y -qq protontricks && return 0
      warn "apt-get install protontricks failed"
    fi
  fi
  # c) Flatpak (user scope, no sudo)
  if command -v flatpak >/dev/null 2>&1; then
    say "Installing protontricks as a Flatpak (user scope)"
    flatpak --user remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo \
      && flatpak --user install -y flathub com.github.Matoking.protontricks && return 0
    warn "flatpak installation failed"
  fi
  return 1
}

prefix_processes() { # "pid command" of wine processes that run inside the GTA V prefix (game, Rockstar Launcher, leftovers)
  local p pid
  for p in /proc/[0-9]*; do
    pid="${p#/proc/}"; [ "$pid" != "$$" ] || continue
    if { tr '\0' '\n' < "$p/environ"; } 2>/dev/null | grep -q -x -F -e "WINEPREFIX=${prefix%/}" -e "WINEPREFIX=${prefix%/}/"; then
      echo "$pid $({ tr '\0' ' ' < "$p/cmdline"; } 2>/dev/null | cut -c1-100)"
    fi
  done
}

GE_PROTON_TAG="${GTAN_GE_PROTON:-GE-Proton8-32}"
GE_PROTON_URL="${GTAN_GE_PROTON_URL:-https://github.com/GloriousEggroll/proton-ge-custom/releases/download/$GE_PROTON_TAG/$GE_PROTON_TAG.tar.gz}"

find_fallback_proton() { # a stable Proton (official or GE) in the Steam libraries / compatibilitytools.d, by preference
  local base name all="" pat
  for base in ${steam_libs[@]+"${steam_libs[@]/%//steamapps/common}"} "$steam_root/compatibilitytools.d" \
              "$HOME/.steam/root/compatibilitytools.d" "$HOME/.local/share/Steam/compatibilitytools.d"; do
    [ -d "$base" ] || continue
    for name in "$base"/*/; do
      name="$(basename "$name")"
      case "$name" in "Proton 8.0"|GE-Proton8-*|"Proton 9.0"|GE-Proton9-*|"Proton 10.0") all="$all$name"$'\n' ;; esac
    done
  done
  for pat in '^Proton 8\.0$' '^GE-Proton8-' '^Proton 9\.0$' '^GE-Proton9-' '^Proton 10\.0$'; do
    name="$(printf '%s' "$all" | grep -E "$pat" | sort -V | tail -1 || true)"
    [ -n "$name" ] && { echo "$name"; return 0; }
  done
  return 1
}

install_ge_proton() { # downloads a community Proton 8 build into Steam's compatibilitytools.d (no Steam login needed)
  local dest tgz
  if [ -z "$steam_root" ] || [ ! -d "$steam_root" ]; then warn "Steam root not found, cannot install $GE_PROTON_TAG"; return 1; fi
  dest="$steam_root/compatibilitytools.d"; mkdir -p "$dest"
  [ -x "$dest/$GE_PROTON_TAG/proton" ] && return 0
  tgz="$DIR/downloads/$GE_PROTON_TAG.tar.gz"
  say "Downloading $GE_PROTON_TAG (about 450 MB) into $dest"
  curl -fL --retry 3 --progress-bar -o "$tgz" "$GE_PROTON_URL" || { warn "download failed: $GE_PROTON_URL"; rm -f "$tgz"; return 1; }
  if curl -fsSL --max-time 60 -o "$tgz.sha512sum" "${GE_PROTON_URL%.tar.gz}.sha512sum" 2>/dev/null; then
    if ! (cd "$DIR/downloads" && sha512sum -c --quiet "$GE_PROTON_TAG.tar.gz.sha512sum"); then
      warn "checksum mismatch for $tgz"; rm -f "$tgz" "$tgz.sha512sum"; return 1
    fi
  fi
  say "Unpacking $GE_PROTON_TAG"
  tar -xzf "$tgz" -C "$dest" || { warn "could not unpack $tgz"; return 1; }
  rm -f "$tgz" "$tgz.sha512sum"
  [ -x "$dest/$GE_PROTON_TAG/proton" ] || { warn "$dest/$GE_PROTON_TAG/proton is missing after unpacking"; return 1; }
  ok "$GE_PROTON_TAG installed into $dest (Steam also lists it under Compatibility after a restart)"
}

used_fallback=""
pt_extra=""       # "--no-runtime" once protontricks had to run a Proton without its Steam Runtime container
pt_run() { # protontricks, through the fallback Proton when one is in use
  if [ -n "$used_fallback" ]; then PROTON_VERSION="$used_fallback" $pt $pt_extra "$@"; else $pt $pt_extra "$@"; fi
}

pt_log="$DIR/logs/protontricks.log"
dotnet_attempt() { # dotnet_attempt [protontricks options]; honours PROTON_VERSION; output also goes to logs/protontricks.log
  say "Running: ${PROTON_VERSION:+PROTON_VERSION=\"$PROTON_VERSION\" }$pt $pt_extra $* 271590 -q dotnet48"
  # warn+msi makes wine name the file it could not write ("failed to create ... (error N)"), which is otherwise silent
  if WINEDEBUG="${WINEDEBUG:-warn+msi}" $pt $pt_extra "$@" 271590 -q dotnet48 2>&1 | tee -a "$pt_log"; then return 0; fi
  if grep -q 'failed to create' "$pt_log"; then
    warn "the installer could not write these files:"
    grep -o 'failed to create L"[^"]*" (error [0-9]*)' "$pt_log" | sort -u | sed 's/^/       /'
  fi
  return 1
}

dotnet_attempt_rt() { # like dotnet_attempt, but falls back to --no-runtime when protontricks lacks the Steam Runtime for that Proton
  dotnet_attempt "$@" && return 0
  if [ -z "$pt_extra" ] && grep -q 'missing the required Steam Runtime' "$pt_log"; then
    warn "protontricks has no Steam Runtime container for ${PROTON_VERSION:-the Proton of the game} (Steam would install it"
    warn "the first time a game runs with that Proton). Retrying without the container: --no-runtime"
    : > "$pt_log"
    pt_extra="--no-runtime"
    dotnet_attempt "$@" && return 0
    grep -q 'Executing w_do_call' "$pt_log" || pt_extra=""   # keep --no-runtime when winetricks itself did run
  fi
  return 1
}

# Proton fills the prefix with symlinks to its own builtin DLLs (system32, syswow64 and the .NET support files
# in Microsoft.NET/Framework*/v4.0.30319). The .NET installers must overwrite some of them, which fails with
# "error 5" through such a link, so the affected links are replaced by real copies (or removed) beforehand.
materialize_link() { # materialize_link <path>: turn a symlink into a regular file with the same content
  local f="$1" tmp
  [ -L "$f" ] || return 0
  tmp="$f.gtan.$$"
  if cp -L "$f" "$tmp" 2>/dev/null && mv -f "$tmp" "$f"; then return 0; fi
  rm -f "$tmp"; rm -f "$f"       # dangling link: the installer creates the file anew
}

fix_proton_symlinks() { # every symlink in system32, syswow64 and Microsoft.NET becomes a real file (some hundred MB, once)
  local n=0 f d before after
  before="$(du -sm "$prefix/drive_c/windows" 2>/dev/null | cut -f1)"
  for d in Microsoft.NET system32 syswow64; do
    while IFS= read -r f; do
      [ -d "$f" ] && continue          # links to directories (gecko, mono) stay as they are
      materialize_link "$f" && n=$((n+1))
    done < <(find "$prefix/drive_c/windows/$d" -type l 2>/dev/null)
  done
  after="$(du -sm "$prefix/drive_c/windows" 2>/dev/null | cut -f1)"
  [ "$n" -gt 0 ] && info "Replaced $n Proton symlinks in the prefix with real files (+$(( ${after:-0} - ${before:-0} )) MB) so that the .NET installers can overwrite them"
  return 0
}

win_to_unix() { # win_to_unix 'C:\windows\...' -> unix path inside the prefix, resolved case-insensitively
  python3 - "$prefix" "$1" <<'PY'
import os, sys
prefix, win = sys.argv[1], sys.argv[2]
if not win[:2].lower() == "c:":
    sys.exit(1)
cur = os.path.join(prefix, "drive_c")
for part in [p for p in win[2:].replace("\\", "/").split("/") if p]:
    if os.path.lexists(os.path.join(cur, part)):
        cur = os.path.join(cur, part); continue
    try:
        match = next(e for e in os.listdir(cur) if e.lower() == part.lower())
    except (StopIteration, OSError):
        sys.exit(1)
    cur = os.path.join(cur, match)
print(cur)
PY
}

fix_symlinks_from_log() { # replace every symlink the last attempt could not overwrite ("failed to create ... (error 5)"); 0 = something fixed
  local w p n=0
  while IFS= read -r w; do
    p="$(win_to_unix "$w" 2>/dev/null)" || continue
    if [ -L "$p" ]; then materialize_link "$p" && n=$((n+1)) && info "  replaced symlink $p"; fi
  done < <(grep -o 'failed to create L"[^"]*" (error 5)' "$pt_log" 2>/dev/null | sed -E 's/^failed to create L"//; s/" \(error 5\)$//; s/\\\\/\\/g' | sort -u)
  [ "$n" -gt 0 ]
}

dotnet_attempt_fix() { # dotnet_attempt_rt, retried after replacing symlinks the installer stumbled over (at most 3 rounds)
  local round
  for round in 1 2 3; do
    dotnet_attempt_rt "$@" && return 0
    fix_symlinks_from_log || return 1
    warn "retrying the .NET install after replacing Proton symlinks (round $round)"
    : > "$pt_log"
  done
  return 1
}

run_protontricks() {
  local rc=0
  used_fallback=""
  run_protontricks_inner || rc=$?
  # winetricks switches the prefix to "Windows XP" while installing .NET 4.0 and leaves it at Windows 7;
  # GTA V and the Rockstar Launcher want Proton's default, Windows 10
  say "Setting the Windows version of the prefix back to 10:  $pt 271590 -q win10"
  pt_run 271590 -q win10 2>&1 | tail -2 || warn "could not set win10; run by hand:  $pt 271590 -q win10"
  return $rc
}

run_protontricks_inner() {
  local busy avail fallback=""
  busy="$(prefix_processes)"
  if [ -n "$busy" ]; then
    warn "Wine processes are still running inside the GTA V prefix (the game, the Rockstar Launcher or leftovers):"
    echo "$busy" | sed 's/^/       /'
    warn "winetricks would wait for them forever. Stop them in Steam (the green Stop button) first,"
    if ask "or shall I stop them now?"; then
      echo "$busy" | awk '{print $1}' | xargs -r kill 2>/dev/null || true; sleep 3
      echo "$busy" | awk '{print $1}' | xargs -r kill -9 2>/dev/null || true
    fi
  fi
  avail="$(df -Pk "$prefix" 2>/dev/null | awk 'NR==2{print int($4/1024)}')"
  if [ -n "$avail" ] && [ "$avail" -lt 3000 ]; then
    warn "Only $avail MB free on the drive that holds the prefix; the .NET installers need about 3 GB"
  fi
  fix_proton_symlinks
  say "Installing .NET Framework 4.8 into the GTA V prefix (log: $pt_log)"
  say "This takes 5-15 minutes and prints a lot: 'may not fully work on a 64-bit installation', Fontconfig errors"
  say "and 'This will hang until all wine processes ... terminate' are normal winetricks noise. The .NET 4.0 and"
  say "4.8 installers then run silently (look for dotNetFx40 / ndp48 processes in top). Do not close popup windows."
  if [ -n "$DOTNET_PROTON" ] && [ "$DOTNET_PROTON" != "auto" ]; then
    fallback="$DOTNET_PROTON"
    if [ "$fallback" = "$GE_PROTON_TAG" ] && ! find_fallback_proton | grep -q -x -F "$fallback"; then
      install_ge_proton || { warn "could not install $fallback"; return 1; }
    fi
    say "Using $fallback for the .NET install (--dotnet-proton)"
  fi
  if [ -n "$fallback" ]; then
    : > "$pt_log"
    PROTON_VERSION="$fallback" dotnet_attempt_fix || { warn "dotnet48 failed with $fallback, see $pt_log"; return 1; }
    used_fallback="$fallback"; DOTNET_PROTON="$fallback"
  elif ! { : > "$pt_log"; dotnet_attempt_fix; }; then
    if grep -q -E 'Failed to extract cabinet|FDICopy failed' "$pt_log"; then
      warn "The .NET 4.0 installer could not extract its cabinets. That happens on very new wine builds"
      warn "(Proton Experimental); a stable Proton installs it fine, and the game can keep its own Proton."
      fallback="$(find_fallback_proton || true)"
      if [ -z "$fallback" ]; then
        warn "No stable Proton is installed in Steam."
        if ask "Download $GE_PROTON_TAG (about 450 MB, a community build of Proton 8) into Steam's compatibilitytools.d and install .NET with it?"; then
          install_ge_proton && fallback="$GE_PROTON_TAG"
        fi
      fi
      if [ -z "$fallback" ]; then
        warn "Install 'Proton 8.0' in Steam (Library > Tools, or: xdg-open steam://install/2348590) and run $DIR/update.sh again;"
        warn "the .NET step then retries with it by itself."
        return 1
      fi
      warn "Retrying with $fallback"
      : > "$pt_log"
      if ! PROTON_VERSION="$fallback" dotnet_attempt_fix; then
        warn "dotnet48 failed with $fallback too, see $pt_log. Retry by hand:  PROTON_VERSION=\"$fallback\" $pt 271590 -q dotnet48"
        return 1
      fi
      used_fallback="$fallback"; DOTNET_PROTON="$fallback"   # remembered in setup.conf for the next time
    elif ! grep -q 'Executing w_do_call' "$pt_log"; then
      warn "protontricks did not reach winetricks (Steam Runtime container problem?); retrying outside the container"
      : > "$pt_log"
      if ! dotnet_attempt --no-bwrap; then
        warn "dotnet48 failed, see $pt_log. Retry later:  $pt --no-bwrap 271590 -q dotnet48"
        return 1
      fi
    else
      warn "dotnet48 failed, see $pt_log. Retry later:  $pt 271590 -q dotnet48"
      return 1
    fi
  fi
  say "Installing the VC++ 2022 runtime:  ${used_fallback:+PROTON_VERSION=\"$used_fallback\" }$pt 271590 -q vcrun2022"
  pt_run 271590 -q vcrun2022 2>&1 | tee -a "$pt_log" \
    || warn "vcrun2022 failed (not fatal, wine's built-in runtime is used). Retry later:  ${used_fallback:+PROTON_VERSION=\"$used_fallback\" }$pt 271590 -q vcrun2022"
  if dotnet_present; then ok ".NET Framework 4.x is now present in the prefix"; else warn ".NET Framework still not detected in $prefix"; fi
}

if [ "$RUN_PROTONTRICKS" -eq 1 ]; then
  if [ -z "$prefix" ] || [ ! -d "$prefix" ]; then
    warn "No Proton prefix for GTA V found yet. Start the game once through Steam, then run $DIR/update.sh"
  elif dotnet_present; then
    ok ".NET Framework 4.x already present in the prefix"
  else
    find_protontricks
    if [ -z "$pt" ] && ask "protontricks is needed to put .NET Framework 4.8 into the GTA V prefix. Install protontricks now?"; then
      install_protontricks || true
      find_protontricks
    fi
    if [ -z "$pt" ]; then
      warn "protontricks is not available. Install it and run:  protontricks 271590 -q dotnet48 vcrun2022"
      warn "  Debian:  enable 'contrib' in /etc/apt/sources.list, then: sudo apt update && sudo apt install protontricks"
      warn "  Ubuntu:  sudo apt install protontricks    Arch: pacman -S protontricks    any: pipx install protontricks"
      warn "  Flatpak: flatpak install flathub com.github.Matoking.protontricks"
    elif ask "Install .NET Framework 4.8 + VC++ 2022 runtime into the GTA V prefix now (takes several minutes)?"; then
      run_protontricks || true
    else
      warn "Skipped. Run later:  $pt 271590 -q dotnet48 vcrun2022"
    fi
  fi
fi

# ---------------------------------------------------------------------------------------------------
# 5. Helper scripts, desktop entry, saved options, copy of this script for update.sh
# ---------------------------------------------------------------------------------------------------
write_script "$DIR/update.sh" <<SH
#!/usr/bin/env bash
# Checks GitHub for a newer GTA Network release and installs it. play.sh, server/start.sh and bot.sh run this first.
# Options: --quiet, --check, --release <tag|latest>, --auto-update on|off, --shv <zip>, --name <nick>, --yes
exec bash "$DIR/setup-linux.sh" --dir "$DIR" --update "\$@"
SH
write_script "$DIR/play.sh" <<SH
#!/usr/bin/env bash
# Checks for updates (GTAN_NO_UPDATE=1 skips that), deploys GTA Network into the game folder, starts GTA V
# through $METHOD and restores the game folder when the game exits.
[ "\${GTAN_NO_UPDATE:-0}" = 1 ] || "$DIR/update.sh" --quiet || echo "update check failed, starting anyway"
exec "$DIR/GTANetwork.Launcher" "\$@"
SH
write_script "$DIR/server/start.sh" <<SH
#!/usr/bin/env bash
# Checks for updates (GTAN_NO_UPDATE=1 skips that), then starts the dedicated server. settings.xml and
# resources/ live next to this script. UDP+TCP 4499.
[ "\${GTAN_NO_UPDATE:-0}" = 1 ] || "$DIR/update.sh" --quiet || echo "update check failed, starting anyway"
cd "$DIR/server" && exec "$DIR/server/GTANetworkServer" "\$@"
SH
write_script "$DIR/bot.sh" <<SH
#!/usr/bin/env bash
# Headless test client: joins the local server and lets you type chat / commands (no game needed).
[ "\${GTAN_NO_UPDATE:-0}" = 1 ] || "$DIR/update.sh" --quiet || echo "update check failed, starting anyway"
exec "$DIR/GTANetwork.Bot" --host 127.0.0.1 --port 4499 --name "${NAME}-bot" --interactive "\$@"
SH

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

save_conf

save_self() { # keep a copy of this script next to the installation for update.sh
  local src="${BASH_SOURCE[0]:-}" url tmp="$DIR/setup-linux.sh.tmp"
  if [ -n "${GTAN_SETUP_URL:-}" ]; then
    curl -fsSL --max-time 60 -o "$tmp" "$GTAN_SETUP_URL" || { rm -f "$tmp"; warn "could not download $GTAN_SETUP_URL"; return 0; }
  elif [ -n "$src" ] && [ -f "$src" ]; then
    cp -f "$src" "$tmp"
  else                                   # piped through bash: take the copy shipped with the release
    url="$(asset_url "setup-linux.sh")"
    [ -n "$url" ] || { warn "release $RELEASE_TAG ships no setup-linux.sh; update.sh will not work until you run the installer from a file"; return 0; }
    curl -fsSL --max-time 60 -o "$tmp" "$url" || { rm -f "$tmp"; warn "could not download $url"; return 0; }
  fi
  grep -q -- '--update' "$tmp" || warn "the setup script shipped with $RELEASE_TAG has no update mode; auto-update needs a newer release"
  chmod +x "$tmp" && mv -f "$tmp" "$DIR/setup-linux.sh"
}
save_self
if [ "$BUILD" -eq 1 ]; then echo "build" > "$DIR/release.txt"; else echo "$RELEASE_TAG" > "$DIR/release.txt"; fi
ok "Created play.sh, server/start.sh, bot.sh, update.sh and a desktop entry"

# ---------------------------------------------------------------------------------------------------
# 6. Summary
# ---------------------------------------------------------------------------------------------------
if [ "$MODE" = "update" ]; then
  say "GTA Network updated to $RELEASE_TAG"
  exit 0
fi
echo
say "Done. Next steps:"
echo "  1. Start a server:            $DIR/server/start.sh"
echo "  2. (optional, no game) test:  $DIR/bot.sh        then type /help"
echo "  3. Play:                      $DIR/play.sh       (or the GTA Network desktop entry)"
echo "     In the game menu open Favorites and pick $SERVER_ADDR."
if [ "$METHOD" = "steam" ]; then
  echo "  Steam launch options for GTA V must be:  WINEDLLOVERRIDES=\"dinput8=n,b\" %command%"
fi
if [ "$AUTO_UPDATE" -eq 1 ]; then
  echo "  Updates:                      checked automatically before each start; $DIR/update.sh runs a check by hand"
else
  echo "  Updates:                      $DIR/update.sh (automatic checks are off)"
fi
echo "  Check anytime:                $DIR/GTANetwork.Launcher doctor"
echo "  Logs:                         $DIR/logs/  (launcher.log, ScriptHookVDotNet-*.log, client logs)"
[ -n "$game_dir" ] && echo "  Game folder:                  $game_dir"
exit 0
