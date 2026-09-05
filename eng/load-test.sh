#!/usr/bin/env bash
# Load test (T-002): one server with freeroam, one bot process holding <players> connections that move and send pure/light
# sync at the client's rates; the server's /metrics.json is sampled every 5 s. Prints a summary table (markdown) and writes
# artifacts/load-<players>.json (the samples, the bot's report and the summary).
# Usage: eng/load-test.sh <players> <seconds> [server dir] [bot exe]
#   With no server dir / bot exe, both are published into artifacts/ first (or reused from LOAD_ART if set).
set -euo pipefail
players="${1:?players}"; seconds="${2:?seconds}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
port="${GTAN_TEST_PORT:-4499}"
move="${LOAD_MOVE:-1500}"
art="${LOAD_ART:-$root/artifacts/load}"
mkdir -p "$root/artifacts"

if [ -n "${3:-}" ]; then server_src="$3"; else server_src="$art/server"; fi
if [ -n "${4:-}" ]; then bot="$4"; else bot="$art/bot/GTANetwork.Bot"; fi
if [ ! -x "$server_src/GTANetworkServer" ] && [ ! -f "$server_src/GTANetworkServer.dll" ]; then
  echo "== publishing server + bot into $art =="
  dotnet publish Server/GTANetworkServer.csproj -c Release -r linux-x64 --self-contained true -o "$art/server" -v quiet
  dotnet publish Tools/GTANetwork.Bot/GTANetwork.Bot.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$art/bot" -v quiet
  cp vehicleData.json "$art/server/"
  server_src="$art/server"; bot="$art/bot/GTANetwork.Bot"
fi

work="$(mktemp -d)"
server_dir="$work/server"
cp -r "$server_src" "$server_dir"
maxplayers=$(( players + 2 )); [ "$maxplayers" -gt 1000 ] && maxplayers=1000
python3 - "$server_dir/settings.xml" "$maxplayers" "$port" <<'PY'
import sys, re, os
p, maxplayers, port = sys.argv[1], sys.argv[2], sys.argv[3]
s = open(p).read()
s = re.sub(r'<maxplayers>.*?</maxplayers>', '<maxplayers>%s</maxplayers>' % maxplayers, s)
s = re.sub(r'<serverport>.*?</serverport>', '<serverport>%s</serverport>' % port, s)
s = re.sub(r'<httpserver>.*?</httpserver>', '<httpserver>true</httpserver>', s)
s = re.sub(r'<announce>.*?</announce>', '<announce>false</announce>', s)
s = re.sub(r'<loglevel>.*?</loglevel>', '<loglevel>1</loglevel>', s)
s = re.sub(r'\s*<resource src="(tsdemo|example)" />', '', s)   # freeroam only: the gamemode every player sees
if os.environ.get("LOAD_NO_ENCRYPTION") == "1":   # plaintext sessions, to measure the cipher's share (T-009)
    s = re.sub(r'<RequireEncryption>.*?</RequireEncryption>', '<RequireEncryption>false</RequireEncryption>', s)
open(p, 'w').write(s)
PY

