using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimMind.Application.Features.Telemetry;
using RimMind.Domain.Events;
using RimMind.Infrastructure.UI.DebugActions;
using RimMind.Presentation;
using RimMind.Presentation.Agent;
using RimMind.Presentation.Api;
using RimWorld;
using Verse;

namespace RimMind.Infrastructure.Verse
{
    /// <summary>
    /// Writes the live event stream (docs/autonomous-play/EVENTS.md) for the web dashboard: session start, hourly
    /// snapshots, geography changes, letters, agent decisions/actions (agent bus) and — via <see cref="LiveEvents"/> —
    /// LLM requests and overseer events. Lines are queued from any thread and appended on the main thread.
    /// Location: <c>$RIMMIND_LIVE_DIR/events.ndjson</c>, else <c>&lt;save data&gt;/RimMind/Live/&lt;session&gt;/events.ndjson</c>
    /// with <c>Live/current.txt</c> pointing at the active file.
    /// </summary>
    public sealed class LiveEventGameComponent : GameComponent
    {
        private const int SnapshotEveryTicks = 2500;
        private const int HousekeepingEveryTicks = 60;

        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static volatile int _tick;
        private static int _startTick;

        private string? _path;
        private readonly HashSet<int> _seenLetters = new HashSet<int>();
        private readonly Dictionary<int, int> _geographyTick = new Dictionary<int, int>();
        private string? _decisionKey;
        private string? _actionKey;

        public LiveEventGameComponent(Game game) { }

        public override void StartedNewGame() => Begin();

        public override void LoadedGame() => Begin();

        private void Begin()
        {
            try
            {
                _path = ResolvePath();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                _startTick = Find.TickManager?.TicksGame ?? 0;
                _tick = _startTick;
                _seenLetters.Clear();
                _geographyTick.Clear();
                foreach (var letter in Find.LetterStack?.LettersListForReading ?? new List<Letter>())
                    _seenLetters.Add(letter.ID);

                LiveEvents.Sink = Enqueue;
                SubscribeBus();

                var map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
                string? runId = GenCommandLine.TryGetCommandLineArg("rimmind-playthrough", out var id) ? id : null;
                LiveEvents.Emit("session_start", new JObject
                {
                    ["map_id"] = map?.uniqueID,
                    ["map_size"] = map == null ? null : new JArray(map.Size.x, map.Size.z),
                    ["biome"] = map?.Biome?.label,
                    ["colony"] = Faction.OfPlayer?.Name,
                    ["model"] = RimMindCoreMod.Settings?.modelName,
                    ["run_id"] = runId,
                    ["speed"] = Find.TickManager?.CurTimeSpeed.ToString(),
                });
                if (map != null)
                {
                    EmitGeographyIfChanged(map);
                    EmitSnapshot();
                }
                Flush();
                Log.Message($"[RimMind-Live] event stream: {_path}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimMind-Live] event stream disabled: {ex.Message}");
                LiveEvents.Sink = null;
                _path = null;
            }
        }

        private static string ResolvePath()
        {
            string? dir = Environment.GetEnvironmentVariable("RIMMIND_LIVE_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
                return Path.Combine(dir!, "events.ndjson");

            string live = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimMind", "Live");
            string session = Path.Combine(live, DateTime.Now.ToString("yyyyMMdd-HHmmss"), "events.ndjson");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "current.txt"), session);
            return session;
        }

        private void SubscribeBus()
        {
            var bus = RimMindAPI.Bus.GetAgentBus();
            if (_decisionKey != null) bus.Unsubscribe<DecisionEvent>(_decisionKey);
            if (_actionKey != null) bus.Unsubscribe<ActionEvent>(_actionKey);
            _decisionKey = bus.Subscribe<DecisionEvent>(e => LiveEvents.Emit("decision", new JObject
            {
                ["source"] = SourceOf(e.NpcId),
                ["npc"] = e.NpcId,
                ["pawn"] = PawnName(e.PawnId),
                ["action"] = e.Action,
                ["reason"] = Trim(e.Reason, 300),
            }));
            _actionKey = bus.Subscribe<ActionEvent>(e => LiveEvents.Emit("action", new JObject
            {
                ["source"] = SourceOf(e.NpcId),
                ["npc"] = e.NpcId,
                ["pawn"] = PawnName(e.PawnId),
                ["tool"] = e.ActionName,
                ["ok"] = e.Success,
                ["result"] = Trim(e.ResultReason, 300),
            }));
        }

        public override void GameComponentTick()
        {
            if (_path == null) return;
            int now = Find.TickManager.TicksGame;
            _tick = now;
            if (now % HousekeepingEveryTicks != 0) return;

            EmitNewLetters();
            if (now % SnapshotEveryTicks == 0)
            {
                EmitSnapshot();
                foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
                    EmitGeographyIfChanged(map);
            }
            Flush();
        }

