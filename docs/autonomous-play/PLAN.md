# Autonomous play — plan and tracker

**Goal:** start a colony, leave the keyboard, and the LLM keeps it alive and growing (food, shelter, production,
research, defense) with no player input.
**Where:** forks `bizkut/RimWorld-RimMind-Mod-Core`, `bizkut/RimWorld-RimMind-Mod-Advisor` (and next
`bizkut/RimWorld-RimMind-Mod-Actions`), branch `autonomous-play`. Other modules (Memory, …) are used unmodified.
**Model:** `beam-free` (reasoning) via the local OpenCode proxy `http://127.0.0.1:8787/v1`.
**Status keys:** ✅ done · 🔄 in progress · ⏭ next · 📋 backlog

## How to run and measure

```sh
script/playthrough-macos.sh DAYS TIMEOUT_MIN                  # AI run: Core+Actions+Advisor, every colonist has an agent
CONTROL=1 MODULES=Core SPEED=Ultrafast script/playthrough-macos.sh 10 50   # no-AI baseline, same runner
```

- Output: `.playthrough/runs/<id>/` (report JSON, day states, screenshots, chronicle, Player.log) and a per-day table:
  alive, downed, mood, food-days, wealth, research, active bills, pending construction, hostiles, actions, LLM calls.
- Speed: Fast ≈ 5.5 min per in-game day; Ultrafast ≈ 1.5 min (use for the control only — LLM latency).
- Tests: `DOTNET_ROLL_FORWARD=Major dotnet test Tests/RimMindCore.Tests.csproj` (net10 on .NET 11). `main` already
  fails 24 unit + 2 arch tests on this machine; compare failing names (`--logger trx`), not counts.

## Baselines

| Run | Result |
|---|---|
| No-AI control, 10 days (`pt-20261127-131328-control`) | colony lost: 1 dead day 7, food 0 day 9, nobody left day 10; never any bills/construction/research |
| AI, 3 days, old parser (`pt-20261127-113652`) | mood ~0.9 vs control ~0.5, 74 actions (64 ok); no bills/construction |
| AI + overseer, 1 day (`pt-20261127-150450`) | overseer: 2 decisions, 7 calls (4 ok); harvest 12 + chop 25 ok; grow zone ×2 and shelter room failed (placement anchor bug) |
| AI + overseer, 1 day, placement fix (`pt-20261127-*` after Actions `255cf52`) | overseer: 3 decisions, 12 calls (10 ok): 46-cell rice zone, 5×5 shelter room (built same day), stockpile, stove + bed blueprints, work priorities for all 3; failed: `Haul` designation (unsupported), cooking bill before a stove existed. Agents: 14 decisions, 2 failed (`eat`, `PlantWork` — now aliased) |
| AI + overseer + agents, 10 days (`pt-20261127-154931-beam-free`) | ❌ **worse than control**: Sheppy starved day 5 with 24–36 meals stored (sealed in his bedroom, R2), Urshed died day 6 after his agent drafted him "to confront" hostile drifters; both left unburied; Betty alone. Run **stalled on day 8**: a "Transport pod crash: Rin" quest dialog paused the game and nothing could answer it (now fixed, below). Built: 2 bedrooms + shelter, 2 rice fields, stockpile, stove + bill, coolers; 0 research chosen |

## Review (2026-11-27) — what needs improvement

Evidence: the 10-day run above, its Player.log, day states and screenshots.

