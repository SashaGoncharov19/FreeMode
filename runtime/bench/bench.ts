// Bun side of the bridge benchmark (T-006 stage 1). Speaks the frame protocol of Tools/GTANetwork.BridgeBench:
// u32 LE length + MessagePack array [type, id, name, payload]. Measures what docs/PLAN.md E-04 asks for:
//   A. one-way calls (no id) per second and µs per call, amortised with batching (flush every 1 ms or at 64 KB)
//   B. round trip p50/p99/max with 1, 16 and 256 calls in flight
//   C. the state mirror: N players at 10 Hz for S seconds — frames received, CPU of this process and of the engine
// Usage: bun bench.ts unix:/tmp/gtan-bridge.sock | tcp:47000 [--players 1000] [--seconds 10] [--oneway 1000000]
import { Packr, unpack } from "msgpackr";

const packr = new Packr({ useRecords: false });
const target = process.argv[2] ?? "unix:/tmp/gtan-bridge.sock";
const arg = (name: string, def: number) => { const i = process.argv.indexOf(name); return i > 0 ? Number(process.argv[i + 1]) : def; };
const players = arg("--players", 1000);
const seconds = arg("--seconds", 10);
const oneway = arg("--oneway", 1_000_000);

type Frame = [number, number | null, string | null, unknown];
const pending = new Map<number, (v: unknown) => void>();
const latencies: number[] = [];
let nextId = 1;
let stateFrames = 0;
let stateRows = 0;
const mirror = new Map<number, number[]>();

// ---- outgoing batching ----
let chunks: Uint8Array[] = [];
let chunkBytes = 0;
let socket: any = null;
function send(frame: Frame) {
  const body = packr.pack(frame);
  const buf = new Uint8Array(4 + body.length);
  new DataView(buf.buffer).setUint32(0, body.length, true);
  buf.set(body, 4);
  chunks.push(buf);
  chunkBytes += buf.length;
  if (chunkBytes >= 65536) flush();
}
function flush() {
  if (!socket || chunks.length === 0) return;
  const out = chunks.length === 1 ? chunks[0] : concat(chunks, chunkBytes);
  chunks = []; chunkBytes = 0;
  socket.write(out);
  socket.flush?.();
}
function concat(parts: Uint8Array[], total: number): Uint8Array {
  const out = new Uint8Array(total); let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}
setInterval(flush, 1);

// A call that waits for an answer is flushed at the end of the current microtask batch instead of the 1 ms timer:
// round-trip latency is then the socket's, while one-way traffic keeps the cheap batching.
let flushQueued = false;
function call(name: string, payload: unknown = null): Promise<unknown> {
  const id = nextId++;
  return new Promise((resolve) => {
    pending.set(id, resolve);
    send([1, id, name, payload]);
    if (!flushQueued) { flushQueued = true; queueMicrotask(() => { flushQueued = false; flush(); }); }
  });
}
function callOneWay(name: string, payload: unknown = null) { send([1, null, name, payload]); }

// ---- incoming framing ----
let inbuf = new Uint8Array(1 << 20);
let inFilled = 0;
function onData(data: Uint8Array) {
  if (inFilled + data.length > inbuf.length) { const bigger = new Uint8Array(Math.max(inbuf.length * 2, inFilled + data.length)); bigger.set(inbuf.subarray(0, inFilled)); inbuf = bigger; }
  inbuf.set(data, inFilled); inFilled += data.length;
  let off = 0;
  const view = new DataView(inbuf.buffer);
  while (inFilled - off >= 4) {
    const len = view.getUint32(off, true);
    if (inFilled - off - 4 < len) break;
    try { handle(unpack(inbuf.subarray(off + 4, off + 4 + len)) as Frame); }
    catch (e) { console.error("bad frame from the engine (" + len + " bytes): " + (e as Error).message); process.exit(1); }
    off += 4 + len;
  }
  if (off > 0) { inbuf.copyWithin(0, off, inFilled); inFilled -= off; }
}
function handle(f: Frame) {
  const [type, id, , payload] = f;
  if (type === 2 && id != null) { const r = pending.get(id); if (r) { pending.delete(id); r(payload); } return; }
  if (type === 4) {
    stateFrames++;
    if (payload instanceof Uint8Array) {
      // binary state: players x 15 float32 (id, x, y, z, rx, ry, rz, vx, vy, vz, health, armor, vehicle, seat, dim)
      // Float32Array needs a 4-byte aligned offset; the frame sits at an arbitrary offset of the receive buffer, so copy when unaligned
      const aligned = payload.byteOffset % 4 === 0 ? payload : payload.slice();
      const f = new Float32Array(aligned.buffer, aligned.byteOffset, aligned.byteLength / 4);
      const n = f.length / 15;
      stateRows += n;
      for (let i = 0; i < n; i++) {
        const o = i * 15;
        let row = mirror.get(f[o]);
        if (!row) { row = new Array(15); mirror.set(f[o], row); }
        for (let k = 0; k < 15; k++) row[k] = f[o + k];
      }
    } else {
      const rows = payload as number[][]; stateRows += rows.length; for (const row of rows) mirror.set(row[0], row);
    }
  }
}

