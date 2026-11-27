# Live event stream (`events.ndjson`) — contract v1

Producer: RimMind-Core `LiveEventStream` (in game). Consumer: `tools/dashboard`.
One JSON object per line, UTF-8, append-only. Unknown fields must be ignored by consumers; new event types may appear.

## Location

- `$RIMMIND_LIVE_DIR/events.ndjson` when the environment variable is set (the playthrough script sets it to the run
  folder `.playthrough/runs/<id>/`).
- Otherwise `<RimWorld save data>/RimMind/Live/<session>/events.ndjson`, and
  `<RimWorld save data>/RimMind/Live/current.txt` holds the absolute path of the active file.

## Envelope (every line)

| Field | Type | Meaning |
|---|---|---|
| `v` | int | contract version, `1` |
| `type` | string | event type (below) |
| `tick` | int | `Find.TickManager.TicksGame` |
| `day` | number | in-game days since session start, 2 decimals |
| `ts` | string | wall-clock UTC, ISO-8601 |

## Event types

### `session_start`
`{ map_id, map_size: [w, h], biome, colony, model, run_id|null, speed|null }`

### `snapshot` — every in-game hour (2500 ticks)
```json
{
  "colony": { "alive": 3, "downed": 0, "mood": 0.62, "food_days": 8.4, "wealth": 14100, "research_done": 8,
              "active_bills": 1, "pending_construction": 6, "buildings": 22, "hostiles": 0, "fires": 0,
              "outdoor_temp": 18, "speed": "Fast", "paused": false },
  "colonists": [ { "id": 855, "name": "Oscar", "x": 140, "z": 118, "mood": 0.7, "food": 0.4, "rest": 0.8,
                   "health": 1.0, "downed": false, "mental": null, "job": "Construct", "agent": "Active",
                   "drafted": false } ]
}
```

### `geography` — at session start, then when it changes (at most once per in-game hour)
```json
{
  "map_id": 0, "width": 250, "height": 250, "block": 10, "cols": 25, "rows": 25,
  "grid": ["row z-max … (string of cols symbols)", "…", "row z-min"],
  "legend": { "~": "water", "^": "mountain/rock", "\"": "rich soil", ",": "soil", ".": "poor soil/sand",
              "m": "marsh/mud", "T": "trees", "$": "ore", "?": "unexplored", "#": "base buildings",
              "S": "stockpile", "G": "growing zone", "@": "colonist" },
  "pois": [ { "kind": "base", "label": "Base center", "x": 158, "z": 102, "size": 0 },
            { "kind": "fertile", "label": "Fertile soil", "x": 136, "z": 112, "size": 140 } ],
  "text": "the same summary the models receive"
}
```
`grid[0]` is the northern-most row (largest z). Block `(col, row)` covers x ∈ [col·block, (col+1)·block), z from the top.

### `decision` — a colonist agent, the Advisor or the overseer decided
`{ "source": "agent|advisor|overseer", "npc": "NPC-855|colony-0", "pawn": "Oscar|null", "action": "...", "reason": "..." }`

### `action` — a tool / mechanism call finished
`{ "source": "agent|advisor|overseer", "npc": "...", "pawn": "...|null", "tool": "actions_manage_zone",
   "args": "{...}", "ok": true, "result": "first 300 chars" }`

### `llm` — an LLM request completed
`{ "source": "mod:RimMind.Agent | scenario:agent_decision | npc:NPC-855", "model": "beam-free", "ok": true,
   "latency_ms": 4100, "prompt_tokens": 3200, "cached_tokens": 2880, "completion_tokens": 310, "error": null }`

### `letter` — a RimWorld letter arrived (raids, deaths, quests, …)
`{ "label": "Raid", "kind": "ThreatBig", "text": "first 200 chars" }`

### `alert` — a colony safety invariant is violated (raised once, then `"resolved": true` when it clears)
`{ "code": "starving_with_food|trapped|unburied_colonist|downed|bleeding|mental_break|no_bed|fire|threat",
   "pawn": "Sheppy", "severity": "critical|warning", "detail": "food 0.00, 36 meals unreachable", "resolved": false }`

### `intervention` — the safety net acted (no LLM involved)
`{ "code": "trapped", "pawn": "Sheppy", "action": "deconstruct_blocker", "target": "PassiveCooler@(118,108)",
   "ok": true, "detail": "path to stockpile restored" }`

### `session_end`
`{ "reason": "completed|quit" }`