| # | Problem | Evidence | Fix (workstream) |
|---|---|---|---|
| R1 | **Nobody notices a dying colonist.** Starvation alert for 2 in-game days, nothing reacted. | Sheppy food 0.00 on days 3–4 with 36/30 meals stored; "Starvation" alert both days | **Colony safety net** (deterministic, no LLM, every 250 ticks): invariants below; act first, then tell the overseer/agent. Highest priority. |
| R2 | **Buildings can trap colonists.** No placement checks reachability; nothing knows doors/paths. | Sheppy sealed in Bedroom@(119,111) (area *Unrestricted*; `eat_food: no available food found`). `PlanRoomProjectCompositeTool` always puts the door mid-south (`minX + size/2, minZ`) → door (119,109); the only cell outside it, (119,108), is the north wall of Bedroom@(121,106) (x 119–123, z 104–108) placed earlier by Urshed's agent. The door opens into a wall. (Passive coolers at (118,108)/(113,106) were not the cause.) | **Room plans keep their door usable**: reserve the cell(s) outside the door (and a one-cell gap or shared-wall rule with neighbouring rooms), choose the door side facing open/connected ground, and run a door-exit reachability test before placing. **Placement validation** for every building: refuse a placement that blocks an existing door or the only path; after each build check every colonist ↔ stockpile/food ↔ bed ↔ map edge. **Structure geometry for everyone** (base plan view below). Trapped colonist = safety-net alarm (cancel/deconstruct the blocker). |
| R3 | **Tools report OK without effect.** | `actions_eat_and_recreation` OK ~10× for Sheppy while he never ate. Cause: Actions `EatAndRecreationCompositeTool` in `auto` mode falls back to `force_rest` when `eat_food` fails and returns `IsError = rest.IsError`, masking the eat failure and its reason | Composite reports each step (eat failed: reason, rest ok) and is an error when the requested/needed step failed. **Outcome verification** in general: success = the job started *and* the effect is observed (need rose, item built, zone exists) within N ticks; the real outcome goes back to the agent and into metrics. |
| R4 | **Agents loop on the wrong need.** | Sheppy's agent called the composite with explicit `mode:"rest"` for comfort while food was falling to 0; the composite obeys `mode` without checking hunger; repeated identical eat calls after a failure | **Hard clamp** in the composite: food critical (< 15%) → `rest` refused, eat forced (reason returned). **Need hierarchy + repeat guard**: survival (food, bleeding, threat) before comfort/recreation in agent rules; same action ×3 without effect → escalate (overseer/safety net) and forbid repeating it. |
| R5 | **Bad combat calls.** | Urshed drafted "to confront" several hostile drifters and died | **Threat assessment**: compare strength (count, weapons, health) before engaging; default flee to shelter; colony-level defense belongs to the overseer (draft all, defensive position), not single agents. |
| R6 | **Colony gaps the overseer leaves open.** | "Unburied Corpses" triggered a decision but nothing was done (grave blocked by the 2-blueprint limit); no research ever selected; "Need defenses" all run; cooking had "no raw ingredients"; `ButcherTable` guessed wrong | **Overseer phase 2**: burial/corpse handling, research tool, defenses, food chain (hunt → butcher → cook); priority lanes in the blueprint limit (graves, beds, doors, food never blocked); resolve def names from the buildable list. |
| R7 | **Tool calls still lost as `dialogue.free`.** | NPC-608/612: `{"tool_call":{…}}`, `{"tool_calls":[…]}`, `{"name","arguments"}`, bare args object | Extend reply-shape normalisation; count `dialogue.free` containing a tool name as a parser failure in metrics. |
| R8 | **Run is only judged at the end.** | I found the death by reading logs after the fact | **Watchdog**: same invariants as R1 evaluated outside the game on the live event stream (dashboard alert panel + a monitor that notifies the operator); a postmortem per death (cause chain from events). |
| R9 | **Exit criteria too weak.** | criteria "bills > 0, construction > 0" passed while 2 of 3 died | Score: deaths (and cause), days survived, starvation hours, trapped hours, unburied hours, research, wealth; several seeds per change. |
| R10 | **The overseer is blind to one dying colonist.** | `ColonyMacroPerceiver` flags food only colony-wide (`IsFoodCritical`: < 3 days or < 5 nutrition); its L2 said "53.2 nutrition (~11.1 days)" while Sheppy starved; bottlenecks were No Agriculture / Unburied Corpses / Shelter. Day 3–5 overseer food actions: harvest/hunt designations only — nothing about Sheppy | **Per-colonist critical-needs bottleneck** (food/rest/health/trapped, with the reason) in the macro state; **pawn → overseer escalation** after repeated failed survival actions; food figure counts only food the colonists can reach and accept (policy, precepts, genes). A colonist death is a first-class run failure with an automatic postmortem (needs, actions, alerts, positions in the 2 days before). |