pushd "$server_dir" >/dev/null
if [ -x ./GTANetworkServer ]; then server_cmd=(./GTANetworkServer); else server_cmd=(dotnet GTANetworkServer.dll); fi
"${server_cmd[@]}" > load-server.log 2>&1 &
server_pid=$!
popd >/dev/null
cleanup() {
  if kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM "$server_pid" 2>/dev/null || true
    for _ in $(seq 1 20); do sleep 1; kill -0 "$server_pid" 2>/dev/null || break; done
    kill -9 "$server_pid" 2>/dev/null || true
  fi
  cp "$server_dir/load-server.log" "$root/artifacts/load-${tag:-$players}-server.log" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT
for _ in $(seq 1 60); do
  sleep 1
  grep -q "Started! Waiting for connections." "$server_dir/load-server.log" && break
  kill -0 "$server_pid" 2>/dev/null || { echo "server died:"; cat "$server_dir/load-server.log"; exit 1; }
done
grep -q "Started! Waiting for connections." "$server_dir/load-server.log" || { echo "server did not start:"; cat "$server_dir/load-server.log"; exit 1; }
curl -fsS -m 3 "http://127.0.0.1:$port/metrics.json" >/dev/null || { echo "no /metrics.json from the server"; exit 1; }

if [ "${LOAD_NO_ENCRYPTION:-0}" = "1" ]; then tag="$players-plain"; else tag="$players"; fi
report="$root/artifacts/load-$tag-bot.json"
samples="$work/samples.jsonl"; : > "$samples"
echo "== $players bots for $seconds s (move radius $move m) =="
if [[ "$bot" == *.dll ]]; then bot_cmd=(dotnet "$bot"); else bot_cmd=("$bot"); fi
bot_extra=()
if [ "${LOAD_NO_ENCRYPTION:-0}" = "1" ]; then bot_extra+=(--no-encryption); fi
if [ -n "${LOAD_THREADS:-}" ]; then bot_extra+=(--threads "$LOAD_THREADS"); fi   # pump threads for the bots (default min(4, cores))
if [ "${LOAD_VOICE:-0}" = "1" ]; then bot_extra+=(--voice); fi                         # every bot talks: 50 frames/s (T-015)
"${bot_cmd[@]}" --host 127.0.0.1 --port "$port" --bots "$players" --move "$move" --duration "$seconds" --timeout $(( seconds + 240 )) --report "$report" --name Load "${bot_extra[@]}" > "$root/artifacts/load-$tag-bot.log" 2>&1 &
bot_pid=$!

# sample the server every 5 s while the bot runs; the bot's RSS from /proc
while kill -0 "$bot_pid" 2>/dev/null; do
  sleep 5
  m=$(curl -fsS -m 3 "http://127.0.0.1:$port/metrics.json" 2>/dev/null) || continue
  bot_rss=$(awk '/VmRSS/{print $2 * 1024}' "/proc/$bot_pid/status" 2>/dev/null || echo 0)
  printf '{"t":%s,"botRssBytes":%s,"server":%s}\n' "$(date +%s)" "${bot_rss:-0}" "$m" >> "$samples"
  players_now=$(printf '%s' "$m" | python3 -c 'import json,sys; d=json.load(sys.stdin); v=d.get("voice") or {}; print(d["players"], "tick p50 %.2f p99 %.2f ms, in %.0f pps, out %.0f pps" % (d["tickMs"]["p50"], d["tickMs"]["p99"], d["in"]["pps"], d["out"]["pps"]) + (", voice in %.0f/s relays %.0f/s" % (v["framesPps"], v["relaysPps"]) if v.get("framesPps") else ""))')
  echo "   players $players_now"
done
wait "$bot_pid" && bot_exit=0 || bot_exit=$?
grep -E "^\[.*\] \[(load|result)\]" "$root/artifacts/load-$tag-bot.log" | tail -n 8

python3 - "$samples" "$report" "$root/artifacts/load-$tag.json" "$players" "$seconds" "$move" <<'PY'
import json, sys
samples_path, report_path, out_path, players, seconds, move = sys.argv[1:7]
samples = [json.loads(l) for l in open(samples_path) if l.strip()]
try: report = json.load(open(report_path))
except Exception: report = {}
# steady state: the samples with the full player count (or the last third when never full)
full = [s for s in samples if s["server"]["players"] >= int(players)]
steady = full[len(full)//3:] if len(full) >= 3 else samples[-max(1, len(samples)//3):]
def avg(f): 
    vals = [f(s) for s in steady]; return sum(vals)/len(vals) if vals else 0
def mx(f):
    vals = [f(s) for s in steady]; return max(vals) if vals else 0
n = max(1, int(avg(lambda s: s["server"]["players"])))
summary = {
    "players": int(players), "seconds": int(seconds), "moveRadius": int(move), "samples": len(samples), "steadySamples": len(steady),
    "joined": report.get("joined"), "failed": report.get("failed"), "disconnected": report.get("disconnected"),
    "tickMs": {"p50": avg(lambda s: s["server"]["tickMs"]["p50"]), "p99": avg(lambda s: s["server"]["tickMs"]["p99"]), "max": mx(lambda s: s["server"]["tickMs"]["max"])},
    "ticksPerSecond": avg(lambda s: s["server"]["ticksPerSecond"]),
    "inPerPlayer": {"pps": avg(lambda s: s["server"]["in"]["pps"]) / n, "bps": avg(lambda s: s["server"]["in"]["bps"]) / n},
    "outPerPlayer": {"pps": avg(lambda s: s["server"]["out"]["pps"]) / n, "bps": avg(lambda s: s["server"]["out"]["bps"]) / n},
    "inTotal": {"pps": avg(lambda s: s["server"]["in"]["pps"]), "bps": avg(lambda s: s["server"]["in"]["bps"])},
    "outTotal": {"pps": avg(lambda s: s["server"]["out"]["pps"]), "bps": avg(lambda s: s["server"]["out"]["bps"])},
    "gc": (steady[-1]["server"]["gc"] if steady else {}),
    "near": {"avg": avg(lambda s: s["server"]["near"]["avg"]), "max": mx(lambda s: s["server"]["near"]["max"])},
    "serverRssBytes": mx(lambda s: s["server"]["process"]["rssBytes"]), "serverCpuSeconds": (steady[-1]["server"]["process"]["cpuSeconds"] if steady else 0),
    "botRssBytes": mx(lambda s: s["botRssBytes"]), "botReport": report,
}
json.dump({"summary": summary, "samples": samples}, open(out_path, "w"), indent=1)
kb = lambda b: "%.1f" % (b / 1024)
print()
print("| players | joined | tick p50 / p99 / max ms | ticks/s | in per player pkt/s, KB/s | out per player pkt/s, KB/s | out total KB/s | GC gen0/1/2 | near avg / max | server RSS MB | bot RSS MB |")
print("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
g = summary["gc"]
label = players + (" (plaintext)" if __import__("os").environ.get("LOAD_NO_ENCRYPTION") == "1" else "")
print("| %s | %s | %.2f / %.2f / %.1f | %.0f | %.1f, %s | %.1f, %s | %s | %s/%s/%s | %.0f / %d | %.0f | %.0f |" % (
    label, summary["joined"], summary["tickMs"]["p50"], summary["tickMs"]["p99"], summary["tickMs"]["max"], summary["ticksPerSecond"],
    summary["inPerPlayer"]["pps"], kb(summary["inPerPlayer"]["bps"]), summary["outPerPlayer"]["pps"], kb(summary["outPerPlayer"]["bps"]), kb(summary["outTotal"]["bps"]),
    g.get("gen0", "?"), g.get("gen1", "?"), g.get("gen2", "?"), summary["near"]["avg"], summary["near"]["max"],
    summary["serverRssBytes"] / 1048576, summary["botRssBytes"] / 1048576))
print()
print("written:", out_path)
PY
echo "bot exit code: $bot_exit"
exit "$bot_exit"
