#!/usr/bin/env bash
# Exercises the same comparison that land.sh runs after its merge-tree gate.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
cleanup() { rm -rf "$fixture"; }
trap cleanup EXIT
mkdir -p "$fixture/eng/baselines" "$fixture/eng"
cp "$root/eng/quality-baseline-landing.sh" "$fixture/eng/"
git -C "$fixture" init -q
git -C "$fixture" config user.name QualityLandingTest
git -C "$fixture" config user.email quality-landing@test.invalid
printf '{"schemaVersion":1,"commit":"fixture","generatedAt":"2026-09-08T00:00:00.000Z","findings":[]}\n' > "$fixture/eng/baselines/quality-baseline.json"
git -C "$fixture" add .
git -C "$fixture" commit --no-verify -qm baseline

shim=$(mktemp -d)
cleanup() { rm -rf "$fixture" "$shim"; }
real_node=$(command -v node)
cat > "$shim/node" <<'SHIM'
#!/usr/bin/env bash
if [ "$1" = '-' ]; then exec "$REAL_NODE" "$@"; fi
candidate=${@: -1}
baseline="$PWD/eng/baselines/quality-baseline.json"
resolved='[]'
if grep -q '"fingerprint":"x"' "$baseline"; then resolved='[{"fingerprint":"x"}]'; fi
printf '{"decisionId":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","policyDigest":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","findings":[],"resolved":%s}\n' "$resolved" > "$(git rev-parse --git-common-dir)/harborline-api-quality-decision.json"
printf '{"schemaVersion":1,"commit":"fixture","generatedAt":"2026-09-08T00:00:00.000Z","findings":[]}\n' > "$candidate"
SHIM
chmod +x "$shim/node"
export REAL_NODE="$real_node"
export PATH="$shim:$PATH"

( cd "$fixture" && source eng/quality-baseline-landing.sh && quality_baseline_compare "$fixture" ) > "$fixture/identical.out"
grep -q 'land: quality baseline unchanged' "$fixture/identical.out"
printf '{"schemaVersion":1,"commit":"fixture","generatedAt":"2026-09-08T00:00:00.000Z","findings":[{"fingerprint":"x","ruleId":"X","path":"x","symbol":null,"snippetHash":"x","enginePartial":"x"}]}\n' > "$fixture/eng/baselines/quality-baseline.json"
git -C "$fixture" add eng/baselines/quality-baseline.json
git -C "$fixture" commit --no-verify -qm moved
if ( cd "$fixture" && source eng/quality-baseline-landing.sh && quality_baseline_compare "$fixture" ) > "$fixture/moved.out" 2>&1; then
  echo 'FAIL moved quality baseline passed'; exit 1
fi
grep -q 'quality baseline moved: 0 new, 1 resolved (re-pin on the branch and regate)' "$fixture/moved.out"
printf '{"schemaVersion":1,"commit":"fixture","generatedAt":"2026-09-08T00:00:00.000Z","findings":[]}\n' > "$fixture/eng/baselines/quality-baseline.json"
if ( cd "$fixture" && source eng/quality-baseline-landing.sh && quality_baseline_compare "$fixture" ) && dry=1 && [ "$dry" -eq 1 ]; then
  echo 'land: dry run — baseline comparison passed'
else
  echo 'FAIL identical baseline did not permit dry run'; exit 1
fi
echo 'quality-baseline-landing: 3 checks passed (identical; committed move refusal; dry comparison)'