Safety-net invariants (R1, R2, R8): colonist food < 15% while reachable food exists → force eat; colonist cannot reach food/bed/stockpile → find the blocker (blueprint, building, closed door) and cancel/deconstruct it; corpse of a colonist > 6 h → grave + bury (bypasses the blueprint limit); downed/bleeding → rescue + tend; mental break / no bed / fire / hostiles near a weak colonist → alert + standard response. Every intervention is logged and sent as an event (`alert`, `intervention`).

**Structure geometry (R2): every model sees it.** The geography overview (whole map, ~10×10-cell blocks) is too coarse for doors. Add a **base plan view** next to it: the base bounding box at 1 cell per character — walls `#`, doors `D`, beds `b`, furniture/blocking `x`, blueprints `+`, stockpile `S`, growing `G`, colonists by initial — plus per-room facts (doors, connected to outside yes/no, colonists inside). Shared by colonists' agents, the Advisor and the overseer (stable L1 part; rebuilt when buildings change), and shown on the web UI map.

## Done

| ✅ | Change | Repo / commit | Evidence |
|---|---|---|---|
| ✅ | Decision parser accepts object `param` | Core `98423bd` | 27/31 decisions had degraded to `dialogue.free` |
| ✅ | Reply shapes: `wait`→no-op, flat args, `tool_name`, `tool_calls` wrapper | Core `9e2627a` | all 17 `dialogue.free` were lost tool calls |
| ✅ | Advisor honours configured max tokens (`RimMindAPI.Settings.MaxTokens`) | Core `7da7c84`, Advisor `e684113` | 24 empty Advisor replies → 0 |
| ✅ | Playthrough: colony outcome stats, no-AI control mode, speed override | Core `113fde2`, `2959fbd` | control run above |
| ✅ | macOS runner `script/playthrough-macos.sh` (open --env, window activation, no build daemons) | Core `76829fe`, `0163ea5`, `a4129a7` | unattended runs complete |
| ✅ | Per-pawn OpenCode sessions (`oc_sk_` placeholder key → `x-opencode-session: rimmind-<NpcId>`) | Core `a4129a7` | relay log: one session per pawn |
| ✅ | Cache-friendly prompt layout (volatile L2/L3/L5 → final user turn) | Core `052241b` | cached share 13–31% → 51% |
| ✅ | Nested tool arguments unwrapped; work types resolved by skill name | Core `b2508a1` | 2/10 failing decisions → 0 |
| ✅ | Every colonist gets an agent (`RIMMIND_PLAYTHROUGH_ALL_AGENTS=1`, script default) | Core `8ba5d2f` | 3/3 agents active |

## Colony overseer

### Audit (2026-11-27) — what already exists

No colony-level decision loop exists in any module. Reuse map:

