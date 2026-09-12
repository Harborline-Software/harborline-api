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
generated_at=$(node -e 'console.log(new Date(Date.now() - (3 * 60 + 2) * 60 * 1000).toISOString())')
printf '{"schemaVersion":1,"commit":"baseline-fixture-405","generatedAt":"%s","findings":[%s,%s]}\n' "$generated_at" "$(row a R001 src/kept.cs)" "$(row b R002 src/resolved.cs)" > "$committed"

printf '{"schemaVersion":1,"findings":[%s,%s]}\n' "$(row b R002 src/resolved.cs)" "$(row a R001 src/kept.cs)" > "$candidate"
out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed")
[ "$out" = 'quality-baseline: unchanged (0 new, 0 resolved)' ] || { echo "FAIL unchanged set: $out"; exit 1; }
printf '%s\n' "$out"

printf '{"schemaVersion":1,"findings":[%s]}\n' "$(row a R001 src/kept.cs)" > "$candidate"
out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed")
[ "$out" = 'quality-baseline: 0 new, 1 resolved (passing; re-pin is a follow-up PR)' ] || { echo "FAIL resolved-only set: $out"; exit 1; }
printf '%s\n' "$out"

printf '{"schemaVersion":1,"findings":[%s,%s,%s]}\n' "$(row a R001 src/kept.cs)" "$(row b R002 src/resolved.cs)" "$(row c VSTHRD002 src/PlantedFinding.cs)" > "$candidate"
if out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed" 2>&1); then
  echo 'FAIL planted finding passed'; exit 1
fi
printf '%s\n' "$out" | grep -F 'quality-baseline: 1 new, 0 resolved' >/dev/null
printf '%s\n' "$out" | grep -F 'ruleId=VSTHRD002 path=src/PlantedFinding.cs' >/dev/null
printf '%s\n' "$out"

# An analyzer-error engine with an intact finding set is the everyday Windows shape: warn and pass.
node - "$candidate" "$committed" <<'NODE'
const {readFileSync, writeFileSync} = require('node:fs')
const committed = JSON.parse(readFileSync(process.argv[3], 'utf8'))
writeFileSync(process.argv[2], JSON.stringify({...committed, engines: [{engine: 'roslyn', status: 'analyzer-error', detail: 'analyzer-error'}]}) + '\n')
NODE
if ! out=$(quality_baseline_gate_candidate_compare "$candidate" "$committed" 2>&1); then
  echo 'FAIL analyzer-error engine with an intact set was refused'; exit 1
fi
printf '%s\n' "$out" | grep -F 'quality-baseline: warning: engine roslyn reported analyzer-error' >/dev/null
printf '%s\n' "$out" | grep -F 'quality-baseline: engine roslyn status=analyzer-error detail=analyzer-error' >/dev/null
printf '%s\n' "$out"
# An analyzer-error engine with an EMPTY set is the hollow shape: the resolved bound refuses it.
large_committed_early="$fixture/large-committed-early.json"
large_baseline "$large_committed_early" 2363 '[{"engine":"roslyn","status":"ok","detail":""},{"engine":"eslint","status":"ok","detail":""}]'
printf '{"schemaVersion":1,"findings":[],"engines":[{"engine":"roslyn","status":"analyzer-error","detail":"analyzer-error"}]}' > "$candidate"
if out=$(quality_baseline_gate_candidate_compare "$candidate" "$large_committed_early" 2>&1); then
  echo 'FAIL hollow analyzer-error candidate passed'; exit 1
fi
printf '%s\n' "$out" | grep -F 'looks like an engine failure, not a clean-up' >/dev/null
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
[ "$out" = 'quality-baseline: 0 new, 3 resolved (passing; re-pin is a follow-up PR)' ] || { echo "FAIL small resolved set: $out"; exit 1; }
printf '%s\n' "$out"
# quality_baseline_gate itself: the committed fallback when no merge-base artifact is present, and the
# merge-base artifact when HARBORLINE_QUALITY_BASELINE names one. (These rows lived in the retired landing
# test; the gate is the only caller now.) The CI verify exports HARBORLINE_QUALITY_BASELINE, so the
# fallback row clears it explicitly rather than inheriting the runner's.
gate_root="$fixture/gate"
mkdir -p "$gate_root/eng/baselines" "$gate_root/artifacts/quality"
cp "$large_committed" "$gate_root/eng/baselines/quality-baseline.json"
node - "$gate_root/eng/baselines/quality-baseline.json" <<'NODE'
const {readFileSync, writeFileSync} = require('node:fs')
const file = process.argv[2]
const baseline = JSON.parse(readFileSync(file, 'utf8'))
writeFileSync(file, JSON.stringify({...baseline, commit: 'baseline-fixture-405', generatedAt: new Date(Date.now() - (3 * 60 + 2) * 60 * 1000).toISOString()}) + '\n')
NODE
cp "$large_committed" "$gate_root/artifacts/quality/findings.json"
out=$(HARBORLINE_QUALITY_BASELINE= quality_baseline_gate "$gate_root" 2>&1) || { echo "FAIL gate with committed fallback exited non-zero: $out"; exit 1; }
grep -Fq "quality-baseline: using committed fallback $gate_root/eng/baselines/quality-baseline.json (pinned commit=baseline-fixture-405; age=3h" <<<"$out" || { echo "FAIL missing dated committed fallback message: $out"; exit 1; }
artifact="$fixture/merge-base.json"; cp "$large_committed" "$artifact"
out=$(HARBORLINE_QUALITY_BASELINE="$artifact" quality_baseline_gate "$gate_root" 2>&1) || { echo "FAIL gate with merge-base artifact exited non-zero: $out"; exit 1; }
grep -Fq "quality-baseline: using merge-base artifact $artifact" <<<"$out" || { echo "FAIL missing merge-base artifact message: $out"; exit 1; }
echo 'quality-baseline-gate: 10 checks passed'
# (the seven above plus: gate uses the committed fallback; gate uses the merge-base artifact)
echo 'quality-baseline-gate: detail (new finding red with rule/path; unchanged green; resolved-only green; analyzer-error with intact set warns and passes; hollow set refused by the bound; large resolved refused; small resolved green)'
