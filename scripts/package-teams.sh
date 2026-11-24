#!/usr/bin/env bash
# Builds a Teams app .zip from infra/teams/manifest.template.json and a config JSON file.
#
# Usage:
#   bash scripts/package-teams.sh --config .local/customer.json --output .local/teams.zip
set -euo pipefail

CONFIG=""
OUTPUT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    *) echo "Usage: $0 --config <config.json> --output <teams.zip>" >&2; exit 1 ;;
  esac
done
[[ -n "$CONFIG" && -f "$CONFIG" && -n "$OUTPUT" ]] || { echo "Missing --config or --output." >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CFG_DIR="$(cd "$(dirname "$CONFIG")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

jq --slurpfile cfg "$CONFIG" '
  ($cfg[0] + { ssoResource: ("api://botid-" + $cfg[0].agentClientId) }) as $v |
  walk(if type == "string" and test("^\\$\\{\\w+\\}$") then $v[.[2:-1]] else . end)
' "$ROOT/infra/teams/manifest.template.json" > "$WORK/manifest.json"

cp "$CFG_DIR/$(jq -er '.colorIcon' "$CONFIG")" "$WORK/color.png"
cp "$CFG_DIR/$(jq -er '.outlineIcon' "$CONFIG")" "$WORK/outline.png"

mkdir -p "$(dirname "$OUTPUT")"
OUT_ABS="$(cd "$(dirname "$OUTPUT")" && pwd)/$(basename "$OUTPUT")"
rm -f "$OUT_ABS"
(cd "$WORK" && zip -q "$OUT_ABS" manifest.json color.png outline.png)
echo "Wrote $OUTPUT"