| Need | Existing code | Verdict |
|---|---|---|
| Colony assessment | Advisor `Perception/ColonyMacroPerceiver` (food-days, temperature extremes, beds vs colonists, corpses, threats, wood/steel, grow-zone cells, bottleneck list + prompt text); Core `Presentation/Agent/MapContextBuilder.BuildStaticMapTopology` (rooms, beds, worktables) and `BuildVolatileColonyResources` (wealth, food, resources, pending construction, bills) | reuse as-is |
| Colony actions | Actions tools, colony-wide with optional `pawn_id`: `actions_plan_room_project`, `actions_place_blueprint`, `actions_query_buildable_structures`, `actions_add_worktable_bill`, `actions_manage_zone` (grow + stockpile), `actions_designate_resources`; Core `pawn.work` for priorities | reuse as-is |
| Tool selection | `RimMindAPI.ToolSet.GetDefinitionsForScope(ToolScope.ColonyBuild \| ToolScope.ColonyManage, compactSchema: true)` | reuse as-is |
| Execution | Advisor `AdvisorToolCallExecutor` + `AdvisorToolCallBatchRunner.ExecuteOnMainThread` (non-pawn caller id → `PawnId` null; Actions `ActionPermissionGuard` resolves map / falls back to a standing colonist) | reuse as-is |
| Multi-round tool feedback | Advisor `AdvisorFeedbackSession` + assistant/tool message pattern in `AdvisorTaskDriver.RequestToolFeedback` | reuse pattern |
| Response parsing | Advisor `AdvisorRecommendationParser.TryParseNative`; Core `LlmPayloadParser.ParsePseudoFunctionCalls` for tool calls written as text | reuse as-is |
| Requests without a pawn | `RimMindAPI.Request.Send` (callbacks on main thread via `RequestCompletionInbox`); envelope with prebuilt messages skips pawn context build; npc id `colony-<mapId>` → session `rimmind-colony-<mapId>` | reuse as-is |
| Scenario | `RimMindAPI.Context.RegisterScenario` — no `colony_decision` yet | add registration |
| Cache-friendly layout | `OpenAIWireCodec` 4-zone layout (L0/L1 static, L2 volatile → final user turn) | reuse as-is |
| Persisted colony plan | none (Memory module stores are per pawn / narrative) | **new**, small |
| Cadence / bottleneck trigger | none (Advisor: per-pawn scan, random macro gate) | **new** |
| Research project selection | none | **gap** — new tool, phase 2 |
| Colony-level autonomy owner | autonomy policy is per pawn only | **gap** — overseer gated by an Advisor setting for now |
| `ScopedAgent` / `ScopedAgentManager` (Core) | generic non-pawn agent shell: no context, no tools, no action dispatch, 300-tick cadence, only created by a debug window | not used (would need all of the above) |
| `PlaythroughPlayerSimulation` / `ColonySubagentCommand` | harness that simulates player actions (unforbid, plan housing) | not an agent; keep its effects out of overseer metrics |

### Design (phase 1)

Lives in **RimMind-Advisor** (owns the macro perceiver, executor, feedback pattern); no Core changes.

- `ColonyOverseerGameComponent`: per player-home map, every 250 ticks evaluate `ColonyMacroPerceiver`; run when
  6 in-game hours passed or a new bottleneck appeared (min gap 1 in-game hour); one request in flight per map;
  gated by Advisor setting `enableColonyOverseer` (default off) and Core `IsConfigured`.
- Request: scenario `colony_decision`, npc id `colony-<mapId>`; messages L0 overseer rules + phase guidance (static),
  L1 map topology (rarely changes), L2 macro state + colony resources + colonist skills + plan memory (volatile);
  colony-scope tools; up to 2 feedback rounds so it can query, then act.
- Execution through the Advisor executor with player authority (setting is the consent); every call and result is
  logged `[RimMind-Overseer]` and appended to the plan memory.
- Plan memory (persisted, per map): last run tick, last bottlenecks, last 20 actions with results.
- Phase 2: research-project tool, phase tracking, deterministic safety net (grow zone / stockpile / beds),
  colony-level autonomy owner.

**Status:** ✅ phase 1 implemented — Advisor `a49ac7d` (+ test stub fix `0e1e3a7`), script `b3e1ef8`; Advisor tests
50/50 (4 new); first in-game run above. The loop, triggers, session, tools, execution and feedback work; the model
adapted after failures (smaller zone → room → designations).

**Exit:** 10-day AI run vs control — no starvation, active bills > 0, construction > 0.

## Next

