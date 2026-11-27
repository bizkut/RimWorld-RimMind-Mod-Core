#!/usr/bin/env bash
# Unattended RimMind playthrough on macOS: build Core + submodules (staged copies; checkouts untouched), install them,
# boot a fresh -quicktest colony and let TenDayPlaythroughRunner play N days, then collect the JSON report + Player.log.
#
# usage: script/playthrough-macos.sh [DAYS=3] [TIMEOUT_MIN=60]
# env:   RIMWORLD_DIR      default /Applications/RimWorld.app
#        MODULES           default "Core Actions Advisor" (sibling checkouts named RimMind-<M> or RimWorld-RimMind-Mod-<M>)
#        ENDPOINT MODEL API_KEY   OpenAI-compatible endpoint (base URL ending in /v1), model, key (placeholder ok if keyless)
#        MAX_TOKENS        default 4096 (reasoning models spend small budgets thinking)
#        SPEED             Normal|Fast|Superfast|Ultrafast (RIMMIND_PLAYTHROUGH_SPEED; default Fast)
#        HARMONY_DIR       Harmony mod folder to install if Mods/Harmony is missing (e.g. a HarmonyRimWorld checkout)
# output: .playthrough/runs/<run-id>/ (report JSON, chronicle, Player.log)
set -euo pipefail

DAYS=${1:-3}
TIMEOUT_MIN=${2:-60}
CORE=$(cd "$(dirname "$0")/.." && pwd)
PARENT=$(dirname "$CORE")
RW=${RIMWORLD_DIR:-/Applications/RimWorld.app}
MODULES=${MODULES:-"Core Actions Advisor"}
ENDPOINT=${ENDPOINT:-http://127.0.0.1:8787/v1}
MODEL=${MODEL:-beam-free}
API_KEY=${API_KEY:-sk-local}
MAX_TOKENS=${MAX_TOKENS:-4096}
SPEED=${SPEED:-Fast}
CFG="$HOME/Library/Application Support/RimWorld/Config"
LOG="$HOME/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"
WORK="$CORE/.playthrough"
RUN_ID="pt-$(date +%Y%m%d-%H%M%S)-$(printf '%s' "$MODEL" | tr -c 'A-Za-z0-9.-' '_')"
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
    if ! dotnet build "$proj" -c Release -nologo -v q -p:GameVersion=1.6 > "$WORK/build/build-$m.log" 2>&1; then
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

# 5. Launch through LaunchServices (`open`); exec'ing the binary from a background shell hangs at boot.
#    The game is found again by its unique run id; the runner auto-exits when done.
START=$(date +%s)
say "run $RUN_ID: $DAYS day(s) at $SPEED, $MODEL via $ENDPOINT, timeout ${TIMEOUT_MIN}m"
open -n -a "$RW" \
    --env "RIMMIND_TEST_ENDPOINT=$ENDPOINT" --env "RIMMIND_TEST_MODEL=$MODEL" --env "RIMMIND_TEST_API_KEY=$API_KEY" \
    --env "RIMMIND_PLAYTHROUGH_OUTPUT_DIR=$OUT" --env "RIMMIND_PLAYTHROUGH_SPEED=$SPEED" --env "RIMMIND_PLAYTHROUGH_AUTO_EXIT=1" \
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
grep -E "\[RimMind-Playthrough\] (Status|Day [0-9]+)" "$OUT/Player.log" | tail -15 || true
say "exceptions: $(grep -c "Exception" "$OUT/Player.log" || true)"
ls "$OUT"
