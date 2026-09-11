#!/usr/bin/env bash
# Exercises the comparison land.sh runs after its merge-tree gate, on real files: the committed baseline is
# pretty-printed with an older commit and timestamp, the candidate is the tool's canonical single line.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
source "$root/eng/quality-baseline-landing.sh"
row() { printf '{"fingerprint":"%s","ruleId":"R","path":"p","symbol":null,"snippetHash":"h","enginePartial":""}' "$1"; }
committed="$fixture/committed.json"; candidate="$fixture/candidate.json"
printf '{\n  "schemaVersion": 1,\n  "commit": "old",\n  "generatedAt": "2026-09-01T00:00:00.000Z",\n  "findings": [\n    %s,\n    %s\n  ]\n}\n' "$(row a)" "$(row b)" > "$committed"
printf '{"schemaVersion":1,"commit":"new","generatedAt":"2026-09-08T12:00:00.000Z","findings":[%s,%s]}' "$(row b)" "$(row a)" > "$candidate"
[ "$(quality_baseline_sets_compare "$candidate" "$committed")" = "0 0" ] || { echo 'FAIL identical sets in different bytes were not equal'; exit 1; }
printf '{"schemaVersion":1,"commit":"new","generatedAt":"2026-09-08T12:00:00.000Z","findings":[%s]}' "$(row a)" > "$candidate"
[ "$(quality_baseline_sets_compare "$candidate" "$committed")" = "0 1" ] || { echo 'FAIL a resolved finding was not counted'; exit 1; }
printf '{"schemaVersion":1,"commit":"new","generatedAt":"2026-09-08T12:00:00.000Z","findings":[%s,%s,%s]}' "$(row a)" "$(row b)" "$(row c)" > "$candidate"
[ "$(quality_baseline_sets_compare "$candidate" "$committed")" = "1 0" ] || { echo 'FAIL a new finding was not counted'; exit 1; }
gate_root="$fixture/gate"
mkdir -p "$gate_root/eng/baselines" "$gate_root/artifacts/quality"
cp "$committed" "$gate_root/eng/baselines/quality-baseline.json"
cp "$committed" "$gate_root/artifacts/quality/findings.json"
fallback_out=$(quality_baseline_gate "$gate_root" 2>&1)
grep -Fq "quality-baseline: using committed fallback $gate_root/eng/baselines/quality-baseline.json" <<<"$fallback_out" || { echo 'FAIL missing committed fallback message'; exit 1; }
echo 'quality-baseline-landing: 4 checks passed (identical sets across formats; resolved counted; new counted; committed fallback message)'
