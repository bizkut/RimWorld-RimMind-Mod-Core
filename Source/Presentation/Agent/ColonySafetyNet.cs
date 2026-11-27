using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RimMind.Application.Common.Models.Mechanisms;
using RimMind.Application.Features.Telemetry;
using RimMind.Infrastructure.Mechanisms.Pawn.Job;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMind.Presentation.Agent
{
    /// <summary>An active colony safety problem (one per code and colonist / corpse).</summary>
    public sealed class SafetyAlert
    {
        public string Code = string.Empty;
        public string Subject = string.Empty;
        public string Severity = "critical";
        public string Detail = string.Empty;
        public int SinceTick;
    }

    /// <summary>
    /// Deterministic colony safety net (no LLM): checks every colonist and colonist corpse on each player home map
    /// and acts immediately — forces a starving colonist to eat, opens a way out for a trapped colonist, has a downed
    /// colonist rescued and a bleeding one tended, and places a grave for a colonist left unburied. Every problem is an
    /// <c>alert</c> event (raised once, then resolved) and every action an <c>intervention</c> event; active alerts are
    /// exposed so the colony overseer sees per-colonist problems. Main thread only.
    /// </summary>
    public static class ColonySafetyNet
    {
        public const float StarvingFood = 0.15f;
        public const float BleedingRate = 0.1f;
        public const int UnburiedTicks = 15000;
        private const int InterventionCooldownTicks = 1250;

        /// <summary>Turned on by the colony overseer while it runs the colony.</summary>
        public static bool Enabled { get; set; }

        private static readonly Dictionary<string, SafetyAlert> Active = new Dictionary<string, SafetyAlert>();
        private static readonly Dictionary<int, int> CorpseFirstSeen = new Dictionary<int, int>();
        private static readonly Dictionary<string, int> LastIntervention = new Dictionary<string, int>();

        public static IReadOnlyList<SafetyAlert> Alerts => Active.Values.ToList();

        /// <summary>Prompt block for the overseer; empty when nothing is wrong.</summary>
        public static string FormatForPrompt()
        {
            if (Active.Count == 0) return string.Empty;
            var sb = new StringBuilder("[colony_alerts] handled by the safety net; fix the cause:\n");
            foreach (var alert in Active.Values)
                sb.Append("- ").Append(alert.Code).Append(' ').Append(alert.Subject).Append(": ").AppendLine(alert.Detail);
            return sb.ToString().TrimEnd();
        }

        public static void Reset()
        {
            Active.Clear();
            CorpseFirstSeen.Clear();
            LastIntervention.Clear();
        }

        public static void Evaluate(Map map)
        {
            int now = Find.TickManager.TicksGame;
            var seen = new HashSet<string>();

            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                string name = pawn.Name?.ToStringShort ?? pawn.LabelShort;
                if (pawn.Downed)
                {
                    Raise(seen, "downed", name, $"downed, health {pawn.health.summaryHealth.SummaryHealthPercent:P0}", now);
                    TryHelp(map, pawn, name, "downed", now, rescue: true);
                    continue;
                }

                float bleed = pawn.health?.hediffSet?.BleedRateTotal ?? 0f;
                if (bleed >= BleedingRate)
                {
                    Raise(seen, "bleeding", name, $"bleeding {bleed:P0}/day", now);
                    TryHelp(map, pawn, name, "bleeding", now, rescue: false);
                }

                if (!pawn.CanReachMapEdge())
                {
                    Raise(seen, "trapped", name, "cannot reach the map edge (sealed in)", now);
                    TryFreeTrapped(map, pawn, name, now);
                }

                var food = pawn.needs?.food;
                if (food != null && food.CurLevelPercentage < StarvingFood && pawn.CurJobDef != JobDefOf.Ingest)
                {
                    Raise(seen, "starving", name, $"food {food.CurLevelPercentage:P0}", now);
                    if (Due("starving:" + name, now))
                    {
                        var result = JobActionDispatcher.HandleEatFood(pawn, new MechanismWriteArgs
                        {
                            MechanismId = "pawn.job", PawnId = pawn.thingIDNumber, Action = "eat_food",
                        });
                        Intervene("starving", name, "force_eat", null, result.IsOk,
                            result.IsOk ? "eating" : result.Error.Message);
                    }
                }
            }

            foreach (var corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().ToList())
            {
                var dead = corpse.InnerPawn;
                if (dead == null || dead.Faction != Faction.OfPlayer || !corpse.Spawned) continue;
                if (!CorpseFirstSeen.TryGetValue(corpse.thingIDNumber, out int first))
                    CorpseFirstSeen[corpse.thingIDNumber] = first = now;
                if (now - first < UnburiedTicks) continue;
                string name = dead.Name?.ToStringShort ?? dead.LabelShort;
                Raise(seen, "unburied", name, $"colonist corpse unburied for {(now - first) / 2500} h", now);
                if (Due("unburied", now)) EnsureGrave(map, name);
            }

            foreach (var key in Active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var alert = Active[key];
                Active.Remove(key);
                LiveEvents.Emit("alert", new JObject
                {
                    ["code"] = alert.Code, ["pawn"] = alert.Subject, ["severity"] = alert.Severity,
                    ["detail"] = alert.Detail, ["resolved"] = true,
                });
                Log.Message($"[RimMind-Safety] resolved {alert.Code} {alert.Subject}");
            }
        }

        private static void Raise(HashSet<string> seen, string code, string subject, string detail, int now)
        {
            string key = code + ":" + subject;
            seen.Add(key);
            if (Active.TryGetValue(key, out var existing))
            {
                existing.Detail = detail;
                return;
            }
            Active[key] = new SafetyAlert { Code = code, Subject = subject, Detail = detail, SinceTick = now };
            LiveEvents.Emit("alert", new JObject
            {
                ["code"] = code, ["pawn"] = subject, ["severity"] = "critical", ["detail"] = detail, ["resolved"] = false,
            });
            Log.Warning($"[RimMind-Safety] {code} {subject}: {detail}");
        }

        private static bool Due(string key, int now)
        {
            if (LastIntervention.TryGetValue(key, out int last) && now - last < InterventionCooldownTicks) return false;
            LastIntervention[key] = now;
            return true;
        }

        private static void Intervene(string code, string subject, string action, string? target, bool ok, string detail)
        {
            LiveEvents.Emit("intervention", new JObject
            {
                ["code"] = code, ["pawn"] = subject, ["action"] = action, ["target"] = target, ["ok"] = ok, ["detail"] = detail,
            });
            Log.Message($"[RimMind-Safety] {action} for {code} {subject}{(target != null ? " → " + target : string.Empty)}: {(ok ? "ok" : "failed")} {detail}");
        }

        /// <summary>Nearest able colonist rescues (downed) or tends (bleeding) the patient.</summary>
        private static void TryHelp(Map map, Pawn patient, string name, string code, int now, bool rescue)
        {
            if (!Due(code + ":" + name, now)) return;
            var helper = map.mapPawns.FreeColonistsSpawned
                .Where(p => p != patient && !p.Downed && !p.Drafted && p.InMentalState == false
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    && p.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly))
                .OrderBy(p => p.Position.DistanceToSquared(patient.Position))
                .FirstOrDefault();
            if (helper == null)
            {
                Intervene(code, name, rescue ? "rescue" : "tend", null, false, "no able colonist can reach them");
                return;
            }
            var args = new MechanismWriteArgs
            {
                MechanismId = "pawn.job", PawnId = helper.thingIDNumber, Action = rescue ? "rescue_pawn" : "tend_pawn",
                Params = new Dictionary<string, string> { ["target_pawn_id"] = patient.thingIDNumber.ToString() },
            };
            var result = rescue ? JobActionDispatcher.HandleRescuePawn(helper, args) : JobActionDispatcher.HandleTendPawn(helper, args);
            Intervene(code, name, rescue ? "rescue" : "tend", helper.Name?.ToStringShort ?? helper.LabelShort, result.IsOk,
                result.IsOk ? "on the way" : result.Error.Message);
        }

        /// <summary>
        /// Finds the cheapest wall/blocker between the colonist's room and ground that reaches the map edge, marks it
        /// for deconstruction (or mining) and has the colonist start on it.
        /// </summary>
        private static void TryFreeTrapped(Map map, Pawn pawn, string name, int now)
        {
            if (!Due("trapped:" + name, now)) return;
            var room = pawn.GetRoom();
            if (room == null || room.TouchesMapEdge)
            {
                Intervene("trapped", name, "open_exit", null, false, "no enclosing room found");
                return;
            }

            Thing? blocker = null;
            int bestCost = int.MaxValue;
            foreach (var cell in room.Cells)
            {
                foreach (var dir in GenAdj.CardinalDirections)
                {
                    var wallCell = cell + dir;
                    var beyond = wallCell + dir;
                    if (!beyond.InBounds(map) || room.ContainsCell(wallCell)) continue;
                    var edifice = wallCell.GetEdifice(map);
                    if (edifice == null || edifice.def.passability != Traversability.Impassable) continue;
                    if (!beyond.Walkable(map) || !map.reachability.CanReachMapEdge(beyond, TraverseParms.For(TraverseMode.PassDoors)))
                        continue;
                    bool natural = edifice.def.building?.isNaturalRock == true;
                    int cost = natural ? 2 : 1;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        blocker = edifice;
                    }
                }
            }

            if (blocker == null)
            {
                Intervene("trapped", name, "open_exit", null, false, "no single wall separates the room from open ground");
                return;
            }

            bool mine = blocker.def.building?.isNaturalRock == true;
            var designation = mine ? DesignationDefOf.Mine : DesignationDefOf.Deconstruct;
            if (map.designationManager.DesignationOn(blocker, designation) == null && map.designationManager.DesignationAt(blocker.Position, designation) == null)
            {
                if (mine) map.designationManager.AddDesignation(new Designation(blocker.Position, designation));
                else map.designationManager.AddDesignation(new Designation(blocker, designation));
            }
            var job = JobMaker.MakeJob(mine ? JobDefOf.Mine : JobDefOf.Deconstruct, blocker);
            var started = JobActionDispatcher.StartJobVerified(pawn, job);
            Intervene("trapped", name, mine ? "mine_exit" : "deconstruct_blocker", $"{blocker.def.defName}@({blocker.Position.x},{blocker.Position.z})",
                true, started.IsOk ? "colonist is opening the way" : "designated; colonists will open it");
        }

        /// <summary>Places a grave blueprint near the base unless an empty grave or a planned one exists.</summary>
        private static void EnsureGrave(Map map, string name)
        {
            bool hasGrave = map.listerBuildings.AllBuildingsColonistOfClass<Building_Grave>().Any(g => !g.HasCorpse)
                || map.listerThings.ThingsOfDef(ThingDefOf.Grave.blueprintDef).Any()
                || map.listerThings.ThingsOfDef(ThingDefOf.Grave.frameDef).Any();
            if (hasGrave) return;

            var buildings = map.listerBuildings.allBuildingsColonist;
            var colonists = map.mapPawns.FreeColonistsSpawned;
            IntVec3 origin = buildings.Count > 0
                ? new IntVec3((int)buildings.Average(b => b.Position.x), 0, (int)buildings.Average(b => b.Position.z))
                : colonists.Count > 0
                    ? new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z))
                    : map.Center;

            foreach (var cell in GenRadial.RadialCellsAround(origin, 30f, true))
            {
                if (cell.DistanceTo(origin) < 10f || !cell.InBounds(map) || cell.Fogged(map)) continue;
                if (!(cell.GetRoom(map)?.PsychologicallyOutdoors ?? true)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Grave, cell, Rot4.North, map).Accepted) continue;
                GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Grave, cell, map, Rot4.North, Faction.OfPlayer, null);
                Intervene("unburied", name, "place_grave", $"Grave@({cell.x},{cell.z})", true, "colonists will dig it and bury the body");
                return;
            }
            Intervene("unburied", name, "place_grave", null, false, "no free outdoor cell for a grave");
        }
    }
}
