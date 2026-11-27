# Autonomous play — plan and tracker

**Goal:** start a colony, leave the keyboard, and the LLM keeps it alive and growing (food, shelter, production,
research, defense) with no player input.
**Where:** forks `bizkut/RimWorld-RimMind-Mod-Core` and `bizkut/RimWorld-RimMind-Mod-Advisor`, branch `autonomous-play`.
Other modules (Actions, Memory, …) are used unmodified from `../RimWorld-RimMind-Mod-*`.
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
| AI 10-day run | ⏭ not yet valid — previous attempt stopped (old sessions + one colonist without agent) |

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

## In progress

- 🔄 **Colony overseer — step 1: audit existing code** so nothing is duplicated. Candidates already found:
  `ColonyMacroPerceiver` (Advisor), `CabinetProposal`/`ApprovalSubmissionPipeline` (Advisor),
  `ColonySubagentCommand` (?), `ScopedAgentManager` / `ScopedThinkStrategy` (Core), `StorytellerComp_RimMindDirector`,
  Actions composite tools (`plan_room_project`, `place_blueprint`, `add_worktable_bill`, `manage_zone`,
  `designate_resources`, `query_buildable_structures`) with `ColonySpatialClusterSolver`, autonomy domains
  (`AgentAutonomyPolicy`), Memory module (colony facts). Findings and reuse decision go here before any code.

## Next

- ⏭ **Colony overseer** (design draft, to be revised by the audit): one colony-level loop with its own scenario and
  session (`rimmind-colony`); triggers every ~6 in-game hours + new bottleneck; colony context (macro state, rooms,
  beds, zones, bills, research, skills) in cache-friendly order; colony tools only, full autonomy; persisted colony plan
  (phases shelter → food → storage → power → defense → research); deterministic safety net for basics (grow zone,
  stockpile, beds) if not handled within N hours.
  **Exit:** 10-day AI run vs control: no starvation, bills > 0, construction > 0.
- ⏭ **10-day AI run** with all fixes (after the overseer, or now as the pre-overseer reference).

## Backlog

- 📋 Bare `undraft`/`draft` intents map to `pawn.job` and fail; alias them to the draft mechanism.
- 📋 Remaining cache misses: first request per session and occasional cold upstream routing (≈50% now).
- 📋 Advisor and agent of the same pawn share one OpenCode session; split per role if it helps caching.
- 📋 RimAI repo (`Rimworld_AI_Framework`) is frozen; its `docs/research/rimmind-*.md` describe RimMind v0.0.1 and are stale.
