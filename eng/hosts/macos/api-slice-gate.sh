#!/bin/bash
# macpro api slice gate: api-slice-gate.sh <branch> [--merge]
#
#   (default)  gate the BRANCH HEAD in the api checkout. Slice-only receipt under the macOS baseline.
#   --merge    gate the MERGE TREE (ticket 350): merge the branch into origin/main in a throwaway
#              worktree, run eng/verify.sh there, and on green publish the receipt as
#              refs/receipts/tree/<tree> so eng/land.sh on Windows can land without a second gate.
#              The merge tree is what actually lands, so it is the only tree a landing may trust.
#
# Versioned here (installed to ~/bin on macpro). bash 3.2 / BSD tools: no `timeout`, no mapfile,
# no associative arrays, no `sed -i` without an argument.
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/tools/node/bin:$HOME/.dotnet:$HOME/.dotnet/tools:$HOME/.cargo/bin:$HOME/.local/bin:$PATH
export DEVELOPER_DIR=/Library/Developer/CommandLineTools DOTNET_CLI_TELEMETRY_OPTOUT=1
export HARBORLINE_CONTROL_REPO=$HOME/Projects/Harborline/lanes/control-gh
export HARBORLINE_PLATFORM_REPO=$HOME/Projects/Harborline/lanes/platform-pin
export HARBORLINE_QUALITY_REPO=$HOME/Projects/Harborline/lanes/quality-pin
branch=$1; [ -n "$branch" ] || { echo "usage: api-slice-gate.sh <branch> [--merge]"; exit 2; }
mode=head; [ "$2" = "--merge" ] && mode=merge
if [ "$(who | awk '{print $1}' | sort -u | wc -l)" -gt 1 ]; then echo "REFUSED: another console user is present"; who; exit 3; fi
api=$HOME/Projects/Harborline/lanes/api-public
cd "$api" || exit 9
git fetch -q origin || exit 4

gate_dir=$api
cleanup() { :; }
if [ "$mode" = merge ]; then
  base_sha=$(git rev-parse origin/main) || exit 5
  head_sha=$(git rev-parse "origin/$branch") || { echo "REFUSED: origin/$branch does not exist; push the branch first"; exit 5; }
  # Same refusal eng/land.sh makes, for the same reason: only an up-to-date head has a merge tree
  # the squash reproduces exactly, so a behind head's receipt would attest to a tree that never lands.
  if ! git merge-base --is-ancestor "$base_sha" "$head_sha"; then
    echo "REFUSED: origin/$branch ($(git rev-parse --short "$head_sha")) is behind origin/main ($(git rev-parse --short "$base_sha")). Merge main into the branch, push, then regate."
    exit 5
  fi
  gate_dir="$api/../api-merge-gate-$$"
  cleanup() { git -C "$api" worktree remove --force "$gate_dir" >/dev/null 2>&1 || echo "warning: could not remove $gate_dir"; }
  trap cleanup EXIT
  git worktree add --detach "$gate_dir" "$base_sha" -q || exit 5
  git -C "$gate_dir" merge --no-ff --no-edit "$head_sha" >/dev/null 2>&1 || { echo "REFUSED: merge conflict between origin/main and $branch"; exit 5; }
  merge_tree=$(git -C "$gate_dir" rev-parse 'HEAD^{tree}')
  echo "MERGE base $(git rev-parse --short "$base_sha") head $(git rev-parse --short "$head_sha") tree $merge_tree"
else
  git checkout -q -B "slice/$branch" "origin/$branch" || exit 5
fi
cd "$gate_dir" || exit 9
echo "START $(date '+%F %T') branch $branch mode $mode HEAD $(git rev-parse --short HEAD)"

# The control policy root and the quality tool pin must be current: a stale clone reds the boundaries step (339 s4, 2026-09-08).
git -C "$HARBORLINE_CONTROL_REPO" pull -q --ff-only origin main 2>/dev/null || echo "warning: control-gh pull failed"
qpin=$(node -e "try{console.log(JSON.parse(require('fs').readFileSync('eng/quality-pin.json','utf8')).commit)}catch(e){console.log('')}")
if [ -n "$qpin" ] && [ -d "$HARBORLINE_QUALITY_REPO/.git" ]; then git -C "$HARBORLINE_QUALITY_REPO" fetch -q origin 2>/dev/null; git -C "$HARBORLINE_QUALITY_REPO" checkout -q --detach "$qpin" 2>/dev/null || echo "warning: quality pin $qpin not checked out"; fi
pin=$(node -e "console.log(JSON.parse(require('fs').readFileSync('eng/platform-pin.json','utf8')).commit)")
if [ ! -d "$HARBORLINE_PLATFORM_REPO/.git" ]; then git clone -q "$HOME/Projects/Harborline/lanes/platform-gh" "$HARBORLINE_PLATFORM_REPO" && git -C "$HARBORLINE_PLATFORM_REPO" remote set-url origin https://github.com/Harborline-Software/harborline-platform.git; fi
git -C "$HARBORLINE_PLATFORM_REPO" fetch -q origin && git -C "$HARBORLINE_PLATFORM_REPO" checkout -q --detach "$pin" && git -C "$HARBORLINE_PLATFORM_REPO" clean -fdxq || { echo "platform pin checkout failed ($pin)"; exit 6; }
s=$(date +%s); node eng/build-local-feed.mjs >/dev/null 2>&1; echo "FEED EXIT $? after $(( $(date +%s)-s ))s"
s=$(date +%s); nice -n 10 bash eng/verify.sh; rc=$?; echo "VERIFY EXIT $rc after $(( $(date +%s)-s ))s"

if [ "$mode" = merge ] && [ $rc -eq 0 ]; then
  HARBORLINE_RECEIPT_HOST=${HARBORLINE_RECEIPT_HOST:-$(hostname -s)} bash eng/gate-receipt-ref.sh "$merge_tree" || { echo "RECEIPT PUBLISH FAILED"; rc=7; }
  [ $rc -eq 0 ] && echo "RECEIPT refs/receipts/tree/$merge_tree published for tree $merge_tree"
fi
echo "END $(date '+%F %T')"
exit $rc
