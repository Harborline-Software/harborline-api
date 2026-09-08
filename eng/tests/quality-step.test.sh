#!/usr/bin/env bash
# Exercises the pinned quality step's refusal and rollout outcomes against a disposable API repository.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
quality_root=$(cd "$(dirname "$(git -c safe.directory="$root" -C "$root" rev-parse --path-format=absolute --git-common-dir)")/../harborline-quality" && pwd -W)
export GIT_CONFIG_COUNT=1
export GIT_CONFIG_KEY_0=safe.directory
export GIT_CONFIG_VALUE_0="$quality_root"
fixture=$(mktemp -d)
cleanup() { rm -rf "$fixture"; }
trap cleanup EXIT

mkdir -p "$fixture/eng/baselines" "$fixture/src" "$fixture/artifacts/quality" "$fixture/control/policy"
cp "$root/eng/quality-pin.json" "$fixture/eng/quality-pin.json"
cp "$root/eng/quality-policy.yaml" "$fixture/eng/quality-policy.yaml"
cp "$root/eng/verify-receipt.mjs" "$root/eng/host-baseline.mjs" "$fixture/eng/"
cat > "$fixture/control/policy/quality-defaults.yaml" <<'YAML'
version: 1
policyId: test-quality
mode: evaluate
findings:
  thresholds:
    error: 0
coverage:
  required: false
waivers:
  allowed: false
YAML
printf 'before\n' > "$fixture/src/example.cs"
git -C "$fixture" init -q
git -C "$fixture" config user.name QualityTest
git -C "$fixture" config user.email quality@test.invalid
git -C "$fixture" add .
git -C "$fixture" commit --no-verify -qm base
base=$(git -C "$fixture" rev-parse HEAD)
printf '{"schemaVersion":1,"commit":"%s","generatedAt":"2026-09-08T00:00:00.000Z","findings":[]}\n' "$base" > "$fixture/eng/baselines/quality-baseline.json"
git -C "$fixture" add eng/baselines/quality-baseline.json
git -C "$fixture" commit --no-verify -qm baseline
git -C "$fixture" update-ref refs/remotes/origin/main "$base"

if env -u HARBORLINE_QUALITY_REPO -u HARBORLINE_CONTROL_REPO node "$root/eng/quality-step.mjs" --root "$fixture" > "$fixture/.git/missing.out" 2>&1; then
  echo 'FAIL missing quality pin passed'; exit 1
fi
grep -q 'HARBORLINE_QUALITY_REPO' "$fixture/.git/missing.out"

HARBORLINE_QUALITY_REPO="$quality_root" HARBORLINE_CONTROL_REPO="$fixture/control" node "$root/eng/quality-step.mjs" --root "$fixture" > "$fixture/.git/zero.out"
grep -q 'quality: recorded sha256:' "$fixture/.git/zero.out"
( cd "$fixture" && node eng/verify-receipt.mjs --record boundaries identity-r3 codegen-check codegen-guard-suite contracts-typescript contracts-csharp localfirst-csharp rule-engine-conformance contracts-rust operator-cli-headless exact-clone quality packages --host-baseline eng/baselines/host-test-baseline.json ) > "$fixture/.git/receipt.out"
node -e 'const r=require(process.argv[1]), q=r.steps.find(s=>typeof s === "object" && s.id === "quality"); if(!q || !/^sha256:[a-f0-9]{64}$/.test(q.decisionDigest) || !/^sha256:[a-f0-9]{64}$/.test(q.policyDigest)) process.exit(1)' "$fixture/.git/harborline-api-verify-receipt.json"

printf 'before\nnew finding\n' > "$fixture/src/example.cs"
git -C "$fixture" add src/example.cs
git -C "$fixture" commit --no-verify -qm changed
cat > "$fixture/artifacts/quality/finding.sarif" <<'JSON'
{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"test","version":"1","rules":[{"id":"TEST001"}]}},"results":[{"ruleId":"TEST001","level":"error","message":{"text":"planted"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/example.cs"},"region":{"startLine":2,"snippet":{"text":"new finding"}}}}]}]}]}
JSON
HARBORLINE_QUALITY_REPO="$quality_root" HARBORLINE_CONTROL_REPO="$fixture/control" node "$root/eng/quality-step.mjs" --root "$fixture" > "$fixture/.git/evaluate.out"
node -e 'const d=require(process.argv[1]); if(d.wouldBlock !== true || d.conclusion !== "success" || d.findings.length !== 1) process.exit(1)' "$fixture/.git/harborline-api-quality-decision.json"
printf 'mode: enforce\n' > "$fixture/eng/quality-policy.yaml"
git -C "$fixture" add eng/quality-policy.yaml
git -C "$fixture" commit --no-verify -qm enforce
if HARBORLINE_QUALITY_REPO="$quality_root" HARBORLINE_CONTROL_REPO="$fixture/control" node "$root/eng/quality-step.mjs" --root "$fixture" > "$fixture/.git/enforce.out" 2>&1; then
  echo 'FAIL enforce quality step passed'; exit 1
fi
grep -q 'Code Quality Gate — FAILURE' "$fixture/.git/enforce.out"
echo 'quality-step: 4 checks passed (missing pin refusal; zero-input receipt; evaluate wouldBlock; enforce failure)'
