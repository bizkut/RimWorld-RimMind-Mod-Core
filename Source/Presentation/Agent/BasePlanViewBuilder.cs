using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMind.Presentation.Agent
{
    /// <summary>
    /// One-character-per-cell plan of the base (walls, doors, beds, furniture, blueprints, zones) plus per-room facts
    /// (size, role, doors, connected to the outside), so every model can see the structure geometry — the coarse
    /// geography overview cannot show that a door opens into a wall. Rebuilt only when colony buildings, blueprints or
    /// zones change; main thread only.
    /// </summary>
    public static class BasePlanViewBuilder
    {
        public const int MaxSide = 48;
        private const int Padding = 3;

        private static readonly Dictionary<int, (int Signature, string Text)> Cache = new Dictionary<int, (int, string)>();

        public static string Get(Map map)
        {
            int signature = Signature(map);
            if (Cache.TryGetValue(map.uniqueID, out var cached) && cached.Signature == signature)
                return cached.Text;
            string text = Build(map);
            Cache[map.uniqueID] = (signature, text);
            return text;
        }

        private static int Signature(Map map)
        {
            unchecked
            {
                int hash = map.listerBuildings.allBuildingsColonist.Count;
                hash = hash * 31 + map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count;
                hash = hash * 31 + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count;
                foreach (var zone in map.zoneManager.AllZones) hash = hash * 31 + zone.CellCount;
                return hash;
            }
        }

        private static string Build(Map map)
        {
            var points = map.listerBuildings.allBuildingsColonist.Select(b => b.Position)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Select(t => t.Position))
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Select(t => t.Position))
                .Concat(map.zoneManager.AllZones.SelectMany(z => z.Cells))
                .ToList();
            if (points.Count == 0)
                return "[base_plan] No buildings, blueprints or zones yet.";

            // Bounding box around the base, capped at MaxSide around the densest centre.
            int cx = (int)points.Average(p => p.x), cz = (int)points.Average(p => p.z);
            int minX = Math.Max(0, Math.Max(points.Min(p => p.x) - Padding, cx - MaxSide / 2));
            int maxX = Math.Min(map.Size.x - 1, Math.Min(points.Max(p => p.x) + Padding, cx + MaxSide / 2 - 1));
            int minZ = Math.Max(0, Math.Max(points.Min(p => p.z) - Padding, cz - MaxSide / 2));
            int maxZ = Math.Min(map.Size.z - 1, Math.Min(points.Max(p => p.z) + Padding, cz + MaxSide / 2 - 1));

            var sb = new StringBuilder();
            sb.AppendLine($"[base_plan] Base area x {minX}..{maxX}, z {minZ}..{maxZ}; one character per cell, north at top.");
            sb.AppendLine("Legend: #=wall D=door b=bed x=other building/furniture +=blueprint/frame S=stockpile G=growing .=indoor floor ,=outdoors ^=rock ~=water ?=unexplored");
            for (int z = maxZ; z >= minZ; z--)
            {
                sb.Append('z').Append(z.ToString().PadLeft(3)).Append(' ');
                for (int x = minX; x <= maxX; x++) sb.Append(Symbol(map, new IntVec3(x, 0, z)));
                sb.AppendLine();
            }

            var rooms = new HashSet<Room>();
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    var room = new IntVec3(x, 0, z).GetRoom(map);
                    if (room != null && !room.PsychologicallyOutdoors && !room.TouchesMapEdge && room.CellCount <= 400) rooms.Add(room);
                }
            if (rooms.Count > 0)
            {
                sb.AppendLine("Rooms:");
                foreach (var room in rooms.OrderBy(r => r.ID))
                {
                    var doors = room.ContainedAndAdjacentThings.OfType<Building_Door>().ToList();
                    bool connected = doors.Any(d => GenAdj.CellsAdjacentCardinal(d).Any(c => c.InBounds(map) && c.Walkable(map)
                        && c.GetRoom(map) != room && map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.PassDoors))));
                    var center = room.Cells.Aggregate((x: 0, z: 0, n: 0), (a, c) => (a.x + c.x, a.z + c.z, a.n + 1));
                    sb.Append("- ").Append(room.Role?.label ?? "room").Append(" at (")
                      .Append(center.x / Math.Max(1, center.n)).Append(',').Append(center.z / Math.Max(1, center.n)).Append("), ")
                      .Append(room.CellCount).Append(" cells, ").Append(doors.Count).Append(doors.Count == 1 ? " door" : " doors")
                      .AppendLine(connected ? ", connected to outside" : ", NOT connected to outside (sealed: anyone inside is trapped)");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static char Symbol(Map map, IntVec3 cell)
        {
            if (cell.Fogged(map)) return '?';
            foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
            {
                if (thing is Blueprint || thing is Frame) return '+';
            }
            var edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                if (edifice.def.building?.isNaturalRock == true) return '^';
                if (edifice is Building_Door) return 'D';
                if (edifice.def.passability == Traversability.Impassable && edifice.def.fillPercent >= 0.99f) return '#';
            }
            foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
            {
                if (thing is Building_Bed) return 'b';
                if (thing is Building building && building.Faction == Faction.OfPlayer) return 'x';
            }
            var zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Stockpile) return 'S';
            if (zone is Zone_Growing) return 'G';
            if (cell.GetTerrain(map)?.IsWater == true) return '~';
            var room = cell.GetRoom(map);
            return room != null && !room.PsychologicallyOutdoors ? '.' : ',';
        }
    }
}
