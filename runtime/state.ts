// The mirror of the players' state the engine pushes at 10 Hz as deltas ("state" frames): scripts read it synchronously.
export interface Vector3 { x: number; y: number; z: number; }
export interface PlayerState {
  handle: number; name: string; position: Vector3; rotation: Vector3; health: number; armor: number; dimension: number;
  vehicle: number; seat: number; model: number; dead: boolean;
}

export const players = new Map<number, PlayerState>();

export function applyState(payload: any) {
  const rows = (payload?.p ?? []) as any[];
  for (const row of rows) {
    const h = row.h as number;
    let p = players.get(h);
    if (!p) { p = { handle: h, name: "", position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0 }, health: 0, armor: 0, dimension: 0, vehicle: 0, seat: -1, model: 0, dead: false }; players.set(h, p); }
    if ("n" in row) p.name = row.n;
    if ("p" in row) { p.position.x = row.p[0]; p.position.y = row.p[1]; p.position.z = row.p[2]; }
    if ("r" in row) { p.rotation.x = row.r[0]; p.rotation.y = row.r[1]; p.rotation.z = row.r[2]; }
    if ("hp" in row) p.health = row.hp;
    if ("ar" in row) p.armor = row.ar;
    if ("dim" in row) p.dimension = row.dim;
    if ("veh" in row) p.vehicle = row.veh;
    if ("seat" in row) p.seat = row.seat;
    if ("model" in row) p.model = row.model;
    if ("dead" in row) p.dead = row.dead;
  }
  for (const h of (payload?.gone ?? []) as number[]) players.delete(h);
}
