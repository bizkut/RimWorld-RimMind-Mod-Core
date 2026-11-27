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
| AI 10-day run | 🔄 running next |

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
4. 🔄 **10-day AI run** with agents + overseer vs the no-AI control (exit criteria above).
5. ⏭ **Phase 2:** research-project tool, plan phases, deterministic safety net, colony-level autonomy owner.

## Backlog

- 📋 Remaining cache misses: first request per session and occasional cold upstream routing (≈50% now).
- 📋 Advisor and agent of the same pawn share one OpenCode session; split per role if it helps caching.
- 📋 RimAI repo (`Rimworld_AI_Framework`) is frozen; its `docs/research/rimmind-*.md` describe RimMind v0.0.1 and are stale.
- 📋 Playthrough harness `PlaythroughPlayerSimulation` can place housing itself; make sure its actions are excluded
  from overseer metrics if it ever fires (it has not so far).
