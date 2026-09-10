#!/usr/bin/env bash
# Ticket 335: a branch pinned before main moves is merged, re-pinned, verified and pushed as the
# exact head that is handed to gh. A measurement not explained by main's movement is refused.
set -uo pipefail

source_root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$source_root"
scratch=$(mktemp -d ".claude/land-main-moved.XXXXXX")
[ -n "${KEEP_SCRATCH:-}" ] || trap 'rm -rf "$scratch"' EXIT
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

make_case() {
  local name=$1 measured=$2 want_rc=$3 evidence=$4 receipt=${5:-0} post_merge_gate=${6:-0} stale_reads=${7:-0} checks_pending_reads=${8:-0}
  local case_dir="$scratch/$name" remote="$scratch/$name.git" case_dir_absolute
  local seed="$case_dir/seed" runner="$case_dir/runner"
  mkdir -p "$case_dir"
  case_dir_absolute=$(cd "$case_dir" && pwd)
  git_r init --bare -q "$remote"
  git_r init -q -b main "$seed"
  git_r -C "$seed" config user.name "Land Test"
  git_r -C "$seed" config user.email "land-test@example.invalid"
  mkdir -p "$seed/eng/tests"
  cp "$source_root/eng/land.sh" "$source_root/eng/land-resolve.sh" "$source_root/eng/land-evidence.sh" \
    "$source_root/eng/gate-lock.sh" "$source_root/eng/repin-baseline.mjs" "$source_root/eng/splice-generic.js" \
    "$source_root/eng/receipt-accept.mjs" "$source_root/eng/verify-receipt.mjs" "$source_root/eng/host-baseline.mjs" "$source_root/eng/pre-push-receipt.mjs" "$source_root/eng/quality-baseline-landing.sh" "$source_root/eng/coverage.mjs" "$seed/eng/"
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
  cat > "$seed/eng/test-verify-stub.sh" <<'EOF'
#!/usr/bin/env bash
expected=$(node -p "require('./eng/baselines/host-test-baseline.json').totals.total")
if [ -n "${MOCK_VERIFY_SENTINEL:-}" ]; then printf 'run\n' >> "$MOCK_VERIFY_SENTINEL"; fi
if [ "${WRITE_EXACT_CLONE_EVIDENCE:-0}" = 1 ]; then
  mkdir -p .claude/land-evidence
  cat > .claude/land-evidence/exact-clone-fail.json <<EVIDENCE
{"steps":[{"id":"host-baseline-match","observed":{"total":$MOCK_MEASURED_TOTAL}}]}
EVIDENCE
fi
echo "Failed: 0, Passed: $((MOCK_MEASURED_TOTAL - 2)), Skipped: 2, Total: $MOCK_MEASURED_TOTAL"
[ "$expected" = "$MOCK_MEASURED_TOTAL" ]
EOF
  chmod +x "$seed/eng/test-verify-stub.sh"
  write_baseline "$seed/eng/baselines/host-test-baseline.json" 100
  git_r -C "$seed" add .
  git_r -C "$seed" commit -q -m seed
  git_r -C "$seed" remote add origin "../../$name.git"
  git_r -C "$seed" push -q -u origin main
  git_r --git-dir="$remote" symbolic-ref HEAD refs/heads/main

  git_r -C "$seed" switch -q -c feature
  write_baseline "$seed/eng/baselines/host-test-baseline.json" 102
  echo branch > "$seed/branch-case.txt"
  git_r -C "$seed" add .
  git_r -C "$seed" commit -q -m "branch adds two cases"
  git_r -C "$seed" push -q -u origin feature

  git_r -C "$seed" switch -q main
  write_baseline "$seed/eng/baselines/host-test-baseline.json" 103
  echo main > "$seed/main-case.txt"
  git_r -C "$seed" add .
  git_r -C "$seed" commit -q -m "abc123 landing adds three cases"
  git_r -C "$seed" push -q

  git_r clone -q "$remote" "$runner"
  git_r -C "$runner" config user.name "Land Test"
  git_r -C "$runner" config user.email "land-test@example.invalid"
  local shim="$case_dir/shim" merged="$case_dir_absolute/merged" admin="../admin" verify_sentinel="$case_dir_absolute/verify-ran" binding_sentinel="$case_dir_absolute/pr-binding-ran"
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
if [ "$*" = "rev-parse --show-toplevel" ]; then
  echo .
  exit 0
fi
for arg in "$@"; do
  case "$arg" in
    +refs/receipts/tree/*:refs/receipts/tree/*)
      if [ "$MOCK_RECEIPT" = 1 ]; then
        receipt_ref=${arg#+}
        tree=${receipt_ref#refs/receipts/tree/}
        tree=${tree%%:*}
        receipt=$(printf '{"schemaVersion":1,"repository":"harborline-api","testedTree":"%s","steps":["boundaries","identity-r3","codegen-check","codegen-guard-suite","contracts-typescript","contracts-csharp","localfirst-csharp","rule-engine-conformance","contracts-rust","operator-cli-headless","install-artefact","exact-clone","packages","quality"],"host":"fixture-mac","recordedAt":"%s"}\n' "$tree" "$(date -u +%Y-%m-%dT%H:%M:%SZ)")
        blob=$(printf '%s' "$receipt" | "$REAL_GIT" --git-dir="$MOCK_REMOTE" hash-object -w --stdin)
        "$REAL_GIT" --git-dir="$MOCK_REMOTE" update-ref "refs/receipts/tree/$tree" "$blob"
      fi
      ;;
  esac
done
exec "$REAL_GIT" "$@"
EOF
  cat > "$shim/gh" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  "pr view 7 --json baseRefName --jq .baseRefName") echo main ;;
  "pr view 7 --json headRefName --jq .headRefName") : > "$MOCK_PR_BINDING_SENTINEL"; echo feature ;;
  "pr view 7 --json headRefOid --jq .headRefOid")
    reads=0
    [ -f "$MOCK_PR_HEAD_READS" ] && reads=$(cat "$MOCK_PR_HEAD_READS")
    reads=$((reads + 1))
    printf '%s\n' "$reads" > "$MOCK_PR_HEAD_READS"
    if [ "$reads" -le "$MOCK_PR_HEAD_STALE_READS" ]; then echo "$MOCK_PR_HEAD_OLD"; else git --git-dir="$MOCK_REMOTE" rev-parse refs/heads/feature; fi
    ;;
  "pr view 7 --json statusCheckRollup --jq "*)
    reads=0
    [ -f "$MOCK_CHECKS_READS" ] && reads=$(cat "$MOCK_CHECKS_READS")
    reads=$((reads + 1))
    printf '%s\n' "$reads" > "$MOCK_CHECKS_READS"
    if [ "$reads" -le "$MOCK_CHECKS_PENDING_READS" ]; then echo 2; else echo 0; fi
    ;;
  "pr view 7 --json state --jq .state") [ -f "$MOCK_MERGED" ] && echo MERGED || echo OPEN ;;
  "pr merge 7 --squash --match-head-commit "*)
    source "$FIXTURE_GIT_RETRY"
    git_r clone -q "$MOCK_REMOTE" "$MOCK_ADMIN"
    git_r -C "$MOCK_ADMIN" config user.name "Land Test"
    git_r -C "$MOCK_ADMIN" config user.email "land-test@example.invalid"
    git_r -C "$MOCK_ADMIN" merge -q --squash origin/feature
    if [ "$MOCK_POST_MERGE_GATE" = 1 ]; then
      echo raced > "$MOCK_ADMIN/post-merge-race.txt"
      git_r -C "$MOCK_ADMIN" add post-merge-race.txt
    fi
    git_r -C "$MOCK_ADMIN" commit -q -m landed
    git_r -C "$MOCK_ADMIN" push -q origin main
    : > "$MOCK_MERGED"
    ;;
  *) echo "unexpected gh $*" >&2; exit 99 ;;
esac
EOF
  chmod +x "$shim/node" "$shim/dotnet" "$shim/git" "$shim/gh"
  local shim_path
  shim_path=$(cd "$shim" && pwd)

  export REAL_NODE="$real_node" REAL_GIT="$real_git" FIXTURE_GIT_RETRY="$source_root/eng/tests/fixture-git-retry.sh" MOCK_MEASURED_TOTAL="$measured" MOCK_REMOTE="../../$name.git" MOCK_ADMIN="$admin" MOCK_MERGED="$merged" WRITE_EXACT_CLONE_EVIDENCE="$evidence" MOCK_RECEIPT="$receipt" MOCK_POST_MERGE_GATE="$post_merge_gate" MOCK_VERIFY_SENTINEL="$verify_sentinel" MOCK_PR_BINDING_SENTINEL="$binding_sentinel"
  export MOCK_PR_HEAD_STALE_READS="$stale_reads" MOCK_PR_HEAD_READS="$(cd "$case_dir" && pwd)/pr-head-reads" MOCK_PR_HEAD_OLD="$(git -C "$runner" rev-parse origin/feature)"
  export MOCK_CHECKS_PENDING_READS="$checks_pending_reads" MOCK_CHECKS_READS="$(cd "$case_dir" && pwd)/checks-reads"
  export HARBORLINE_LAND_VERIFY_CMD='bash eng/test-verify-stub.sh'
  export HARBORLINE_GATE_LOCK_PATH='../gate.lock'
  if [ "$name" = retries-stale-pr-head ] || [ "$name" = refuses-permanently-stale-pr-head ]; then
    export LAND_PR_HEAD_RETRIES=3 LAND_PR_HEAD_DELAY=0
  else
    unset LAND_PR_HEAD_RETRIES LAND_PR_HEAD_DELAY
  fi
  if [ "$name" = waits-for-pending-checks ]; then
    export LAND_CHECKS_DELAY=1
    unset LAND_CHECKS_WAIT
  elif [ "$name" = refuses-checks-still-pending ]; then
    export LAND_CHECKS_WAIT=2 LAND_CHECKS_DELAY=1
  else
    unset LAND_CHECKS_WAIT LAND_CHECKS_DELAY
  fi
  if [ "$name" = refuses-regression ]; then
    local feature_before policy_out policy_rc
    feature_before=$(git --git-dir="$remote" rev-parse feature)
    git_r -C "$seed" switch -q feature
    node -e "const fs=require('node:fs'); const p='$seed/eng/baselines/host-test-baseline.json'; const b=JSON.parse(fs.readFileSync(p)); b.branchPolicyFixture=true; fs.writeFileSync(p, JSON.stringify(b, null, 2)+'\n')"
    git_r -C "$seed" add eng/baselines/host-test-baseline.json
    git_r -C "$seed" commit -q -m "branch baseline policy change"
    git_r -C "$seed" push -q origin feature
    policy_out=$(cd "$runner" && PATH="$shim_path:$PATH" bash eng/land.sh feature --pr 7 2>&1); policy_rc=$?
    [ "$policy_rc" = 1 ] && grep -Fq 'branch changed host-baseline policy beyond its pin' <<<"$policy_out" || {
      echo "FAIL $name: branch baseline policy change was not refused"; printf '%s\n' "$policy_out"; return 1;
    }
    git_r -C "$seed" push -q --force origin "$feature_before:feature"
    git_r -C "$runner" fetch -q origin
  fi
  local feature_before
  feature_before=$(git --git-dir="$remote" rev-parse feature)
  local main_before
  main_before=$(git --git-dir="$remote" rev-parse main)
  out=$(cd "$runner" && PATH="$shim_path:$PATH" bash eng/land.sh feature --pr 7 2>&1); rc=$?
  printf '%s\n' "$out" > "$case_dir/output.log"
  if [ "$rc" != "$want_rc" ]; then
    echo "FAIL $name: rc=$rc want=$want_rc"
    tail -20 "$case_dir/output.log"
    return 1
  fi

  if [ "$receipt" = 1 ]; then
    local main_total branch_total feature_after receipt_tree
    receipt_tree=$(grep -Eo 'land: accepting receipt for tree [0-9a-f]{40}' "$case_dir/output.log" | awk '{print $6}')
    [ -n "$receipt_tree" ] || { echo "FAIL $name: missing accepted receipt line"; return 1; }
    git --git-dir="$remote" cat-file -e "refs/receipts/tree/$receipt_tree^{blob}" || { echo "FAIL $name: receipt is not a blob in fixture origin"; return 1; }
    grep -Fq "land: accepting receipt for tree $receipt_tree from fixture-mac at" "$case_dir/output.log" || { echo "FAIL $name: receipt acceptance does not name fixture host"; return 1; }
    main_total=$(git --git-dir="$remote" show main:eng/baselines/host-test-baseline.json | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).totals.total))")
    branch_total=$(git --git-dir="$remote" show feature:eng/baselines/host-test-baseline.json | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).totals.total))")
    feature_after=$(git --git-dir="$remote" rev-parse feature)
    [ "$main_total/$branch_total" = "105/105" ] || { echo "FAIL $name: totals main/branch=$main_total/$branch_total"; return 1; }
    [ "$feature_after" != "$feature_before" ] || { echo "FAIL $name: re-pinned head was not pushed"; return 1; }
    [ -f "$binding_sentinel" ] || { echo "FAIL $name: did not continue to PR binding"; return 1; }
    if [ "$post_merge_gate" = 1 ]; then
      calls=$(tr -d ' ' < <(wc -l < "$verify_sentinel" 2>/dev/null || echo 0))  # BSD wc pads with spaces
      [ "$calls" = 1 ] || { echo "FAIL $name: post-merge verify calls=$calls, want=1"; return 1; }
      grep -Fq 'land: main gated green after the fact' "$case_dir/output.log" || { echo "FAIL $name: post-merge gate did not turn green"; return 1; }
      ! grep -Fq 'run_land_verify: command not found' "$case_dir/output.log" || { echo "FAIL $name: post-merge gate lost run_land_verify"; return 1; }
    else
      [ ! -e "$verify_sentinel" ] || { echo "FAIL $name: accepted receipt ran the verify stub"; return 1; }
      grep -Fq "land: quality baseline comparison skipped on the accepted receipt" "$case_dir/output.log" || { echo "FAIL $name: accepted receipt still ran the quality baseline comparison"; return 1; }
      grep -Fq 'land: main moved: re-pinned arithmetically (main 103, branch delta +2, expected 105); measurement skipped on the accepted receipt (ticket 350)' "$case_dir/output.log" || { echo "FAIL $name: missing measurement-skipped re-pin reason"; return 1; }
    fi
  elif [ "$want_rc" = 0 ]; then
    local main_total branch_total
    main_total=$(git --git-dir="$remote" show main:eng/baselines/host-test-baseline.json | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).totals.total))")
    branch_total=$(git --git-dir="$remote" show feature:eng/baselines/host-test-baseline.json | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).totals.total))")
    [ "$main_total/$branch_total" = "105/105" ] || { echo "FAIL $name: totals main/branch=$main_total/$branch_total"; return 1; }
    grep -Fq 'main moved, measured matches: re-pinned' "$case_dir/output.log" || { echo "FAIL $name: missing re-pin reason"; return 1; }
    if [ "$name" = retries-stale-pr-head ]; then
      [ "$(grep -Fc 'land: waiting for GitHub to see the pushed head' "$case_dir/output.log")" = 2 ] || { echo "FAIL $name: waiting line count was not 2"; return 1; }
      grep -Fxq 'land: waiting for GitHub to see the pushed head (1)' "$case_dir/output.log" || { echo "FAIL $name: missing first waiting line"; return 1; }
      grep -Fxq 'land: waiting for GitHub to see the pushed head (2)' "$case_dir/output.log" || { echo "FAIL $name: missing second waiting line"; return 1; }
    elif [ "$name" = waits-for-pending-checks ]; then
      [ "$(grep -Fc "land: waiting for GitHub's required checks" "$case_dir/output.log")" = 2 ] || { echo "FAIL $name: waiting line count was not 2"; return 1; }
      [ -f "$merged" ] || { echo "FAIL $name: PR was not merged"; return 1; }
    fi
  elif [ "$name" = refuses-permanently-stale-pr-head ]; then
    local pushed_head
    pushed_head=$(git --git-dir="$remote" rev-parse feature)
    grep -Fq "land: PR #7 head ($MOCK_PR_HEAD_OLD) is not the pushed head ($pushed_head); GitHub is stale after 3 reads. Rerun land.sh." "$case_dir/output.log" || { echo "FAIL $name: missing stale GitHub refusal"; return 1; }
  elif [ "$name" = refuses-checks-still-pending ]; then
    grep -Fxq "land: GitHub's required checks are still running after 2s; nothing landed. Rerun land.sh." "$case_dir/output.log" || { echo "FAIL $name: missing required-checks refusal"; return 1; }
    [ ! -e "$merged" ] || { echo "FAIL $name: PR merged despite pending checks"; return 1; }
    [ "$(git --git-dir="$remote" rev-parse main)" = "$main_before" ] || { echo "FAIL $name: origin/main moved despite pending checks"; return 1; }
  elif [ "$name" = refuses-regression ]; then
    local branch_total
    branch_total=$(git --git-dir="$remote" show feature:eng/baselines/host-test-baseline.json | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).totals.total))")
    [ "$branch_total" = 102 ] || { echo "FAIL $name: refused branch moved to $branch_total"; return 1; }
    grep -Fq 'main moved by 3, measured differs by 2!=3: regression (main 103, branch delta +2, measured 104; verify log:' "$case_dir/output.log" || { echo "FAIL $name: missing numeric regression reason"; return 1; }
    grep -Fq 'abc123 landing adds three cases' "$case_dir/output.log" || { echo "FAIL $name: missing intervening landing"; return 1; }
  else
    ! grep -Fq 'measured differs by' "$case_dir/output.log" || { echo "FAIL $name: invented regression from a non-host Total line"; return 1; }
    grep -Fq 'land: gate RED before the host suite ran; no measurement (see ' "$case_dir/output.log" || { echo "FAIL $name: missing no-measurement reason"; return 1; }
  fi
  echo "ok   $name"
}

fails=0
make_case repins-and-lands 105 0 0 || fails=$((fails + 1))
make_case refuses-regression 104 1 1 || fails=$((fails + 1))
make_case no-measurement 104 1 0 || fails=$((fails + 1))
make_case receipt-accepted 105 0 0 1 || fails=$((fails + 1))
make_case receipt-accepted-post-merge-safety 105 3 0 1 1 || fails=$((fails + 1))
make_case retries-stale-pr-head 105 0 0 0 0 2 || fails=$((fails + 1))
make_case refuses-permanently-stale-pr-head 105 1 0 0 0 3 || fails=$((fails + 1))
make_case waits-for-pending-checks 105 0 0 0 0 0 2 || fails=$((fails + 1))
make_case refuses-checks-still-pending 105 1 0 0 0 0 99 || fails=$((fails + 1))
echo "9 cases, $fails failures"
[ "$fails" -eq 0 ]
