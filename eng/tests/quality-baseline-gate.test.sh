#!/usr/bin/env bash
# Exercises the verify.sh quality-baseline gate's set comparison without invoking the pinned analyzer.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
source "$root/eng/quality-baseline-landing.sh"
row() { printf '{"fingerprint":"%s","ruleId":"%s","path":"%s","symbol":null,"snippetHash":"h","enginePartial":""}' "$1" "$2" "$3"; }
large_baseline() {
  node - "$1" "$2" "$3" <<'NODE'
const {writeFileSync} = require('node:fs')
const [file, count, engines] = process.argv.slice(2)
const findings = Array.from({length: Number(count)}, (_, index) => ({fingerprint: `f${index}`, ruleId: 'R', path: `src/${index}.cs`, symbol: null, snippetHash: 'h', enginePartial: ''}))
writeFileSync(file, JSON.stringify({schemaVersion: 1, findings, engines: JSON.parse(engines)}) + '\n')
NODE
}
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

printf '{"schemaVersion":1,"findings":[],"engines":[{"engine":"roslyn","status":"analyzer-error","detail":"analyzer-error"}]}' > "$candidate"
if out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed" 2>&1); then
  echo 'FAIL analyzer-error candidate passed'; exit 1
fi
printf '%s\n' "$out" | grep -F 'quality-baseline: engine roslyn failed (analyzer-error); the comparison is hollow, not a pass' >/dev/null
printf '%s\n' "$out" | grep -F 'quality-baseline: engine roslyn status=analyzer-error detail=analyzer-error' >/dev/null
printf '%s\n' "$out"

large_committed="$fixture/large-committed.json"; large_candidate="$fixture/large-candidate.json"
large_baseline "$large_committed" 2363 '[{"engine":"roslyn","status":"ok","detail":""},{"engine":"eslint","status":"ok","detail":""}]'
large_baseline "$large_candidate" 0 '[{"engine":"roslyn","status":"ok","detail":""},{"engine":"eslint","status":"ok","detail":""}]'
if out=$(quality_baseline_gate_candidate_compare "$large_candidate" "$large_committed" 2>&1); then
  echo 'FAIL wholesale resolved candidate passed'; exit 1
fi
printf '%s\n' "$out" | grep -F 'quality-baseline: resolved 2363 of 2363 looks like an engine failure, not a clean-up; run the tool by hand' >/dev/null
printf '%s\n' "$out" | grep -F 'quality-baseline: engine roslyn status=ok detail=' >/dev/null
printf '%s\n' "$out"

large_baseline "$large_candidate" 2360 '[{"engine":"roslyn","status":"ok","detail":""},{"engine":"eslint","status":"ok","detail":""}]'
out=$(quality_baseline_gate_candidate_compare "$large_candidate" "$large_committed")
[ "$out" = 'quality-baseline: 0 new, 3 resolved (passing; re-pin belongs to land)' ] || { echo "FAIL small resolved set: $out"; exit 1; }
printf '%s\n' "$out"
echo 'quality-baseline-gate: 6 checks passed (new finding red with rule/path; unchanged green; small resolved-only green; analyzer-error red; wholesale resolved red)'
