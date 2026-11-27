using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimMind.Presentation.Agent
{
    /// <summary>A point of interest on the geography overview (map cell coordinates).</summary>
    public sealed class GeographyPoi
    {
        public string Kind = string.Empty;
        public string Label = string.Empty;
        public int X;
        public int Z;
        public int Size;
    }

    /// <summary>Whole-map overview: coarse terrain/feature grid plus points of interest.</summary>
    public sealed class MapGeography
    {
        public int MapId;
        public int Width;
        public int Height;
        public int Block;
        public int Cols;
        public int Rows;
        /// <summary>Row 0 is the northern-most row (largest z).</summary>
        public List<string> Grid = new List<string>();
        public List<GeographyPoi> Pois = new List<GeographyPoi>();
        public string Text = string.Empty;
        public int ComputedTick;
    }

    /// <summary>
    /// Builds a whole-map geography overview so colonists, the Advisor and the colony overseer know where fertile soil,
    /// open buildable ground, water, rock, ore and the base are. One symbol per block of cells; slow-changing content
    /// only (terrain, base buildings, zones), recomputed at most once per in-game day so it stays a stable, cacheable
    /// L1 prompt prefix. Main thread only.
    /// </summary>
    public static class MapGeographyBuilder
    {
        public const int TargetBlocksPerSide = 25;
        public const int RefreshTicks = 60000;

        // Vanilla fertility: rich soil 1.4, soil 1.0, gravel/stony 0.7, sand 0.1.
        private const float RichSoilFertility = 1.3f;
        private const float SoilFertility = 0.95f;

        public static readonly IReadOnlyDictionary<char, string> Legend = new Dictionary<char, string>
        {
            ['#'] = "base buildings", ['G'] = "growing zone", ['S'] = "stockpile", ['$'] = "ore",
            ['~'] = "water", ['^'] = "mountain/rock", ['m'] = "marsh/mud", ['T'] = "dense trees",
            ['"'] = "rich soil", [','] = "soil", ['.'] = "poor soil/sand", ['?'] = "unexplored",
        };

        private static readonly Dictionary<int, MapGeography> Cache = new Dictionary<int, MapGeography>();

        /// <summary>Cached overview for the map; rebuilt when older than <see cref="RefreshTicks"/>.</summary>
        public static MapGeography Get(Map map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            int now = Find.TickManager?.TicksGame ?? 0;
            if (Cache.TryGetValue(map.uniqueID, out var cached) && now - cached.ComputedTick < RefreshTicks)
                return cached;
            var built = Build(map, now);
            Cache[map.uniqueID] = built;
            return built;
        }

        public static void ClearCache() => Cache.Clear();

        private sealed class BlockStats
        {
            public int Cells, Fogged, Water, Rock, Marsh, Trees, Ore, Base, Growing, Stockpile, Open, Fertile;
            public float FertilitySum;
            public int FertilityCells;
            public readonly Dictionary<string, int> OreKinds = new Dictionary<string, int>();
        }

        private static MapGeography Build(Map map, int now)
        {
            int w = map.Size.x, h = map.Size.z;
            int block = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)TargetBlocksPerSide));
            int cols = (w + block - 1) / block, rows = (h + block - 1) / block;
            var stats = new BlockStats[cols, rows];
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    stats[c, r] = new BlockStats();

            var baseCells = new HashSet<IntVec3>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (b == null || !b.Spawned) continue;
                foreach (var cell in b.OccupiedRect()) baseCells.Add(cell);
            }

            foreach (var cell in map.AllCells)
            {
                var s = stats[cell.x / block, cell.z / block];
                s.Cells++;
                if (cell.Fogged(map)) { s.Fogged++; continue; }

                if (baseCells.Contains(cell)) s.Base++;
                var zone = map.zoneManager.ZoneAt(cell);
                if (zone is Zone_Growing) s.Growing++;
                else if (zone is Zone_Stockpile) s.Stockpile++;

                var edifice = cell.GetEdifice(map);
                if (edifice?.def?.building != null && edifice.def.building.isNaturalRock)
                {
                    s.Rock++;
                    if (edifice.def.building.isResourceRock && edifice.def.building.mineableThing != null)
                    {
                        s.Ore++;
                        string ore = edifice.def.building.mineableThing.label ?? edifice.def.building.mineableThing.defName;
                        s.OreKinds[ore] = s.OreKinds.TryGetValue(ore, out var n) ? n + 1 : 1;
                    }
                    continue;
                }

                var terrain = cell.GetTerrain(map);
                string tName = terrain?.defName ?? string.Empty;
                if (tName.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) >= 0
                    || tName.IndexOf("Mud", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s.Marsh++;
                    continue;
                }
                if (terrain != null && terrain.IsWater)
                {
                    s.Water++;
                    continue;
                }

                var plant = cell.GetPlant(map);
                if (plant?.def?.plant != null && plant.def.plant.IsTree) s.Trees++;

                float fertility = cell.GetFertility(map);
                s.FertilitySum += fertility;
                s.FertilityCells++;
                if (fertility >= SoilFertility) s.Fertile++;
                if (edifice == null && cell.Standable(map)) s.Open++;
            }

            var geo = new MapGeography
            {
                MapId = map.uniqueID, Width = w, Height = h, Block = block, Cols = cols, Rows = rows, ComputedTick = now,
            };

            for (int r = rows - 1; r >= 0; r--)
            {
                var line = new StringBuilder(cols);
                for (int c = 0; c < cols; c++) line.Append(Symbol(stats[c, r]));
                geo.Grid.Add(line.ToString());
            }

            BuildPois(map, geo, stats, baseCells);
            geo.Text = BuildText(map, geo);
            return geo;
        }

        private static char Symbol(BlockStats s)
        {
            if (s.Cells == 0) return '?';
            float Share(int n) => n / (float)s.Cells;
            if (Share(s.Fogged) > 0.5f) return '?';
            if (s.Base >= 3) return '#';
            if (Share(s.Growing) >= 0.2f) return 'G';
            if (Share(s.Stockpile) >= 0.2f) return 'S';
            if (s.Ore >= 3) return '$';
            if (Share(s.Water) >= 0.5f) return '~';
            if (Share(s.Rock) >= 0.5f) return '^';
            if (Share(s.Marsh) >= 0.5f) return 'm';
            if (Share(s.Trees) >= 0.15f) return 'T';
            float fertility = s.FertilityCells > 0 ? s.FertilitySum / s.FertilityCells : 0f;
            if (fertility >= RichSoilFertility) return '"';
            if (fertility >= SoilFertility) return ',';
            return '.';
        }

        private static IntVec3 BlockCenter(MapGeography geo, int c, int r) =>
            new IntVec3(Math.Min(geo.Width - 1, c * geo.Block + geo.Block / 2), 0, Math.Min(geo.Height - 1, r * geo.Block + geo.Block / 2));

        private static void BuildPois(Map map, MapGeography geo, BlockStats[,] stats, HashSet<IntVec3> baseCells)
        {
            // Anchor: base buildings, else colonists, else map center.
            IntVec3 anchor;
            if (baseCells.Count > 0)
            {
                anchor = new IntVec3((int)baseCells.Average(c => c.x), 0, (int)baseCells.Average(c => c.z));
                geo.Pois.Add(new GeographyPoi { Kind = "base", Label = "Base center", X = anchor.x, Z = anchor.z, Size = baseCells.Count });
            }
            else
            {
                var colonists = map.mapPawns.FreeColonistsSpawned;
                anchor = colonists.Count > 0
                    ? new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z))
                    : map.Center;
                geo.Pois.Add(new GeographyPoi { Kind = "camp", Label = "Colonists' camp (no base yet)", X = anchor.x, Z = anchor.z });
            }

            float Dist(IntVec3 a) => (float)Math.Sqrt(a.DistanceToSquared(anchor));

            // Best block for a value, preferring blocks near the anchor (one block of distance ≈ 2 cells of value).
            void AddBest(string kind, string label, Func<BlockStats, int> value, int minValue)
            {
                int bestC = -1, bestR = -1;
                float bestScore = float.MinValue;
                for (int c = 0; c < geo.Cols; c++)
                    for (int r = 0; r < geo.Rows; r++)
                    {
                        int v = value(stats[c, r]);
                        if (v < minValue) continue;
                        float score = v - Dist(BlockCenter(geo, c, r)) * 2f / geo.Block;
                        if (score > bestScore) { bestScore = score; bestC = c; bestR = r; }
                    }
                if (bestC < 0) return;
                var center = BlockCenter(geo, bestC, bestR);
                geo.Pois.Add(new GeographyPoi { Kind = kind, Label = label, X = center.x, Z = center.z, Size = value(stats[bestC, bestR]) });
            }

            int area = geo.Block * geo.Block;
            AddBest("fertile", "Fertile soil (for fields)", s => s.Fertile - s.Growing - s.Base, area / 3);
            AddBest("open", "Open flat ground (for buildings)", s => s.Open - s.Base - s.Growing - s.Stockpile, area / 2);
            AddBest("water", "Water", s => s.Water, area / 4);
            AddBest("rock", "Mountain/rock edge", s => s.Rock, area / 3);

            // Ore: one entry per kind (largest kinds first) at its richest block, with the map-wide total.
            var ores = new List<(string Kind, int Count, int C, int R)>();
            for (int c = 0; c < geo.Cols; c++)
                for (int r = 0; r < geo.Rows; r++)
                    foreach (var kv in stats[c, r].OreKinds)
                        ores.Add((kv.Key, kv.Value, c, r));
            foreach (var kind in ores.GroupBy(o => o.Kind).OrderByDescending(g => g.Sum(o => o.Count)).Take(5))
            {
                var richest = kind.OrderByDescending(o => o.Count).First();
                var center = BlockCenter(geo, richest.C, richest.R);
                geo.Pois.Add(new GeographyPoi
                {
                    Kind = "ore",
                    Label = $"Ore: {kind.Key} ({kind.Sum(o => o.Count)} cells in {kind.Count()} deposits; richest here)",
                    X = center.x, Z = center.z, Size = richest.Count,
                });
            }

            foreach (var hostile in map.mapPawns.AllPawnsSpawned
                         .Where(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer))
                         .Take(3))
            {
                geo.Pois.Add(new GeographyPoi { Kind = "hostile", Label = $"Hostile: {hostile.LabelShort}", X = hostile.Position.x, Z = hostile.Position.z });
            }
        }

        private static string Direction(IntVec3 from, int x, int z)
        {
            int dx = x - from.x, dz = z - from.z;
            if (Math.Abs(dx) < 6 && Math.Abs(dz) < 6) return "at";
            string ns = dz > 5 ? "N" : dz < -5 ? "S" : string.Empty;
            string ew = dx > 5 ? "E" : dx < -5 ? "W" : string.Empty;
            return ns + ew;
        }

        private static string BuildText(Map map, MapGeography geo)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[map_geography] Map {geo.Width}x{geo.Height} cells; overview {geo.Cols}x{geo.Rows}, one symbol = {geo.Block}x{geo.Block} cells. North at top; x grows east (left→right), z grows north.");
            sb.Append("Legend: ").AppendLine(string.Join(" ", Legend.Select(kv => $"{kv.Key}={kv.Value}")));
            for (int i = 0; i < geo.Grid.Count; i++)
            {
                int zTop = (geo.Rows - 1 - i) * geo.Block;
                sb.Append("z").Append(zTop.ToString().PadLeft(3)).Append(' ').AppendLine(geo.Grid[i]);
            }
            sb.Append("x=0").Append(' ', Math.Max(1, geo.Cols - 3)).Append('→').AppendLine($" x={geo.Width - 1}");

            var anchorPoi = geo.Pois.FirstOrDefault(p => p.Kind == "base" || p.Kind == "camp");
            var anchor = anchorPoi != null ? new IntVec3(anchorPoi.X, 0, anchorPoi.Z) : map.Center;
            sb.AppendLine("Points of interest (x,z):");
            foreach (var poi in geo.Pois)
            {
                sb.Append("- ").Append(poi.Label).Append(" at (").Append(poi.X).Append(',').Append(poi.Z).Append(')');
                if (poi.Size > 0) sb.Append(", ").Append(poi.Size).Append(" cells");
                if (poi != anchorPoi)
                {
                    int dist = (int)Math.Sqrt(new IntVec3(poi.X, 0, poi.Z).DistanceToSquared(anchor));
                    string dir = Direction(anchor, poi.X, poi.Z);
                    if (dir != "at") sb.Append(", ").Append(dist).Append(" cells ").Append(dir).Append(" of ").Append(anchorPoi?.Kind == "base" ? "base" : "camp");
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