1. ✅ **Placement anchor fix** — Actions `255cf52` (`MapSpatialSnapshotBuilder.ResolvePlacementOrigin`: base →
   acting colonist → colonists → map center); Actions tests 104/104.
2. ✅ **Overseer turn budget** — Advisor `9d365ae`: a query-only turn earns one extra feedback round.
3. ✅ **Aliases** — Core `2f3fcd3`: bare `eat`/`draft`/`undraft`, work type `PlantWork`/`farming` → Growing.
4. ✅ **10-day AI run** — done (Baselines; stalled day 8 on a popup); review R1–R10 above.
5. ✅ **Map geography** (Core provider `map_geography` in L1 for colonists/Advisor, overseer L1) and **Web UI**
   (`events.ndjson` exporter + `tools/dashboard`) — verified in game (`pt-20261127-170313`): 25×25 grid + points of
   interest, all event types streamed.
6. ✅ **Popups answered by the overseer** — Core `world.dialog` mechanism (`PendingChoices`: open event dialogs —
   exactly `Dialog_NodeTree`, never `Dialog_MessageBox` confirmations or specialised subclasses — and decision letters;
   option chosen through the option's own handler, so Accept / Reject / Jump to location / Postpone all work) → tools
   `world_dialog_query` / `world_dialog_trigger` (world-admin scope: overseer only). The overseer checks every real
   second (also while a popup pauses the game), lists pending choices in L2, answers within 2 decisions or closes the
   popup so the game never stays frozen; a stuck request is released after 5 real minutes. Verified in game: "Wanderer
   joins" letter → Accept → colonist joined; an open, game-pausing joiner dialog → Accept → game ticking again.
   Runner no longer force-closes event dialogs while the overseer is on.
7. ✅ **Interim room fix (R2)** — Actions `PlanRoomProjectCompositeTool`: every room keeps a free walkable one-cell
   ring (rooms never share walls) and its door goes on the side facing the base whose outside cell is reachable.
   Full layout still comes from the master plan.
8. ✅ **P0 Colony safety net** (R1, R2, R10) — Core `ColonySafetyNet` (deterministic, every 250 ticks, on while the
   overseer runs): starving (< 15% food) → forced eat; trapped (cannot reach the map edge) → wall/rock between the
   room and open ground marked for deconstruct/mine and the colonist starts on it; downed → nearest doctor-capable
   colonist rescues; bleeding → tend; colonist corpse unburied > 6 h → grave blueprint (bypasses the blueprint
   limit). `alert` / `intervention` events; active alerts trigger an overseer decision and appear in its L2
   (`[colony_alerts]`). Verified in game (`pt-20261127-174850`): sealed + starving colonist freed and fed, killed
   colonist buried. Downed/bleeding paths not yet exercised in game. Watchdog alerts on the dashboard: next.
9. ⏭ **P0 Base plan view** for all models (R2) and **placement validation** (no blocked doors/paths).
10. 🔄 **P1 Outcome verification** (R3), **need hierarchy + repeat guard** (R4), **threat assessment** (R5), parser
    shapes (R7). Done (Actions `d053c1f`): `eat_and_recreation` / `stabilize_rest` refuse rest below 30% food and eat
    instead (with a note); a failed eat is reported, not masked by rest. Seen in the 10-day run: agents ordered rest
    for "comfort 0%" and permanent scars while food fell to 15%. Still open: general effect checks, repeat guard,
    teaching that scars are permanent and comfort comes from furniture, threat assessment, parser shapes.
11. ⏭ **P1 Overseer phase 2** (R6): burial, research tool, defenses, food chain, blueprint priority lanes; quests
    listed only in the Quests tab (accept/decline there — quest-offer letters/popups like "Transport pod crash" are
    already answered through item 6).
12. ⏭ **Colony master plan / base layout** (below) — waiting on three choices; uses the base plan view.
13. ⏭ **Scorecard + multi-seed evaluation** (R9).
14. ⏭ **DLC support** (below): context first, then tools, per DLC.

## Map geography

Today the models only see biome + map size, a rooms/worktables list (`MapContextBuilder.BuildStaticMapTopology`), and
the overseer's query tool returns a local ASCII snapshot (≤ 32 cells around the base: indoor/outdoor/blocked only — no
terrain, fertility, water or ore). Nobody sees the whole map.

