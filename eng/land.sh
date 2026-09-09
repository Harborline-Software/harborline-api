#!/usr/bin/env bash
# land.sh <branch> [--pr N] [--dry-run]
#
# The manual bors (R-0006 lesson 4, ticket 242). Tests the MERGE COMMIT before it lands, lands exactly
# that head, then proves main is the tree that was tested.
#   1. fetch (checked); pin BASE=origin/main and HEAD=origin/<branch> by sha
#   2. throwaway worktree at BASE; merge HEAD (no fast-forward). If main moved since the branch's
#      baseline pin, re-pin the merge to main's totals plus the branch delta.
#   3. build the platform feed there (the api restores Harborline.Contracts from it since 314 s2), then verify
#   4. bind the PR to the pinned refs: base branch main, PR head == HEAD; re-fetch and refuse if
#      origin/main moved off BASE; merge with `gh pr merge --squash --match-head-commit HEAD` so the
#      server refuses a head that advanced after the gate
#   5. resolve the landing from the REMOTE (eng/land-resolve.sh), whatever gh's exit code said: if the PR
#      is merged or origin/main moved, the landed tree must equal the tested tree (exit 0) or main is gated
#      right here with the revert printed if red (exit 3). "Nothing landed" (exit 1) is claimed only when the
#      PR is not merged AND origin/main is still the pinned base. eng/tests/land-resolve.test.sh enumerates it.
# Hooks: gh pr merge is a server-side operation and does not run .githooks/pre-push; that is why this
# script runs the same verify.sh the hook requires, on the exact tree that lands.
set -euo pipefail
branch=${1:?usage: land.sh <branch> [--pr N] [--dry-run]}; shift || true
pr=""; dry=0
while [ $# -gt 0 ]; do case $1 in --pr) pr=$2; shift 2;; --dry-run) dry=1; shift;; *) echo "unknown arg $1"; exit 2;; esac; done
root=$(git rev-parse --show-toplevel); cd "$root"
# shellcheck source=gate-lock.sh
source "$root/eng/gate-lock.sh"
gate_lock_acquire "eng/land.sh $branch"
# shellcheck source=land-evidence.sh
source "$root/eng/land-evidence.sh"
git fetch -q origin || { echo "land: fetch failed; refusing to gate stale refs"; exit 1; }
base_sha=$(git rev-parse origin/main)
head_sha=$(git rev-parse "origin/$branch") || { echo "land: origin/$branch does not exist; push the branch first"; exit 1; }
merge_base=$(git merge-base "$base_sha" "$head_sha") || { echo "land: origin/main and origin/$branch have no merge base"; exit 1; }
baseline=eng/baselines/host-test-baseline.json
baseline_tuple_at() {
  git show "$1:$baseline" | node -e 'let s=""; process.stdin.on("data", d => s += d).on("end", () => { const t=JSON.parse(s).totals; const v=[t.total,t.passed,t.failed,t.notExecuted]; if (!v.every(Number.isSafeInteger) || t.total !== t.passed+t.failed+t.notExecuted) process.exit(1); console.log(v.join("\t")) })'
}
baseline_policy_at() {
  git show "$1:$baseline" | node -e 'const {createHash}=require("node:crypto"); let s=""; process.stdin.on("data", d => s += d).on("end", () => { const b=JSON.parse(s); for (const k of ["sourcePin","recordedAt","totals","deltaFromPrevious","measuredAt"]) delete b[k]; process.stdout.write(createHash("sha256").update(JSON.stringify(b)).digest("hex")) })'
}
IFS=$'\t' read -r main_total main_passed main_failed main_not_executed < <(baseline_tuple_at "$base_sha") || { echo "land: invalid main host baseline"; exit 1; }
IFS=$'\t' read -r branch_total branch_passed branch_failed branch_not_executed < <(baseline_tuple_at "$head_sha") || { echo "land: invalid branch host baseline"; exit 1; }
IFS=$'\t' read -r common_total common_passed common_failed common_not_executed < <(baseline_tuple_at "$merge_base") || { echo "land: invalid merge-base host baseline"; exit 1; }
main_moved=0
if [ "$merge_base" != "$base_sha" ]; then main_moved=1; fi
if [ $main_moved -eq 1 ] && [ "$(baseline_policy_at "$head_sha")" != "$(baseline_policy_at "$merge_base")" ]; then
  echo "land: branch changed host-baseline policy beyond its pin; merge main into the branch and regate so no policy change is discarded"; exit 1
fi
main_move=$((main_total - common_total))
branch_delta=$((branch_total - common_total))
expected_total=$((main_total + branch_delta))
expected_passed=$((main_passed + branch_passed - common_passed))
expected_failed=$((main_failed + branch_failed - common_failed))
expected_not_executed=$((main_not_executed + branch_not_executed - common_not_executed))
if [ "$expected_failed" -ne 0 ] || [ "$expected_total" -ne $((expected_passed + expected_failed + expected_not_executed)) ]; then
  echo "land: baseline arithmetic is invalid (main $main_total, branch delta $(printf '%+d' "$branch_delta"), expected $expected_total)"; exit 1
fi
intervening_landings=$(git log --oneline "$merge_base..$base_sha")
landing_shas=$(git log --format=%h "$merge_base..$base_sha" | paste -sd, -)
land_dir="$root/.claude/worktrees/land-$$-$(date +%s)"
land_scratch="$root/.claude/worktrees/land-scratch-$$"
cleanup() { if [ -d "$land_dir" ]; then git worktree remove --force "$land_dir" >/dev/null 2>&1 || echo "land: WARNING could not remove $land_dir"; fi; rm -rf "$land_scratch"; gate_lock_release; }
trap cleanup EXIT
git worktree add --detach "$land_dir" "$base_sha" -q
if ! git -C "$land_dir" merge --no-ff --no-commit "$head_sha" >/dev/null 2>&1; then
  conflicts=$(git -C "$land_dir" diff --name-only --diff-filter=U)
  [ "$conflicts" = "$baseline" ] || { echo "land: merge conflict between origin/main and $branch"; exit 1; }
  git -C "$land_dir" checkout "$base_sha" -- "$baseline"
  git -C "$land_dir" add "$baseline"
fi
repinned_head=""
if [ $main_moved -eq 1 ]; then
  repin_dir="$land_scratch/land-repin"
  mkdir -p "$repin_dir"
  printf '{"recordedAt":"%s","cause":"ticket 335: main moved by %s; branch delta %+d; intervening landings: %s","detectedBy":"eng/land.sh merge-commit verification"}\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$main_move" "$branch_delta" "${landing_shas:-none}" > "$repin_dir/narrative.json"
  printf 'Failed: 0, Passed: %s, Skipped: %s, Total: %s\n' "$expected_passed" "$expected_not_executed" "$expected_total" > "$repin_dir/measured.log"
  node "$land_dir/eng/repin-baseline.mjs" "$land_dir/$baseline" "$repin_dir/narrative.json" "$repin_dir/measured.log" >/dev/null
  git -C "$land_dir" add "$baseline"
  git -C "$land_dir" commit -q -m "335: re-pin baseline after main moved (+$main_move from ${landing_shas:-none})"
  repinned_head=$(git -C "$land_dir" rev-parse HEAD)
else
  git -C "$land_dir" commit -q --no-edit
fi
tested_tree=$(git -C "$land_dir" rev-parse 'HEAD^{tree}')
if [ $main_moved -eq 0 ]; then
  head_tree=$(git rev-parse "$head_sha^{tree}")
  [ "$tested_tree" = "$head_tree" ] || { echo "land: internal: merge tree != head tree although head is a descendant of main"; exit 1; }
fi
echo "land: gating tree ${tested_tree:0:12} (base $(git rev-parse --short "$base_sha"), head $(git rev-parse --short "$head_sha"))"
run_land_verify() {
  local verify_root=$1 verify_log=$2
  ( cd "$verify_root" && export HARBORLINE_GATE_QUALITY=1 && node eng/build-local-feed.mjs >/dev/null 2>&1 && dotnet restore apps/local-node-host/tests/tests.csproj -nodeReuse:false -maxcpucount:6 >/dev/null 2>&1 && ( for d in apps/capability-host; do [ -d "$d" ] && ( cd "$d" && npm ci --silent --no-audit --no-fund >/dev/null 2>&1 ) || true; done ) && if [ -n "${HARBORLINE_LAND_VERIFY_CMD:-}" ]; then exec bash -c "$HARBORLINE_LAND_VERIFY_CMD"; else exec bash eng/verify.sh; fi ) > "$verify_log" 2>&1
  local verify_rc=$?
  cat "$verify_log"
  return "$verify_rc"
}

# Ticket 350: the mac slice gate publishes a receipt for the MERGE tree as refs/receipts/tree/<tree>.
# If one exists for exactly this tree, is schema-current, covers every step eng/verify-receipt.mjs
# requires and is younger than HARBORLINE_RECEIPT_MAX_AGE_HOURS, the twelve steps have already run on
# this exact tree and re-running them here buys nothing but an hour. Everything AFTER the gate — the
# baseline re-pin, the PR binding, the merge and the landing resolution — is unchanged and still runs.
# eng/receipt-accept.mjs owns the decision (and prints the reason either way) so it is testable
# without a repository.
receipt_ref="refs/receipts/tree/$tested_tree"
receipt_file=$(mktemp "${TMPDIR:-/tmp}/land-receipt-XXXXXX")
receipt_accepted=0
if git fetch -q --no-tags origin "+$receipt_ref:$receipt_ref" 2>/dev/null && git cat-file blob "$receipt_ref" >"$receipt_file" 2>/dev/null; then
  if node eng/receipt-accept.mjs "$tested_tree" "$receipt_file"; then receipt_accepted=1; fi
else
  echo "land: no verification receipt published for tree ${tested_tree:0:12}; gating here"
fi
rm -f "$receipt_file"
if [ $receipt_accepted -eq 0 ]; then
verify_log="$land_scratch/land-verify.log"
preserved_verify_log="$root/.claude/land-verify-$$.log"
mkdir -p "$(dirname "$verify_log")"
if ! run_land_verify "$land_dir" "$verify_log" || ! ( cd "$land_dir" && node eng/verify-receipt.mjs --landing ); then
  cp "$verify_log" "$preserved_verify_log" || { echo "land: WARNING could not preserve verify log at $preserved_verify_log" >&2; preserved_verify_log="$verify_log"; }
  measured_total=$(node - "$land_dir/.claude/land-evidence/exact-clone-fail.json" <<'NODE'
const {existsSync, readFileSync} = require('node:fs')
const evidence = process.argv[2]
let measured = null
if (existsSync(evidence)) {
  try {
    const report = JSON.parse(readFileSync(evidence, 'utf8'))
    const total = report.steps.find(step => step.id === 'host-baseline-match')?.observed?.total
    if (Number.isSafeInteger(total)) measured = total
  } catch {}
}
if (measured !== null) process.stdout.write(String(measured))
NODE
)
  if [ $main_moved -eq 1 ]; then
    if [ -n "$measured_total" ] && [ "$measured_total" -ne "$expected_total" ]; then
      measured_move=$((measured_total - branch_total))
      echo "land: main moved by $main_move, measured differs by $measured_move!=$main_move: regression (main $main_total, branch delta $(printf '%+d' "$branch_delta"), measured $measured_total; verify log: $preserved_verify_log)"
      echo "land: intervening landings:"
      printf '%s\n' "$intervening_landings"
    fi
  fi
  if [ -z "$measured_total" ]; then echo "land: gate RED before the host suite ran; no measurement (see $preserved_verify_log)"; fi
  preserve_land_evidence "$root" "$land_dir" "$head_sha" || echo "land: WARNING could not preserve exact-clone evidence" >&2
  echo "land: gate RED on the merge commit; nothing landed (verify log: $preserved_verify_log)"
  exit 1
fi
if [ $main_moved -eq 1 ]; then
  echo "land: main moved, measured matches: re-pinned (main $main_total, branch delta $(printf '%+d' "$branch_delta"), measured $expected_total)"
  echo "land: intervening landings:"
  printf '%s\n' "$intervening_landings"
fi
else
  if [ $main_moved -eq 1 ]; then
    echo "land: main moved: re-pinned arithmetically (main $main_total, branch delta $(printf '%+d' "$branch_delta"), expected $expected_total); measurement skipped on the accepted receipt (ticket 350)"
    echo "land: intervening landings:"
    printf '%s\n' "$intervening_landings"
  fi
fi
if [ $dry -eq 1 ]; then echo "land: dry run — gate green on ${tested_tree:0:12}; not landing"; exit 0; fi
if [ $main_moved -eq 1 ]; then
  git -C "$land_dir" push --no-verify origin "$repinned_head:refs/heads/$branch" || { echo "land: could not push the re-pinned merge head; nothing landed"; exit 1; }
  head_sha=$repinned_head
fi
if [ -z "$pr" ]; then
  pr=$(gh pr list --head "$branch" --base main --state open --json number --jq '.[0].number // empty')
  [ -n "$pr" ] || { echo "land: no open PR for $branch against main — open it first so the description is reviewed"; exit 1; }
fi
pr_base=$(gh pr view "$pr" --json baseRefName --jq .baseRefName)
pr_head_ref=$(gh pr view "$pr" --json headRefName --jq .headRefName)
[ "$pr_base" = "main" ] || { echo "land: PR #$pr targets '$pr_base', not main"; exit 1; }
[ "$pr_head_ref" = "$branch" ] || { echo "land: PR #$pr head is '$pr_head_ref', not $branch"; exit 1; }
if [ $main_moved -eq 1 ]; then
  pushed_head=$(git ls-remote --heads origin "refs/heads/$branch" | awk -v ref="refs/heads/$branch" '$2 == ref { print $1 }')
  [ "$pushed_head" = "$head_sha" ] || { echo "land: remote branch $branch ($pushed_head) is not the gated head ($head_sha); the branch moved after the gate. Rerun land.sh."; exit 1; }
  pr_head_retries=${LAND_PR_HEAD_RETRIES:-12}
  pr_head_delay=${LAND_PR_HEAD_DELAY:-5}
  case "$pr_head_retries" in ''|*[!0-9]*) echo "land: LAND_PR_HEAD_RETRIES must be a positive integer"; exit 2;; esac
  [ "$pr_head_retries" -gt 0 ] || { echo "land: LAND_PR_HEAD_RETRIES must be a positive integer"; exit 2; }
  pr_head_reads=0
  while [ "$pr_head_reads" -lt "$pr_head_retries" ]; do
    pr_head=$(gh pr view "$pr" --json headRefOid --jq .headRefOid)
    pr_head_reads=$((pr_head_reads + 1))
    [ "$pr_head" = "$head_sha" ] && break
    if [ "$pr_head_reads" -lt "$pr_head_retries" ]; then
      echo "land: waiting for GitHub to see the pushed head ($pr_head_reads)"
      sleep "$pr_head_delay"
    fi
  done
  [ "$pr_head" = "$head_sha" ] || { echo "land: PR #$pr head ($pr_head) is not the pushed head ($head_sha); GitHub is stale after $pr_head_reads reads. Rerun land.sh."; exit 1; }
