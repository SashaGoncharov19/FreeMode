using System;
using GTANetworkServer.Constant;
using System.Collections.Generic;
using MessagePack;

namespace GTANetworkServer.Runtime
{
    /// <summary>
    /// Pushes the players' state to the runtime at 10 Hz as deltas, so scripts read positions and health locally instead of
    /// asking the engine. A "state" frame carries a map { p: [ per changed player: { h, and only the fields that changed } ],
    /// gone: [handles] }; the first frame after a (re)connect and a new player's first row are complete.
    /// </summary>
    internal sealed class StateMirror
    {
        private sealed class Row
        {
            public string Name;
            public float X, Y, Z, Rx, Ry, Rz;
            public int Health, Armor, Dimension, Vehicle, Seat, Model;
            public bool Dead;
        }

        private readonly Dictionary<int, Row> _last = new Dictionary<int, Row>();

        public void Reset()
        {
            _last.Clear();
        }

        public void Publish(FrameWriter writer, IList<Client> clients)
        {
            var seen = new HashSet<int>();
            var changed = new List<(int handle, Row row, Row previous)>();
            foreach (var client in clients)
            {
                Row row;
                int handle;
                try
                {
                    handle = client.handle.Value;
                    if (handle == 0 || client.Fake) continue;
                    var pos = client.position;
                    var rot = client.rotation;
                    row = new Row
                    {
                        Name = client.name,
                        X = pos?.X ?? 0, Y = pos?.Y ?? 0, Z = pos?.Z ?? 0,
                        Rx = rot?.X ?? 0, Ry = rot?.Y ?? 0, Rz = rot?.Z ?? 0,
                        Health = client.health, Armor = client.armor, Dimension = client.dimension,
                        Vehicle = client.isInVehicle ? (client.vehicle?.handle.Value ?? 0) : 0, Seat = client.isInVehicle ? client.vehicleSeat : -1,
                        Model = client.model, Dead = client.dead,
                    };
                }
                catch
                {
                    continue; // a client half-way through connecting or leaving
                }
                seen.Add(handle);
                _last.TryGetValue(handle, out var previous);
                if (previous == null || Differs(previous, row)) changed.Add((handle, row, previous));
                _last[handle] = row;
            }
            var gone = new List<int>();
            foreach (var handle in _last.Keys) if (!seen.Contains(handle)) gone.Add(handle);
            foreach (var handle in gone) _last.Remove(handle);
            if (changed.Count == 0 && gone.Count == 0) return;

            writer.Write(FrameType.State, null, null, (ref MessagePackWriter w) =>
            {
                w.WriteMapHeader(2);
                w.Write("p");
                w.WriteArrayHeader(changed.Count);
                foreach (var (handle, row, previous) in changed) WriteRow(ref w, handle, row, previous);
                w.Write("gone");
                w.WriteArrayHeader(gone.Count);
                foreach (var handle in gone) w.Write(handle);
            }, flushImmediately: false);
        }

        private static bool Differs(Row a, Row b)
        {
            return a.Name != b.Name || a.X != b.X || a.Y != b.Y || a.Z != b.Z || a.Rx != b.Rx || a.Ry != b.Ry || a.Rz != b.Rz
                   || a.Health != b.Health || a.Armor != b.Armor || a.Dimension != b.Dimension || a.Vehicle != b.Vehicle || a.Seat != b.Seat
                   || a.Model != b.Model || a.Dead != b.Dead;
        }

        private static void WriteRow(ref MessagePackWriter w, int handle, Row row, Row prev)
        {
            var full = prev == null;
            var fields = 1;
            var name = full || prev.Name != row.Name;
            var pos = full || prev.X != row.X || prev.Y != row.Y || prev.Z != row.Z;
            var rot = full || prev.Rx != row.Rx || prev.Ry != row.Ry || prev.Rz != row.Rz;
            var hp = full || prev.Health != row.Health;
            var ar = full || prev.Armor != row.Armor;
            var dim = full || prev.Dimension != row.Dimension;
            var veh = full || prev.Vehicle != row.Vehicle || prev.Seat != row.Seat;
            var model = full || prev.Model != row.Model;
            var dead = full || prev.Dead != row.Dead;
            foreach (var f in new[] { name, pos, rot, hp, ar, dim, veh, model, dead }) if (f) fields++;
            if (veh) fields++;
            w.WriteMapHeader(fields);
            w.Write("h"); w.Write(handle);
            if (name) { w.Write("n"); w.Write(row.Name ?? ""); }
            if (pos) { w.Write("p"); w.WriteArrayHeader(3); w.Write(row.X); w.Write(row.Y); w.Write(row.Z); }
            if (rot) { w.Write("r"); w.WriteArrayHeader(3); w.Write(row.Rx); w.Write(row.Ry); w.Write(row.Rz); }
            if (hp) { w.Write("hp"); w.Write(row.Health); }
            if (ar) { w.Write("ar"); w.Write(row.Armor); }
            if (dim) { w.Write("dim"); w.Write(row.Dimension); }
            if (veh) { w.Write("veh"); w.Write(row.Vehicle); w.Write("seat"); w.Write(row.Seat); }
            if (model) { w.Write("model"); w.Write(row.Model); }
            if (dead) { w.Write("dead"); w.Write(row.Dead); }
        }
    }
}