- `MapGeographyBuilder` (Core, next to `MapContextBuilder`): ~24×24 overview grid of the map (one symbol per block:
  water, mountain/rock, rich soil, soil, poor/sand, marsh, trees, ore, unexplored; base, colonists, stockpiles,
  growing zones) with coordinates, plus points of interest with coordinates (base center, colonists, largest fertile
  area near the base, largest open flat buildable area, ore clusters, water, mountain edge, map exits, hostiles).
  ~1.5k chars. Text form for prompts, structured form for the web UI map.
- Prompt placement: stable L1 (recomputed about once per in-game day) so prompt caching holds.
- Consumers: new Core context provider `map_geography` (L1, map-scoped) weighted into `Decision`, `agent_decision`,
  `advisor_decision` (colonists + Advisor); the overseer adds it to its own L1.
- Not doing: free target coordinates in placement tools (would scatter buildings). Placement goes through the master
  plan instead.

### Ruins and abandoned structures (next geography step)

Gap (2026-11-27): the overview only marks *colony* buildings (`#`); faction-less structures — ruined walls, abandoned
rooms, ancient shrines/"Ancient danger" complexes — are drawn as plain ground, and no tool can claim or salvage them.

- Geography: new symbol `R` = ruins (non-colony, non-natural buildings); points of interest per ruin cluster: size,
  walls/doors count, whether it encloses a room (roofed? doorway?), salvage value (steel/stone), and danger flag for
  sealed ancient complexes (do not open until the colony can fight).
- Overseer tools: **claim** an abandoned structure (vanilla Claim designation), **salvage** it (deconstruct for
  materials), **repurpose** an enclosed ruin as a first shelter/storeroom (claim walls, add door + bed through the room
  tool), with the same reachability checks as new rooms.
- Master plan: an enclosed, safe ruin near good ground can become the base seed instead of building from scratch.

## Colony master plan / base layout

Problem: `plan_room_project` takes the first clear square around the anchor and `manage_zone` the best open rectangle
near it, so buildings scatter and never share walls (first overseer run: room at (158,102), stockpile (140,122),
fields (136,112)). The overseer decides *what* to build; code decides *where* from a persisted layout.

- Base site chosen once from geography: flat, buildable, unfogged, near colonists, away from water/marsh/best farmland.
- Base block on a grid: fixed-size rooms sharing walls, corridor with doors; districts — bedroom row; kitchen → freezer
  → storage; workshop row; grows row by row. Outside: farm district on the nearest large fertile area, yard (outdoor
  stockpile, power), defense line toward the map entry.
- Plot-based placement: `room_type` → next free plot in its district; reserved plots block other placements; out-of-plan
  placements refused with a reason. Furniture keeps the existing cluster solver inside rooms.
- Plan (plots: built / under construction / free) in the geography context and the web UI map overlay.
- Open choices: room module size (5×5 default), central corridor vs rooms opening onto each other, prefer mountain edge.

## Web UI

Live view of a run or a normal game. Contract: [`EVENTS.md`](EVENTS.md).

1. **Event exporter** (Core): append-only `events.ndjson` — hourly snapshots (colony stats, colonists with positions),
   geography, agent/overseer decisions and tool results, LLM requests (latency, tokens, cache), letters/deaths.
   Written to `RIMMIND_LIVE_DIR` (playthrough sets it to the run folder) or the game's save-data `RimMind/Live`.
