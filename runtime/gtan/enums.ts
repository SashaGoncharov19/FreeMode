// Helpers over the generated enum tables (enums.generated.ts): a chat argument like "adder" or "0xB779A091" becomes the value,
// a value becomes its canonical name ("Adder") for messages.
import type { EnumTable } from "./enums.generated";

const nameIndex = new WeakMap<EnumTable, Map<string, string>>();

function index(table: EnumTable): Map<string, string> {
  let map = nameIndex.get(table);
  if (!map) {
    map = new Map();
    for (const key of Object.keys(table)) map.set(key.toLowerCase(), key);
    nameIndex.set(table, map);
  }
  return map;
}

/** The value of `text` in `table`: a member name (case-insensitive), a decimal or hex number, or a number; undefined when unknown. */
export function parseEnum(table: EnumTable, text: string | number | undefined | null): number | undefined {
  if (text === undefined || text === null) return undefined;
  if (typeof text === "number") return Number.isFinite(text) ? text : undefined;
  const trimmed = String(text).trim();
  if (trimmed === "") return undefined;
  const key = index(table).get(trimmed.toLowerCase());
  if (key !== undefined) return table[key];
  if (/^-?\d+$/.test(trimmed)) return Number(trimmed);
  if (/^0x[0-9a-f]+$/i.test(trimmed)) return Number.parseInt(trimmed, 16) | 0;
  return undefined;
}

/** The member name of `value` in `table` (the first one when several share it), or the number as text. */
export function enumName(table: EnumTable, value: number): string {
  for (const key of Object.keys(table)) if (table[key] === value) return key;
  return String(value);
}
