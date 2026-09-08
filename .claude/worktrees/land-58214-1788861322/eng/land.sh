#!/usr/bin/env bash
# land.sh <branch> [--pr N] [--dry-run]
#
# The manual bors (R-0006 lesson 4, ticket 242). Tests the MERGE COMMIT before it lands, lands exactly
# that head, then proves main is the tree that was tested.
#   1. fetch (checked); pin BASE=origin/main and HEAD=origin/<branch> by sha
#   2. throwaway worktree at BASE; merge HEAD (no fast-forward). Refuse if HEAD is behind BASE: a
#      squash of an up-to-date head reproduces the merge tree exactly, so that is the only case we land.
#   3. run bash eng/verify.sh there (all 11 steps)
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
if ! git merge-base --is-ancestor "$base_sha" "$head_sha"; then
  echo "land: origin/$branch ($(git rev-parse --short "$head_sha")) is behind origin/main ($(git rev-parse --short "$base_sha")). Merge main into the branch, regate, push, then land."; exit 1
fi
land_dir="$root/.claude/worktrees/land-$$-$(date +%s)"
cleanup() { if [ -d "$land_dir" ]; then git worktree remove --force "$land_dir" >/dev/null 2>&1 || echo "land: WARNING could not remove $land_dir"; fi; gate_lock_release; }
trap cleanup EXIT
git worktree add --detach "$land_dir" "$base_sha" -q
git -C "$land_dir" merge --no-ff --no-edit "$head_sha" >/dev/null 2>&1 || { echo "land: merge conflict between origin/main and $branch"; exit 1; }
tested_tree=$(git -C "$land_dir" rev-parse 'HEAD^{tree}')
head_tree=$(git rev-parse "$head_sha^{tree}")
[ "$tested_tree" = "$head_tree" ] || { echo "land: internal: merge tree != head tree although head is a descendant of main"; exit 1; }
echo "land: gating tree ${tested_tree:0:12} (base $(git rev-parse --short "$base_sha"), head $(git rev-parse --short "$head_sha"))"
( cd "$land_dir" && dotnet restore apps/local-node-host/tests/tests.csproj >/dev/null 2>&1 && ( for d in apps/capability-host; do [ -d "$d" ] && ( cd "$d" && npm ci --silent --no-audit --no-fund >/dev/null 2>&1 ) || true; done ) && bash eng/verify.sh ) && ( cd "$land_dir" && node eng/verify-receipt.mjs --landing ) || { preserve_land_evidence "$root" "$land_dir" "$head_sha" || echo "land: WARNING could not preserve exact-clone evidence" >&2; echo "land: gate RED on the merge commit; nothing landed"; exit 1; }
if [ $dry -eq 1 ]; then echo "land: dry run — gate green on ${tested_tree:0:12}; not landing"; exit 0; fi
if [ -z "$pr" ]; then
  pr=$(gh pr list --head "$branch" --base main --state open --json number --jq '.[0].number // empty')
  [ -n "$pr" ] || { echo "land: no open PR for $branch against main — open it first so the description is reviewed"; exit 1; }
fi
pr_base=$(gh pr view "$pr" --json baseRefName --jq .baseRefName)
pr_head=$(gh pr view "$pr" --json headRefOid --jq .headRefOid)
pr_head_ref=$(gh pr view "$pr" --json headRefName --jq .headRefName)
[ "$pr_base" = "main" ] || { echo "land: PR #$pr targets '$pr_base', not main"; exit 1; }
[ "$pr_head_ref" = "$branch" ] || { echo "land: PR #$pr head is '$pr_head_ref', not $branch"; exit 1; }
[ "$pr_head" = "$head_sha" ] || { echo "land: PR #$pr head ($pr_head) is not the gated head ($head_sha); the branch moved after the gate. Rerun land.sh."; exit 1; }
git fetch -q origin || { echo "land: fetch failed before landing; refusing"; exit 1; }
[ "$(git rev-parse origin/main)" = "$base_sha" ] || { echo "land: origin/main moved during the gate ($(git rev-parse --short origin/main) != $(git rev-parse --short "$base_sha")); nothing landed. Merge main into the branch, regate, rerun."; exit 1; }
# shellcheck source=land-resolve.sh
source "$root/eng/land-resolve.sh"
gate_main='verify_dir="$root/.claude/worktrees/land-verify-$$"; git worktree add --detach "$verify_dir" origin/main -q && ( cd "$verify_dir" && dotnet restore apps/local-node-host/tests/tests.csproj >/dev/null 2>&1 && bash eng/verify.sh ) && ( cd "$verify_dir" && node eng/verify-receipt.mjs --landing ); rc=$?; git worktree remove --force "$verify_dir" >/dev/null 2>&1 || true; [ $rc -eq 0 ]'
land_request_and_resolve "$pr" "$base_sha" "$head_sha" "$tested_tree" "$gate_main"
exit $?
