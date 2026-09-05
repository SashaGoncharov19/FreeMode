// A small MessagePack codec (nil, bool, int, float, str, bin, array, map) compatible with MessagePack-CSharp on the engine
// side. No dependency, so the runtime folder ships as plain files and needs no install step.

export function encode(value: unknown): Uint8Array {
  const w = new Writer();
  w.any(value);
  return w.bytes();
}

export function decode(buf: Uint8Array): unknown {
  const r = new Reader(buf);
  return r.any();
}

class Writer {
  private buf = new Uint8Array(1024);
  private view = new DataView(this.buf.buffer);
  private pos = 0;

  bytes(): Uint8Array { return this.buf.subarray(0, this.pos); }

  private ensure(n: number) {
    if (this.pos + n <= this.buf.length) return;
    let size = this.buf.length * 2;
    while (size < this.pos + n) size *= 2;
    const bigger = new Uint8Array(size);
    bigger.set(this.buf.subarray(0, this.pos));
    this.buf = bigger;
    this.view = new DataView(bigger.buffer);
  }
  private u8(v: number) { this.ensure(1); this.buf[this.pos++] = v; }
  private u16(v: number) { this.ensure(2); this.view.setUint16(this.pos, v); this.pos += 2; }
  private u32(v: number) { this.ensure(4); this.view.setUint32(this.pos, v); this.pos += 4; }

  any(v: unknown) {
    if (v === null || v === undefined) { this.u8(0xc0); return; }
    switch (typeof v) {
      case "boolean": this.u8(v ? 0xc3 : 0xc2); return;
      case "number": this.number(v); return;
      case "string": this.string(v); return;
      case "bigint": this.ensure(9); this.buf[this.pos++] = v < 0 ? 0xd3 : 0xcf; (v < 0 ? this.view.setBigInt64 : this.view.setBigUint64).call(this.view, this.pos, v); this.pos += 8; return;
      case "object":
        if (v instanceof Uint8Array) { this.bin(v); return; }
        if (Array.isArray(v)) { this.arrayHeader(v.length); for (const item of v) this.any(item); return; }
        if (v instanceof Map) { this.mapHeader(v.size); for (const [k, val] of v) { this.any(k); this.any(val); } return; }
        { const keys = Object.keys(v as object); this.mapHeader(keys.length); for (const k of keys) { this.string(k); this.any((v as Record<string, unknown>)[k]); } return; }
      default: this.u8(0xc0);
    }
  }
  number(v: number) {
    if (Number.isInteger(v) && Math.abs(v) <= 0x7fffffff) {
      if (v >= 0) {
        if (v < 128) this.u8(v);
        else if (v < 256) { this.u8(0xcc); this.u8(v); }
        else if (v < 65536) { this.u8(0xcd); this.u16(v); }
        else { this.u8(0xce); this.u32(v); }
      } else {
        if (v >= -32) this.u8(0x100 + v);
        else if (v >= -128) { this.u8(0xd0); this.u8(v & 0xff); }
        else if (v >= -32768) { this.u8(0xd1); this.u16(v & 0xffff); }
        else { this.u8(0xd2); this.u32(v >>> 0); }
      }
      return;
    }
    this.ensure(9); this.buf[this.pos++] = 0xcb; this.view.setFloat64(this.pos, v); this.pos += 8;
  }
  string(s: string) {
    const bytes = new TextEncoder().encode(s);
    if (bytes.length < 32) this.u8(0xa0 | bytes.length);
    else if (bytes.length < 256) { this.u8(0xd9); this.u8(bytes.length); }
    else if (bytes.length < 65536) { this.u8(0xda); this.u16(bytes.length); }
    else { this.u8(0xdb); this.u32(bytes.length); }
    this.ensure(bytes.length); this.buf.set(bytes, this.pos); this.pos += bytes.length;
  }
  bin(b: Uint8Array) {
    if (b.length < 256) { this.u8(0xc4); this.u8(b.length); } else if (b.length < 65536) { this.u8(0xc5); this.u16(b.length); } else { this.u8(0xc6); this.u32(b.length); }
    this.ensure(b.length); this.buf.set(b, this.pos); this.pos += b.length;
  }
  arrayHeader(n: number) { if (n < 16) this.u8(0x90 | n); else if (n < 65536) { this.u8(0xdc); this.u16(n); } else { this.u8(0xdd); this.u32(n); } }
  mapHeader(n: number) { if (n < 16) this.u8(0x80 | n); else if (n < 65536) { this.u8(0xde); this.u16(n); } else { this.u8(0xdf); this.u32(n); } }
}

class Reader {
  private view: DataView;
  private pos = 0;
  private static decoder = new TextDecoder();
  constructor(private buf: Uint8Array) { this.view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength); }

  any(): unknown {
    const c = this.buf[this.pos++];
    if (c <= 0x7f) return c;
    if (c >= 0xe0) return c - 0x100;
    if ((c & 0xf0) === 0x80) return this.map(c & 0x0f);
    if ((c & 0xf0) === 0x90) return this.array(c & 0x0f);
    if ((c & 0xe0) === 0xa0) return this.str(c & 0x1f);
    switch (c) {
      case 0xc0: return null;
      case 0xc2: return false;
      case 0xc3: return true;
      case 0xc4: return this.binOf(this.buf[this.pos++]);
      case 0xc5: return this.binOf(this.u16());
      case 0xc6: return this.binOf(this.u32());
      case 0xca: { const v = this.view.getFloat32(this.pos); this.pos += 4; return v; }
      case 0xcb: { const v = this.view.getFloat64(this.pos); this.pos += 8; return v; }
      case 0xcc: return this.buf[this.pos++];
      case 0xcd: return this.u16();
      case 0xce: return this.u32();
      case 0xcf: { const v = this.view.getBigUint64(this.pos); this.pos += 8; return v <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(v) : v; }
      case 0xd0: { const v = this.view.getInt8(this.pos); this.pos += 1; return v; }
      case 0xd1: { const v = this.view.getInt16(this.pos); this.pos += 2; return v; }
      case 0xd2: { const v = this.view.getInt32(this.pos); this.pos += 4; return v; }
      case 0xd3: { const v = this.view.getBigInt64(this.pos); this.pos += 8; return v >= BigInt(Number.MIN_SAFE_INTEGER) && v <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(v) : v; }
      case 0xd9: return this.str(this.buf[this.pos++]);
      case 0xda: return this.str(this.u16());
      case 0xdb: return this.str(this.u32());
      case 0xdc: return this.array(this.u16());
      case 0xdd: return this.array(this.u32());
      case 0xde: return this.map(this.u16());
      case 0xdf: return this.map(this.u32());
      default: throw new Error("msgpack: unsupported type byte 0x" + c.toString(16));
    }
  }
  private u16() { const v = this.view.getUint16(this.pos); this.pos += 2; return v; }
  private u32() { const v = this.view.getUint32(this.pos); this.pos += 4; return v; }
  private str(n: number) { const s = Reader.decoder.decode(this.buf.subarray(this.pos, this.pos + n)); this.pos += n; return s; }
  private binOf(n: number) { const b = this.buf.slice(this.pos, this.pos + n); this.pos += n; return b; }
  private array(n: number) { const a = new Array(n); for (let i = 0; i < n; i++) a[i] = this.any(); return a; }
  private map(n: number) { const o: Record<string, unknown> = {}; for (let i = 0; i < n; i++) { const k = this.any(); o[String(k)] = this.any(); } return o; }
}
