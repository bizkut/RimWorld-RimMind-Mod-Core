# RimMind dashboard

From the repository root, run `python3 tools/dashboard/server.py` and open <http://127.0.0.1:8790/>. Python 3's standard library is sufficient. The server binds only to localhost. Use `--port`, `--runs`, or `--live` to override the port, `.playthrough/runs`, or the macOS RimWorld live directory. To try the included synthetic stream:

```sh
python3 tools/dashboard/server.py --runs tools/dashboard/sample/runs --port 8791
```

Choose a run and optionally a comparison; the newest control run is selected by default. The header shows the model, game time, status, and recent stream rate. Vital-sign charts show snapshots over game days, with a dashed comparison; exit badges use the latest current-run snapshot. Colonist cards, filtered decisions/actions, tool results, LLM latency/token/cache statistics grouped by mod, geography with colonist/POI markers, screenshots, and recent letters use available event data. Missing values are shown as unknown, not zero.

The server replays and tails contract-v1 `events.ndjson` files and resolves the live session through `current.txt`. For older runs it synthesizes a session, daily snapshots, and agent actions from the BOM-encoded playthrough report and `day-NN-state.json`; screenshots are served separately. Legacy food days are estimated from available nutrition at 1.6 nutrition per colonist per day when the report has no colony value. Legacy files cannot provide geography, precise positions, individual LLM requests, or letters. LLM token counts are usage counts, not monetary costs: the contract has no price information.
