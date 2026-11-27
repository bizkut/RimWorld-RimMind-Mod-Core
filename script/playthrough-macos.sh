#!/usr/bin/env bash
# Unattended RimMind playthrough on macOS: build Core + submodules (staged copies; checkouts untouched), install them,
# boot a fresh -quicktest colony and let TenDayPlaythroughRunner play N days, then collect the JSON report + Player.log.
#
# usage: script/playthrough-macos.sh [DAYS=3] [TIMEOUT_MIN=60]
# env:   RIMWORLD_DIR      default /Applications/RimWorld.app
#        MODULES           default "Core Actions Advisor" (sibling checkouts named RimMind-<M> or RimWorld-RimMind-Mod-<M>)
#        ENDPOINT MODEL API_KEY   OpenAI-compatible endpoint (base URL ending in /v1), model, key (placeholder ok if keyless)
#                          Default key "oc_sk_local-proxy": the oc_sk_ prefix makes OpenCodeHeaderDecorator send
#                          x-opencode-session: rimmind-<NpcId>, one session per pawn, through a local OpenCode proxy
#                          (which substitutes its own upstream key). Without it the proxy guesses sessions from the
#                          prompt and every PawnAgent shares one session.
#        MAX_TOKENS        default 4096 (reasoning models spend small budgets thinking)
#        SPEED             Normal|Fast|Superfast|Ultrafast (RIMMIND_PLAYTHROUGH_SPEED; default Fast)
#        HARMONY_DIR       Harmony mod folder to install if Mods/Harmony is missing (e.g. a HarmonyRimWorld checkout)
#        CONTROL=1         no-AI baseline: no PawnAgents activated (combine with MODULES=Core for no Advisor/Actions)
#        ALL_AGENTS        default 1: every colonist gets a PawnAgent; 0 keeps the runner's one-agent-less split
#        OVERSEER          default 1: enable the Advisor colony overseer (writes Mod_RimMind-Advisor settings); 0 = off
# output: .playthrough/runs/<run-id>/ (report JSON, chronicle, Player.log); per-day colony stats printed at the end
set -euo pipefail