        private void EmitSnapshot()
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            var c = PlaythroughColonyStats.Capture(map);
            var colonists = new JArray();
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                colonists.Add(new JObject
                {
                    ["id"] = p.thingIDNumber,
                    ["name"] = p.Name?.ToStringShort ?? p.LabelShort,
                    ["x"] = p.Position.x,
                    ["z"] = p.Position.z,
                    ["mood"] = Round(p.needs?.mood?.CurLevel),
                    ["food"] = Round(p.needs?.food?.CurLevel),
                    ["rest"] = Round(p.needs?.rest?.CurLevel),
                    ["health"] = Round(p.health?.summaryHealth?.SummaryHealthPercent),
                    ["downed"] = p.Downed,
                    ["mental"] = p.MentalStateDef?.defName,
                    ["job"] = p.CurJobDef?.defName,
                    ["agent"] = CompPawnAgent.GetComp(p)?.Agent?.State.ToString(),
                    ["drafted"] = p.Drafted,
                });
            }
            LiveEvents.Emit("snapshot", new JObject
            {
                ["colony"] = new JObject
                {
                    ["alive"] = c.ColonistsAlive,
                    ["downed"] = c.ColonistsDowned,
                    ["mood"] = Round(c.AverageMood),
                    ["food_days"] = Round(c.FoodDays),
                    ["wealth"] = (int)c.Wealth,
                    ["research_done"] = c.ResearchCompleted,
                    ["active_bills"] = c.ActiveBills,
                    ["pending_construction"] = c.PendingConstruction,
                    ["buildings"] = map.listerBuildings.allBuildingsColonist.Count,
                    ["hostiles"] = c.Hostiles,
                    ["fires"] = c.Fires,
                    ["outdoor_temp"] = (int)map.mapTemperature.OutdoorTemp,
                    ["speed"] = Find.TickManager.CurTimeSpeed.ToString(),
                    ["paused"] = Find.TickManager.Paused,
                },
                ["colonists"] = colonists,
            });
        }

        private void EmitGeographyIfChanged(Map map)
        {
            var geo = MapGeographyBuilder.Get(map);
            if (_geographyTick.TryGetValue(map.uniqueID, out var last) && last == geo.ComputedTick) return;
            _geographyTick[map.uniqueID] = geo.ComputedTick;
            LiveEvents.Emit("geography", new JObject
            {
                ["map_id"] = geo.MapId,
                ["width"] = geo.Width,
                ["height"] = geo.Height,
                ["block"] = geo.Block,
                ["cols"] = geo.Cols,
                ["rows"] = geo.Rows,
                ["grid"] = new JArray(geo.Grid),
                ["legend"] = JObject.FromObject(MapGeographyBuilder.Legend.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)),
                ["pois"] = new JArray(geo.Pois.Select(p => new JObject
                {
                    ["kind"] = p.Kind, ["label"] = p.Label, ["x"] = p.X, ["z"] = p.Z, ["size"] = p.Size,
                })),
                ["text"] = geo.Text,
            });
        }

        private void EmitNewLetters()
        {
            var letters = Find.LetterStack?.LettersListForReading;
            if (letters == null) return;
            foreach (var letter in letters)
            {
                if (!_seenLetters.Add(letter.ID)) continue;
                string text = letter is ChoiceLetter choice ? choice.Text.Resolve().StripTags() : string.Empty;
                LiveEvents.Emit("letter", new JObject
                {
                    ["label"] = letter.Label.Resolve().StripTags(),
                    ["kind"] = letter.def?.defName,
                    ["text"] = Trim(text, 200),
                });
            }
        }

        private static void Enqueue(string type, JObject payload)
        {
            int tick = _tick;
            var line = new JObject
            {
                ["v"] = 1,
                ["type"] = type,
                ["tick"] = tick,
                ["day"] = Math.Round((tick - _startTick) / 60000.0, 2),
                ["ts"] = DateTime.UtcNow.ToString("o"),
            };
            foreach (var prop in payload.Properties()) line[prop.Name] = prop.Value;
            Pending.Enqueue(line.ToString(Formatting.None));
        }

        private void Flush()
        {
            if (_path == null || Pending.IsEmpty) return;
            var sb = new StringBuilder();
            while (Pending.TryDequeue(out var line)) sb.Append(line).Append('\n');
            try
            {
                File.AppendAllText(_path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimMind-Live] write failed: {ex.Message}");
            }
        }

        private static string SourceOf(string? npcId) =>
            npcId != null && npcId.StartsWith("colony-", StringComparison.Ordinal) ? "overseer" : "agent";

        private static string? PawnName(int pawnId)
        {
            if (pawnId <= 0) return null;
            var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(p => p.thingIDNumber == pawnId);
            return pawn?.Name?.ToStringShort ?? pawn?.LabelShort;
        }

        private static float? Round(float? value) => value.HasValue ? (float?)Math.Round(value.Value, 2) : null;

        private static string Trim(string? s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s!.Length > max ? s.Substring(0, max) + "…" : s);
    }
}