2. **Dashboard** (`tools/dashboard`): local server tails event files and streams them to the browser (SSE); replays past
   runs. Panels: run header, colony vital signs (with control-run overlay), exit criteria, colonists, overseer, decision
   feed with per-tool success rates, AI health/cost, live map from the geography grid, daily screenshots, run comparison.
3. **Controls** (later): pause/speed, force overseer decision, player directives, agent toggles, approvals — through a
   command file polled on the main thread; every control logged as a player intervention.

## DLC support

Today (2026-11-27): runs with all DLCs active (Royalty, Ideology, Biotech, Anomaly, Odyssey) complete with 0 exceptions;
none is a dependency. The models see only ideoligion name + memes, a noble title, and genes (`PawnDataExtractor`).
No DLC-specific actions exist. Goal: the overseer and colonists understand and use each DLC's mechanics.

Rules for every DLC item:
- Gate on `ModsConfig.<Dlc>Active`; with the DLC off, nothing is sent to the model and no tool is registered.
- Context first (cheap, safe), then tools; tools go through the existing tool manifest, autonomy domains and
  `HighRiskOperations` for anything irreversible (surgery, gene implanting, rituals with deaths, entity release).
- Colony-level mechanics belong to the overseer; personal ones (psycasts, own needs) to colonist agents.
- Each item is verified in a playthrough with that DLC on, and the base game still runs with it off.

| DLC | Context to add | Tools / actions | Owner |
|---|---|---|---|
| Royalty | titles and their needs/throne-room requirements, psylink level, psyfocus, known psycasts, empire goodwill, active royal quests | cast psycast (targeted), meditate/psyfocus, build throne room/required furniture, accept/decline Royalty quests, permits (call aid/trade) | agents (psycasts, meditation), overseer (throne room, quests, permits) |
| Ideology | precepts that constrain actions (diet, nudity, corpses, trees), roles and their holders, ritual availability/quality, required buildings, slaves | assign ideoligion roles, schedule rituals/ceremonies, build required/altar buildings, follow precepts in bills and work (e.g. no meat), slave handling | overseer; agents follow precept limits |
| Biotech | xenotypes/genes (exists), mechanitor bandwidth and mechs, children/growth moments, pollution, genepacks | form/gestate mechs and assign mech work, child care/learning, implant/combine genes (high-risk), pollution cleanup, gene bank/building | overseer (mechs, genes, pollution), agents (child care) |
| Anomaly | monolith level, containment entities and their strength, dark study progress, active threats (e.g. fleshbeasts, shamblers) | containment cell building and capture/contain, study entity, anomaly research projects, defend/avoid anomaly threats | overseer; agents react to threats |
| Odyssey | gravship status and fuel, landing sites, space/orbital conditions | gravship maintenance, prepare and launch decisions (high-risk) | overseer only |

Order: Ideology (precepts already shape everyday choices and are partly in context) → Biotech (mechs strongly change
colony labour) → Royalty → Anomaly → Odyssey. Exit per DLC: a playthrough with that DLC on where the AI uses at least
one of its mechanics correctly and breaks none of its rules.

## Backlog

- 📋 A popup waits until a running overseer cycle finishes (bounded: requests still run while paused). Preempt the
  cycle or answer popups in a separate short session if the wait matters.
- ✅ Runner modal sweep also runs after 10 real seconds without tick progress, so a force-pausing window the overseer
  does not handle (confirmation boxes, specialised dialogs) can no longer freeze a playthrough.
- 📋 Remaining cache misses: first request per session and occasional cold upstream routing (≈50% now).
- 📋 Advisor and agent of the same pawn share one OpenCode session; split per role if it helps caching.
- 📋 RimAI repo (`Rimworld_AI_Framework`) is frozen; its `docs/research/rimmind-*.md` describe RimMind v0.0.1 and are stale.
- 📋 Playthrough harness `PlaythroughPlayerSimulation` can place housing itself; make sure its actions are excluded
  from overseer metrics if it ever fires (it has not so far).