DAYS=${1:-3}
TIMEOUT_MIN=${2:-60}
CORE=$(cd "$(dirname "$0")/.." && pwd)
PARENT=$(dirname "$CORE")
RW=${RIMWORLD_DIR:-/Applications/RimWorld.app}
MODULES=${MODULES:-"Core Actions Advisor"}
ENDPOINT=${ENDPOINT:-http://127.0.0.1:8787/v1}
MODEL=${MODEL:-beam-free}
API_KEY=${API_KEY:-oc_sk_local-proxy}
MAX_TOKENS=${MAX_TOKENS:-4096}
SPEED=${SPEED:-Fast}
CFG="$HOME/Library/Application Support/RimWorld/Config"
LOG="$HOME/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"
WORK="$CORE/.playthrough"
RUN_ID="pt-$(date +%Y%m%d-%H%M%S)-$( [ "${CONTROL:-0}" = 1 ] && printf control || printf '%s' "$MODEL" | tr -c 'A-Za-z0-9.-' '_')"
OUT="$WORK/runs/$RUN_ID"

say() { echo "[playthrough] $*"; }
module_dir() {
    for d in "$PARENT/RimMind-$1" "$PARENT/RimWorld-RimMind-Mod-$1"; do [ -d "$d/Source" ] && { echo "$d"; return; }; done
    say "module $1 not found next to $CORE"; exit 1
}

curl -sf -m 10 "$ENDPOINT/models" >/dev/null || { say "endpoint $ENDPOINT not reachable"; exit 1; }
pkill -f "$RW/Contents/MacOS/" 2>/dev/null && sleep 3 || true

# 1. Stage + build. Submodules reference Core via ../../RimMind-Core/1.6/Assemblies, so staged folders use that layout.
mkdir -p "$WORK/build" "$OUT"
for m in $MODULES; do
    rsync -a --delete --exclude .git --exclude bin --exclude obj --exclude 1.6 --exclude 1.7 --exclude .playthrough \
        "$(module_dir "$m")/" "$WORK/build/RimMind-$m/"
done
for m in $MODULES; do
    proj=$(ls "$WORK/build/RimMind-$m/Source/"*.csproj | head -1)
    say "building RimMind-$m"
    # No node reuse / shared compiler: otherwise MSBuild and VBCSCompiler daemons outlive the build.
    if ! dotnet build "$proj" -c Release -nologo -v q -p:GameVersion=1.6 -nodeReuse:false -p:UseSharedCompilation=false \
            > "$WORK/build/build-$m.log" 2>&1; then
        grep -E "error" "$WORK/build/build-$m.log" | sort -u | head -20; say "build failed: RimMind-$m"; exit 1
    fi
done

# 2. Install mod content into Mods/RimMind-<M>; Harmony must be present.
for m in $MODULES; do
    mkdir -p "$RW/Mods/RimMind-$m"
    rsync -a --delete --include "/About/***" --include "/Defs/***" --include "/Languages/***" --include "/Textures/***" \
        --include "/1.6/***" --include "/LoadFolders.xml" --exclude "*" "$WORK/build/RimMind-$m/" "$RW/Mods/RimMind-$m/"
done
if [ ! -f "$RW/Mods/Harmony/1.6/Assemblies/0Harmony.dll" ]; then
    [ -n "${HARMONY_DIR:-}" ] || { say "Harmony not installed; set HARMONY_DIR"; exit 1; }
    mkdir -p "$RW/Mods/Harmony"
    rsync -a --delete "$HARMONY_DIR/About" "$HARMONY_DIR/1.6" "$HARMONY_DIR/LoadFolders.xml" "$RW/Mods/Harmony/"
fi

# 3. Active mods: Harmony, official content, RimMind modules (nothing else). Original saved once as .bak-playthrough.
[ -f "$CFG/ModsConfig.xml.bak-playthrough" ] || cp "$CFG/ModsConfig.xml" "$CFG/ModsConfig.xml.bak-playthrough"
PACKAGES=$(for m in $MODULES; do grep -oE "<packageId>[^<]+" "$WORK/build/RimMind-$m/About/About.xml" | head -1 | sed 's/<packageId>//'; done)
python3 - "$CFG/ModsConfig.xml" $PACKAGES <<'PY'
import sys, xml.etree.ElementTree as ET
path, mods = sys.argv[1], [m.lower() for m in sys.argv[2:]]
tree = ET.parse(path)
active = tree.getroot().find("activeMods")
official = [li.text.strip().lower() for li in active.findall("li") if li.text.strip().lower().startswith("ludeon.")]
for li in list(active):
    active.remove(li)
for i in ["brrainz.harmony"] + official + mods:
    ET.SubElement(active, "li").text = i
tree.write(path, encoding="utf-8", xml_declaration=True)
print("[playthrough] activeMods:", ", ".join(["brrainz.harmony"] + official + mods))
PY

# 4. maxTokens is not overridable by env: preset it (and the endpoint, for the UI) in Core settings.
KEY=$(python3 -c 'import base64,sys;print(base64.b64encode(bytes(b^0x5A for b in sys.argv[1].encode())).decode())' "$API_KEY")
cat > "$CFG/Mod_RimMind-Core_RimMindCoreMod.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
  <ModSettings Class="RimMind.Presentation.Settings.RimMindCoreSettings">
    <provider>openai</provider>
    <apiKey>$KEY</apiKey>
    <apiEndpoint>$ENDPOINT</apiEndpoint>
    <modelName>$MODEL</modelName>
    <maxTokens>$MAX_TOKENS</maxTokens>
    <requestTimeoutMs>120000</requestTimeoutMs>
    <debugLogging>True</debugLogging>
  </ModSettings>
</SettingsBlock>
XML
if [[ " $MODULES " == *" Advisor "* ]]; then
    cat > "$CFG/Mod_RimMind-Advisor_RimMindAdvisorMod.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
  <ModSettings Class="RimMind.Advisor.Settings.RimMindAdvisorSettings">
    <enableColonyOverseer>$([ "${OVERSEER:-1}" = 1 ] && echo True || echo False)</enableColonyOverseer>
  </ModSettings>
</SettingsBlock>
XML
fi

# 5. Launch through LaunchServices (`open`); exec'ing the binary from a background shell hangs at boot.
#    The game is found again by its unique run id; the runner auto-exits when done.
START=$(date +%s)
say "run $RUN_ID: $DAYS day(s) at $SPEED, $MODEL via $ENDPOINT, control=${CONTROL:-0}, timeout ${TIMEOUT_MIN}m"
open -n -a "$RW" \
    --env "RIMMIND_TEST_ENDPOINT=$ENDPOINT" --env "RIMMIND_TEST_MODEL=$MODEL" --env "RIMMIND_TEST_API_KEY=$API_KEY" \
    --env "RIMMIND_PLAYTHROUGH_OUTPUT_DIR=$OUT" --env "RIMMIND_PLAYTHROUGH_SPEED=$SPEED" --env "RIMMIND_PLAYTHROUGH_AUTO_EXIT=1" \
    --env "RIMMIND_PLAYTHROUGH_CONTROL=${CONTROL:-0}" --env "RIMMIND_PLAYTHROUGH_ALL_AGENTS=${ALL_AGENTS:-1}" \
    --env "RIMMIND_LIVE_DIR=$OUT" \
    --args -quicktest "-rimmind-playthrough=$RUN_ID" "-rimmind-playthrough-days=$DAYS"
# RimWorld does not start -quicktest until its window has been activated once. `open -a` (no -n) activates the
# running instance through LaunchServices; unlike System Events it needs no Accessibility permission.
sleep 10
open -a "$RW"
sleep 5
while pgrep -f -- "-rimmind-playthrough=$RUN_ID" >/dev/null; do
    if [ $(( $(date +%s) - START )) -ge $(( TIMEOUT_MIN * 60 )) ]; then
        say "timeout; stopping game"; pkill -f -- "-rimmind-playthrough=$RUN_ID" || true; break
    fi
    sleep 10
done
sleep 2
cp "$LOG" "$OUT/Player.log"

# 6. Summary.
say "output: $OUT"
grep -E "\[RimMind-Playthrough\] Status" "$OUT/Player.log" | tail -1 || true
say "exceptions: $(grep -c "Exception" "$OUT/Player.log" || true)"
say "overseer: $(grep -c '\[RimMind-Overseer\] map .*: deciding' "$OUT/Player.log" || true) decisions, $(grep -cE '\[RimMind-Overseer\] map .* → ok' "$OUT/Player.log" || true) ok / $(grep -cE '\[RimMind-Overseer\] map .* → FAILED' "$OUT/Player.log" || true) failed tool calls"
REPORT=$(ls "$OUT"/playthrough-"$DAYS"days-report.json 2>/dev/null | head -1)
if [ -n "$REPORT" ]; then
    python3 - "$REPORT" <<'PY'
import json, sys
r = json.load(open(sys.argv[1], encoding="utf-8-sig"))  # the runner writes a UTF-8 BOM
print(f"{'day':>3} {'alive':>5} {'down':>4} {'mood':>5} {'food':>5} {'wealth':>7} {'res':>3} {'bills':>5} {'build':>5} {'host':>4} {'acts':>4} {'llm':>4}")
for d in r.get("Days", []):
    c = d.get("Colony") or {}
    print(f"{d['DayNumber']:>3} {c.get('ColonistsAlive',0):>5} {c.get('ColonistsDowned',0):>4} {c.get('AverageMood',0):>5.2f} "
          f"{c.get('FoodDays',0):>5.1f} {c.get('Wealth',0):>7.0f} {c.get('ResearchCompleted',0):>3} {c.get('ActiveBills',0):>5} "
          f"{c.get('PendingConstruction',0):>5} {c.get('Hostiles',0):>4} {d.get('AgentActionsExecutedCount',0):>4} {d.get('LlmCallsCount',0):>4}")
PY
fi
