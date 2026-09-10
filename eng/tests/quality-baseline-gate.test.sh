#!/usr/bin/env bash
# Exercises the verify.sh quality-baseline gate's set comparison without invoking the pinned analyzer.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
source "$root/eng/quality-baseline-landing.sh"
row() { printf '{"fingerprint":"%s","ruleId":"%s","path":"%s","symbol":null,"snippetHash":"h","enginePartial":""}' "$1" "$2" "$3"; }
committed="$fixture/committed.json"; candidate="$fixture/candidate.json"
printf '{"schemaVersion":1,"findings":[%s,%s]}\n' "$(row a R001 src/kept.cs)" "$(row b R002 src/resolved.cs)" > "$committed"

printf '{"schemaVersion":1,"findings":[%s,%s]}\n' "$(row b R002 src/resolved.cs)" "$(row a R001 src/kept.cs)" > "$candidate"
out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed")
[ "$out" = 'quality-baseline: unchanged (0 new, 0 resolved)' ] || { echo "FAIL unchanged set: $out"; exit 1; }
printf '%s\n' "$out"

printf '{"schemaVersion":1,"findings":[%s]}\n' "$(row a R001 src/kept.cs)" > "$candidate"
out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed")
[ "$out" = 'quality-baseline: 0 new, 1 resolved (passing; re-pin belongs to land)' ] || { echo "FAIL resolved-only set: $out"; exit 1; }
printf '%s\n' "$out"

printf '{"schemaVersion":1,"findings":[%s,%s,%s]}\n' "$(row a R001 src/kept.cs)" "$(row b R002 src/resolved.cs)" "$(row c VSTHRD002 src/PlantedFinding.cs)" > "$candidate"
if out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed" 2>&1); then
  echo 'FAIL planted finding passed'; exit 1
fi
printf '%s\n' "$out" | grep -F 'quality-baseline: 1 new, 0 resolved' >/dev/null
printf '%s\n' "$out" | grep -F 'ruleId=VSTHRD002 path=src/PlantedFinding.cs' >/dev/null
printf '%s\n' "$out"
echo 'quality-baseline-gate: 3 checks passed (new finding red with rule/path; unchanged green; resolved-only green)'