else
  pr_head=$(gh pr view "$pr" --json headRefOid --jq .headRefOid)
  [ "$pr_head" = "$head_sha" ] || { echo "land: PR #$pr head ($pr_head) is not the gated head ($head_sha); the branch moved after the gate. Rerun land.sh."; exit 1; }
fi
git fetch -q origin || { echo "land: fetch failed before landing; refusing"; exit 1; }
[ "$(git rev-parse origin/main)" = "$base_sha" ] || { echo "land: origin/main moved during the gate ($(git rev-parse --short origin/main) != $(git rev-parse --short "$base_sha")); nothing landed. Merge main into the branch, regate, rerun."; exit 1; }
# shellcheck source=land-resolve.sh
source "$root/eng/land-resolve.sh"
gate_main='verify_dir="$root/.claude/worktrees/land-verify-$$"; verify_scratch="$root/.claude/worktrees/land-scratch-$$"; verify_log="$verify_scratch/land-verify.log"; preserved_verify_log="$root/.claude/land-verify-$$.log"; git worktree add --detach "$verify_dir" origin/main -q && mkdir -p "$(dirname "$verify_log")" && run_land_verify "$verify_dir" "$verify_log" && ( cd "$verify_dir" && node eng/verify-receipt.mjs --landing ); rc=$?; cp "$verify_log" "$preserved_verify_log" || true; git worktree remove --force "$verify_dir" >/dev/null 2>&1 || true; rm -rf "$verify_scratch"; [ $rc -eq 0 ]'
land_request_and_resolve "$pr" "$base_sha" "$head_sha" "$tested_tree" "$gate_main"
exit $?
