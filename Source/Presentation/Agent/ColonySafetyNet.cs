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
        private static readonly Dictionary<int, List<int>> BotchTicks = new Dictionary<int, List<int>>();
        public const int BotchAlertCount = 3;

        /// <summary>Called for every botched construction (Harmony postfix on <c>Frame.FailConstruction</c>).</summary>
        public static void RecordBotchedConstruction(Pawn? worker, Frame frame)
        {
            if (worker == null || !Enabled) return;
            if (!BotchTicks.TryGetValue(worker.thingIDNumber, out var ticks))
                BotchTicks[worker.thingIDNumber] = ticks = new List<int>();
            ticks.Add(Find.TickManager.TicksGame);
            LiveEvents.Emit("action", new JObject
            {
                ["source"] = "game", ["npc"] = null, ["pawn"] = worker.Name?.ToStringShort ?? worker.LabelShort,
                ["tool"] = "construction_botched", ["ok"] = false,
                ["result"] = $"botched {frame.def?.entityDefToBuild?.label ?? frame.LabelShort}; Construction skill {worker.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? 0}",
            });
        }

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
            BotchTicks.Clear();
        }

        public static void Evaluate(Map map)
        {
            int now = Find.TickManager.TicksGame;
            string scope = map.uniqueID + ":";
            var seen = new HashSet<string>();

            // FreeColonists also lists colonists being carried (not spawned): their alerts stay active, but nobody
            // acts on them while someone carries them.
            foreach (var pawn in map.mapPawns.FreeColonists.ToList())
            {
                string name = pawn.Name?.ToStringShort ?? pawn.LabelShort;
                bool carried = !pawn.Spawned;
                var food = pawn.needs?.food;
                bool starving = food != null && food.CurLevelPercentage < StarvingFood && pawn.CurJobDef != JobDefOf.Ingest;

                if (pawn.Downed)
                {
                    Raise(seen, scope, "downed", name, $"downed, health {pawn.health.summaryHealth.SummaryHealthPercent:P0}", now);
                    if (starving) Raise(seen, scope, "starving", name, $"food {food!.CurLevelPercentage:P0} (downed)", now);
                    if (carried) continue;
                    if (!pawn.InBed()) TryHelp(map, pawn, name, "downed", now, HelpKind.Rescue);
                    else if (starving) TryHelp(map, pawn, name, "starving", now, HelpKind.Feed);
                    continue;
                }
                if (carried) continue;

                float bleed = pawn.health?.hediffSet?.BleedRateTotal ?? 0f;
                if (bleed >= BleedingRate)
                {
                    Raise(seen, scope, "bleeding", name, $"bleeding {bleed:P0}/day", now);
                    TryHelp(map, pawn, name, "bleeding", now, HelpKind.Tend);
                }

                if (!pawn.CanReachMapEdge())
                {
                    Raise(seen, scope, "trapped", name, "cannot reach the map edge (sealed in)", now);
                    TryFreeTrapped(map, pawn, name, now);
                }

                if (starving)
                {
                    Raise(seen, scope, "starving", name, $"food {food!.CurLevelPercentage:P0}", now);
                    // Someone carrying a person always finishes the carry (interrupting drops the patient); someone
                    // treating a patient finishes first unless close to death.
                    bool carrying = pawn.carryTracker?.CarriedThing is Pawn;
                    bool helping = pawn.CurJobDef == JobDefOf.Rescue || pawn.CurJobDef == JobDefOf.TendPatient
                        || pawn.CurJobDef == JobDefOf.FeedPatient;
                    if (!carrying && (!helping || food.CurLevelPercentage < 0.05f) && Due("starving:" + name, now))
                    {
                        FreeForForcedJob(pawn);
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
                if (dead == null || dead.Faction != Faction.OfPlayer || !dead.RaceProps.Humanlike || !corpse.Spawned) continue;
                if (!CorpseFirstSeen.TryGetValue(corpse.thingIDNumber, out int first))
                    CorpseFirstSeen[corpse.thingIDNumber] = first = now;
                if (now - first < UnburiedTicks) continue;
                string name = dead.Name?.ToStringShort ?? dead.LabelShort;
                Raise(seen, scope, "unburied", name, $"colonist corpse unburied for {(now - first) / 2500} h", now);
                if (Due("unburied", now)) EnsureGrave(map, name);
            }

            // Repeated botched construction: the builder is too unskilled for what it is building.
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (!BotchTicks.TryGetValue(pawn.thingIDNumber, out var ticks)) continue;
                ticks.RemoveAll(t => now - t > 60000);
                if (ticks.Count < BotchAlertCount) continue;
                int skill = pawn.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? 0;
                Raise(seen, scope, "botching", pawn.Name?.ToStringShort ?? pawn.LabelShort,
                    $"{ticks.Count} botched constructions in the last day (Construction skill {skill}); give construction to a " +
                    "more skilled colonist (pawn_work_set) or build cheaper things", now);
            }

            foreach (var key in Active.Keys.Where(k => k.StartsWith(scope, StringComparison.Ordinal) && !seen.Contains(k)).ToList())
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

        private static void Raise(HashSet<string> seen, string scope, string code, string subject, string detail, int now)
        {
            string key = scope + code + ":" + subject;
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

        /// <summary>Any hostile, able pawn within 15 cells (manhunters included).</summary>
        private static bool HostileNear(Map map, IntVec3 cell) =>
            map.mapPawns.AllPawnsSpawned.Any(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer)
                && p.Position.DistanceToSquared(cell) <= 15 * 15);

        /// <summary>
        /// Drops a carried item and ends the current job so a forced job can start; the game otherwise keeps a hauling
        /// colonist on its job ("did not start job ... current job: HaulToCell"). Callers never pass a colonist who is
        /// carrying a person: ending that job would drop the patient.
        /// </summary>
        private static void FreeForForcedJob(Pawn pawn)
        {
            var carried = pawn.carryTracker?.CarriedThing;
            if (carried != null && carried is not Pawn)
                pawn.carryTracker!.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            if (pawn.CurJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        private enum HelpKind { Rescue, Tend, Feed }

        /// <summary>
        /// Nearest able, fed colonist rescues (downed, not in bed), tends (bleeding) or feeds (downed in bed and
        /// starving) the patient — unless someone is already doing it.
        /// </summary>
        private static void TryHelp(Map map, Pawn patient, string name, string code, int now, HelpKind kind)
        {
            var jobDef = kind switch
            {
                HelpKind.Rescue => JobDefOf.Rescue,
                HelpKind.Tend => JobDefOf.TendPatient,
                _ => JobDefOf.FeedPatient,
            };
            string action = kind.ToString().ToLowerInvariant();
            var colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists.Any(p => p.CurJobDef == jobDef && (p.CurJob.targetA.Thing == patient || p.CurJob.targetB.Thing == patient)))
                return;
            if (!Due(code + ":" + action + ":" + name, now)) return;

            bool Able(Pawn p) => p != patient && !p.Downed && !p.InMentalState
                && p.carryTracker?.CarriedThing is not Pawn
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                && p.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly);
            var helper = colonists
                .Where(p => !p.Drafted && Able(p))
                .OrderBy(p => (p.needs?.food?.CurLevelPercentage ?? 1f) < StarvingFood ? 1 : 0)
                .ThenBy(p => p.Position.DistanceToSquared(patient.Position))
                .FirstOrDefault();

            // Nobody free: undraft the nearest drafted colonist, but only when no hostile is close to them or the
            // patient (agents often leave colonists drafted after a fight; drafted colonists take no jobs).
            if (helper == null)
            {
                var drafted = colonists
                    .Where(p => p.Drafted && Able(p) && !HostileNear(map, p.Position) && !HostileNear(map, patient.Position))
                    .OrderBy(p => p.Position.DistanceToSquared(patient.Position))
                    .FirstOrDefault();
                if (drafted != null)
                {
                    drafted.drafter.Drafted = false;
                    helper = drafted;
                    Intervene(code, name, "undraft_helper", drafted.Name?.ToStringShort ?? drafted.LabelShort, true, "no hostiles nearby");
                }
            }
            if (helper == null)
            {
                Intervene(code, name, action, null, false, "no able colonist can reach them");
                return;
            }
            string helperName = helper.Name?.ToStringShort ?? helper.LabelShort;

            if (kind == HelpKind.Feed)
            {
                if (!FoodUtility.TryFindBestFoodSourceFor(helper, patient, false, out Thing foodSource, out ThingDef foodDef, canRefillDispenser: false))
                {
                    Intervene(code, name, action, helperName, false, "no food the patient can eat");
                    return;
                }
                var feed = JobMaker.MakeJob(JobDefOf.FeedPatient, foodSource, patient);
                feed.count = FoodUtility.WillIngestStackCountOf(patient, foodDef, FoodUtility.GetNutrition(patient, foodSource, foodDef));
                FreeForForcedJob(helper);
                var fed = JobActionDispatcher.StartJobVerified(helper, feed);
                Intervene(code, name, action, helperName, fed.IsOk, fed.IsOk ? "bringing food" : fed.Error.Message);
                return;
            }

            var args = new MechanismWriteArgs
            {
                MechanismId = "pawn.job", PawnId = helper.thingIDNumber, Action = kind == HelpKind.Rescue ? "rescue_pawn" : "tend_pawn",
                Params = new Dictionary<string, string> { ["target_pawn_id"] = patient.thingIDNumber.ToString() },
            };
            FreeForForcedJob(helper);
            var result = kind == HelpKind.Rescue ? JobActionDispatcher.HandleRescuePawn(helper, args) : JobActionDispatcher.HandleTendPawn(helper, args);
            Intervene(code, name, action, helperName, result.IsOk, result.IsOk ? "on the way" : result.Error.Message);
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
