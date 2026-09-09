#!/usr/bin/env bash
# Ticket 335 fix 1: land-owned scratch must never dirty the merge worktree the exact-clone gate sees.
set -uo pipefail
set +e

source_root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$source_root"
scratch=$(mktemp -d ".claude/land-dirty-tree.XXXXXX")
trap 'rm -rf "$scratch"' EXIT
real_node=$(command -v node)
real_git=$(command -v git)
# shellcheck source=fixture-git-retry.sh
source "$source_root/eng/tests/fixture-git-retry.sh"

write_baseline() {
  local path=$1 total=$2
  mkdir -p "$(dirname "$path")"
  cat > "$path" <<EOF
{
  "schemaVersion": 1,
  "suite": "test",
  "repository": "test",
  "sourcePin": "test",
  "recordedAt": "2026-09-08T00:00:00Z",
  "totals": {
    "total": $total,
    "passed": $((total - 2)),
    "failed": 0,
    "notExecuted": 2
  },
  "permittedFailures": [],
  "knownFlaky": [],
  "deltaFromPrevious": {
    "previousTotals": { "total": $total, "passed": $((total - 2)), "failed": 0, "notExecuted": 2 },
    "change": "fixture",
    "cause": "fixture",
    "detectedBy": "fixture",
    "arithmeticCrossCheck": "fixture"
  }
}
EOF
}

remote="$scratch/remote.git"
seed="$scratch/seed"
runner="$scratch/runner"
git_r init --bare -q "$remote"
git_r init -q -b main "$seed"
git_r -C "$seed" config user.name 'Land Test'
git_r -C "$seed" config user.email 'land-test@example.invalid'
mkdir -p "$seed/eng"
cp "$source_root/eng/land.sh" "$source_root/eng/land-resolve.sh" "$source_root/eng/land-evidence.sh" \
  "$source_root/eng/gate-lock.sh" "$source_root/eng/repin-baseline.mjs" "$source_root/eng/splice-generic.js" "$source_root/eng/quality-baseline-landing.sh" "$seed/eng/"
cat > "$seed/eng/gate-lock.sh" <<'EOF'
gate_lock_acquire() { :; }
gate_lock_release() { :; }
EOF
cat > "$seed/eng/quality-step.mjs" <<'EOF'
// Fixture stand-in for the quality tool: 339 s4's landing comparison runs it on the merged tree.
import {writeFileSync} from 'node:fs'
writeFileSync(process.argv[process.argv.indexOf('--write-baseline') + 1], JSON.stringify({findings: []}))
EOF
mkdir -p "$seed/eng/baselines"
printf '{"findings": []}
' > "$seed/eng/baselines/quality-baseline.json"
cat > "$seed/eng/test-clean-tree-gate.sh" <<'EOF'
#!/usr/bin/env bash
land_worktree=$(git rev-parse --show-toplevel)
worktree_parent=$(dirname "$land_worktree")
land_scratch=''
for candidate in "$worktree_parent"/land-scratch-*; do
  if [ -d "$candidate" ]; then land_scratch=$candidate; break; fi
done
porcelain=$(git status --porcelain)
{
  printf '%s\n' "$land_worktree"
  printf '%s\n' "$land_scratch"
  printf '%s\n' "$porcelain"
} > "$LAND_CLEAN_EVIDENCE"
[ -z "$porcelain" ] && [ -n "$land_scratch" ] && [ "$(dirname "$land_scratch")" = "$worktree_parent" ]
EOF
chmod +x "$seed/eng/test-clean-tree-gate.sh"
write_baseline "$seed/eng/baselines/host-test-baseline.json" 100
git_r -C "$seed" add .
git_r -C "$seed" commit -q -m seed
git_r -C "$seed" remote add origin "../remote.git"
git_r -C "$seed" push -q -u origin main
git_r --git-dir="$remote" symbolic-ref HEAD refs/heads/main

git_r -C "$seed" switch -q -c feature
write_baseline "$seed/eng/baselines/host-test-baseline.json" 102
git_r -C "$seed" add .
git_r -C "$seed" commit -q -m feature
git_r -C "$seed" push -q -u origin feature
git_r -C "$seed" switch -q main
write_baseline "$seed/eng/baselines/host-test-baseline.json" 103
git_r -C "$seed" add .
git_r -C "$seed" commit -q -m main
git_r -C "$seed" push -q

git_r clone -q "$remote" "$runner"
git_r -C "$runner" config user.name 'Land Test'
git_r -C "$runner" config user.email 'land-test@example.invalid'
shim="$scratch/shim"
mkdir -p "$shim"
cat > "$shim/node" <<'EOF'
#!/usr/bin/env bash
case "$1" in
  eng/build-local-feed.mjs|eng/verify-receipt.mjs) exit 0 ;;
  *) exec "$REAL_NODE" "$@" ;;
esac
EOF
cat > "$shim/dotnet" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$shim/git" <<'EOF'
#!/usr/bin/env bash
if [ "$*" = "rev-parse --show-toplevel" ] && [ "$(basename "$PWD")" = runner ]; then echo .; else exec "$REAL_GIT" "$@"; fi
EOF
chmod +x "$shim/node" "$shim/dotnet" "$shim/git"
shim_path=$(cd "$shim" && pwd)

out="$scratch/output.log"
evidence="$scratch/clean-tree.evidence"
REAL_NODE="$real_node" REAL_GIT="$real_git" HARBORLINE_LAND_VERIFY_CMD='bash eng/test-clean-tree-gate.sh' \
  HARBORLINE_GATE_LOCK_PATH="$scratch/gate.lock" LAND_CLEAN_EVIDENCE='../../../../clean-tree.evidence' PATH="$shim_path:$PATH" \
  bash -c 'cd "$1" && bash eng/land.sh feature --dry-run' -- "$runner" > "$out" 2>&1
rc=$?
if [ "$rc" -ne 0 ]; then
  echo "FAIL clean-tree gate: land.sh rc=$rc; recorded evidence:"
  if [ -f "$evidence" ]; then sed -n '1,20p' "$evidence"; else echo '(no evidence recorded)'; fi
  tail -20 "$out"
  exit 1
fi
if [ ! -f "$evidence" ]; then
  echo 'FAIL clean-tree gate: verifier recorded no evidence'
  tail -20 "$out"
  exit 1
fi
land_worktree=$(sed -n '1p' "$evidence")
land_scratch=$(sed -n '2p' "$evidence")
porcelain=$(sed -n '3,$p' "$evidence")
if [ -n "$porcelain" ]; then
  echo "FAIL clean-tree gate: git -C $land_worktree status --porcelain returned:"
  printf '%s\n' "$porcelain"
  exit 1
fi
if [ -z "$land_scratch" ] || [ "$(dirname "$land_scratch")" != "$(dirname "$land_worktree")" ]; then
  echo "FAIL clean-tree gate: worktree=$land_worktree scratch=${land_scratch:-'(not found)'}"
  exit 1
fi
echo 'ok   land verifier sees a clean merge worktree'