function percentile(sorted: number[], p: number) { return sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(p * sorted.length))] : 0; }
const fmt = (n: number, d = 1) => n.toFixed(d);

async function roundTrips(inFlight: number, durationMs: number) {
  latencies.length = 0;
  const end = performance.now() + durationMs;
  let done = 0;
  const worker = async () => {
    while (performance.now() < end) {
      const t0 = performance.now();
      await call("ping");
      latencies.push((performance.now() - t0) * 1000);
      done++;
    }
  };
  const t0 = performance.now();
  await Promise.all(Array.from({ length: inFlight }, worker));
  const el = (performance.now() - t0) / 1000;
  const s = [...latencies].sort((a, b) => a - b);
  return { perSec: done / el, p50: percentile(s, 0.5), p99: percentile(s, 0.99), max: s[s.length - 1] ?? 0 };
}

async function main() {
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("connect timeout (is the engine listening on " + target + "?)")), 10000);
    const handlers = {
      data: (_s: any, d: Uint8Array) => onData(d),
      open: (s: any) => { clearTimeout(timer); socket = s; resolve(); },
      error: (_s: any, e: Error) => { clearTimeout(timer); reject(e); },
      connectError: (_s: any, e: Error) => { clearTimeout(timer); reject(e); },
      close: () => { if (pending.size > 0) { console.error("engine closed the connection with " + pending.size + " call(s) pending"); process.exit(1); } },
      drain: () => {},
    };
    if (target.startsWith("unix:")) Bun.connect({ unix: target.slice(5), socket: handlers });
    else Bun.connect({ hostname: "127.0.0.1", port: Number(target.slice(4)), socket: handlers });
  });
  const lines: string[] = [];
  lines.push(`transport: ${target}, bun ${Bun.version}`);

  console.error("connected; warm-up");
  // warm-up
  for (let i = 0; i < 200; i++) await call("ping");
  console.error("warm-up done");

  // A. one-way throughput
  {
    const cpu0 = process.cpuUsage();
    const t0 = performance.now();
    for (let i = 0; i < oneway; i++) callOneWay("setPos", [i, 1.5, 2.5, 3.5]);
    flush();
    const stats = (await call("stats")) as [number, number, number];
    const el = (performance.now() - t0) / 1000;
    const cpu = process.cpuUsage(cpu0);
    lines.push(`A one-way: ${oneway} calls in ${fmt(el, 2)} s = ${fmt(oneway / el / 1000, 0)}k calls/s, ${fmt((el * 1e6) / oneway, 2)} µs/call amortised; bun cpu ${fmt((cpu.user + cpu.system) / 1000 / 1000 / el * 100, 0)} % of a core; engine saw ${stats[0]} calls, ${fmt(stats[1] / 1024 / 1024, 1)} MB in`);
  }

  console.error("A done");
  // B. round trips
  for (const inFlight of [1, 16, 256]) {
    const r = await roundTrips(inFlight, 2000);
    lines.push(`B round trip, ${inFlight} in flight: ${fmt(r.perSec / 1000, 1)}k/s, p50 ${fmt(r.p50, 0)} µs, p99 ${fmt(r.p99, 0)} µs, max ${fmt(r.max, 0)} µs`);
  }

  console.error("B done");
  // C. state mirror: msgpack arrays (mode 0) and one float32 buffer per frame (mode 1)
  for (const mode of [0, 1]) {
    stateFrames = 0; stateRows = 0; mirror.clear();
    const cpu0 = process.cpuUsage();
    await call("state.start", [players, 10, mode]);
    await Bun.sleep(seconds * 1000);
    const stop = (await call("state.stop")) as [number, number, number];
    const cpu = process.cpuUsage(cpu0);
    const bunPct = (cpu.user + cpu.system) / 1000 / 1000 / seconds * 100;
    const enginePct = stop[2] / 1000 / seconds * 100;
    lines.push(`C state mirror (${mode === 0 ? "msgpack arrays" : "float32 buffer"}), ${players} players @10 Hz for ${seconds} s: ${stateFrames} frames received (engine sent ${stop[0]}), ${stateRows} rows, ${fmt(stop[1] / 1024 / 1024 / seconds, 2)} MB/s; bun cpu ${fmt(bunPct, 1)} % of a core, engine cpu ${fmt(enginePct, 1)} % of a core; mirror holds ${mirror.size} players`);
  }

  console.log(lines.join("\n"));
  socket.end();
  process.exit(0);
}
main().catch((e) => { console.error("bench failed:", e); process.exit(1); });
