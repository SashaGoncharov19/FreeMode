// The runtime side of the engine bridge: framing (u32 LE length + msgpack [type, id, name, payload]), batching (1 ms or
// 64 KB; frames that wait for an answer flush at the end of the microtask), the pending-result table.
import { encode, decode } from "./msgpack";

export const FrameType = { Call: 1, Result: 2, Event: 3, State: 4, Log: 5, Load: 6, Unload: 7 } as const;
export type Frame = { type: number; id: number | null; name: string | null; payload: unknown };

export class Bridge {
  private socket: any = null;
  private chunks: Uint8Array[] = [];
  private chunkBytes = 0;
  private flushQueued = false;
  private nextId = 1;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private inbuf = new Uint8Array(1 << 20);
  private inFilled = 0;
  onFrame: (f: Frame) => void = () => {};
  onClose: () => void = () => {};

  async connect(target: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("connect timeout to " + target)), 10000);
      const handlers = {
        data: (_s: any, d: Uint8Array) => this.onData(d),
        open: (s: any) => { clearTimeout(timer); this.socket = s; resolve(); },
        error: (_s: any, e: Error) => { clearTimeout(timer); reject(e); },
        connectError: (_s: any, e: Error) => { clearTimeout(timer); reject(e); },
        close: () => { this.socket = null; for (const p of this.pending.values()) p.reject(new Error("engine gone")); this.pending.clear(); this.onClose(); },
        drain: () => {},
      };
      if (target.startsWith("unix:")) Bun.connect({ unix: target.slice(5), socket: handlers });
      else { const [host, port] = target.slice(4).split(":").length === 2 ? target.slice(4).split(":") : ["127.0.0.1", target.slice(4)]; Bun.connect({ hostname: host, port: Number(port), socket: handlers }); }
    });
    setInterval(() => this.flush(), 1);
  }

  send(type: number, id: number | null, name: string | null, payload: unknown, immediate = false) {
    const body = encode([type, id, name, payload]);
    const buf = new Uint8Array(4 + body.length);
    new DataView(buf.buffer).setUint32(0, body.length, true);
    buf.set(body, 4);
    this.chunks.push(buf);
    this.chunkBytes += buf.length;
    if (this.chunkBytes >= 65536) this.flush();
    else if (immediate && !this.flushQueued) { this.flushQueued = true; queueMicrotask(() => { this.flushQueued = false; this.flush(); }); }
  }

  /** A call to the engine; resolves with the result (or rejects with the engine's error) when wantResult, else resolves at once. */
  call(name: string, args: unknown[], wantResult: boolean): Promise<unknown> {
    if (!wantResult) { this.send(FrameType.Call, null, name, args); return Promise.resolve(undefined); }
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.send(FrameType.Call, id, name, args, true);
    });
  }

  result(id: number, value: unknown) { this.send(FrameType.Result, id, null, value, true); }
  log(resource: string, level: string, text: string) { this.send(FrameType.Log, null, resource, [level, text], true); }

  flush() {
    if (!this.socket || this.chunks.length === 0) return;
    let out: Uint8Array;
    if (this.chunks.length === 1) out = this.chunks[0];
    else { out = new Uint8Array(this.chunkBytes); let o = 0; for (const c of this.chunks) { out.set(c, o); o += c.length; } }
    this.chunks = []; this.chunkBytes = 0;
    this.socket.write(out);
    this.socket.flush?.();
  }

  private onData(data: Uint8Array) {
    if (this.inFilled + data.length > this.inbuf.length) {
      const bigger = new Uint8Array(Math.max(this.inbuf.length * 2, this.inFilled + data.length));
      bigger.set(this.inbuf.subarray(0, this.inFilled));
      this.inbuf = bigger;
    }
    this.inbuf.set(data, this.inFilled); this.inFilled += data.length;
    let off = 0;
    const view = new DataView(this.inbuf.buffer);
    while (this.inFilled - off >= 4) {
      const len = view.getUint32(off, true);
      if (this.inFilled - off - 4 < len) break;
      let frame: unknown[];
      try { frame = decode(this.inbuf.subarray(off + 4, off + 4 + len)) as unknown[]; }
      catch (e) { console.error("bridge: bad frame from the engine: " + (e as Error).message); off += 4 + len; continue; }
      off += 4 + len;
      const f: Frame = { type: frame[0] as number, id: (frame[1] as number | null) ?? null, name: (frame[2] as string | null) ?? null, payload: frame[3] };
      if (f.type === FrameType.Result && f.id != null) {
        const p = this.pending.get(f.id);
        if (p) { this.pending.delete(f.id); const v = f.payload as any; if (v && typeof v === "object" && "error" in v) p.reject(new Error(String(v.error))); else p.resolve(v); }
        continue;
      }
      try { this.onFrame(f); } catch (e) { console.error("bridge: frame handler failed: " + (e as Error).stack); }
    }
    if (off > 0) { this.inbuf.copyWithin(0, off, this.inFilled); this.inFilled -= off; }
  }
}
