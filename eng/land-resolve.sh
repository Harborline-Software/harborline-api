# land-resolve.sh — sourced by eng/land.sh; also sourced by eng/tests/land-resolve.test.sh with mocked git/gh.
#
# resolve_landing <pr> <base_sha> <tested_tree> <gate_cmd>
#   Called AFTER a `gh pr merge` request has been sent, whatever its exit code. A nonzero gh exit is not
#   proof that nothing landed (the server may have merged and the response been lost), so the remote
#   outcome is resolved from the remote itself:
#     - PR state MERGED, or origin/main moved off <base_sha>  => something landed:
#         origin/main tree == <tested_tree>  -> "landed and proven", return 0
#         otherwise                          -> gate main now with <gate_cmd>; print revert if red; return 3
#     - PR not merged and origin/main still at <base_sha>       => "nothing landed", return 1
#   Every path that observes a landed tree reaches the tree proof; none claims "nothing landed" without
#   checking the remote.
resolve_landing() {
  local pr=$1 base_sha=$2 tested_tree=$3 gate_cmd=$4
  git fetch -q origin || { echo "land: fetch failed while resolving the landing; assume it MAY have landed — run: git fetch origin && git rev-parse origin/main^{tree} and compare to ${tested_tree:0:12}"; return 3; }
  local state main_sha main_tree
  state=$(gh pr view "$pr" --json state --jq .state 2>/dev/null || echo UNKNOWN)
  main_sha=$(git rev-parse origin/main)
  main_tree=$(git rev-parse 'origin/main^{tree}')
  if [ "$state" != "MERGED" ] && [ "$main_sha" = "$base_sha" ]; then
    echo "land: PR #$pr is $state and origin/main is still $(git rev-parse --short "$base_sha") — nothing landed"
    return 1
  fi
  if [ "$main_tree" = "$tested_tree" ]; then
    echo "land: origin/main $(git rev-parse --short "$main_sha") == tested tree ${tested_tree:0:12} — landed and proven"
    return 0
  fi
  echo "land: WARNING origin/main tree ${main_tree:0:12} != tested tree ${tested_tree:0:12} (PR #$pr state $state). main holds a tree that was not gated. Gating main now."
  if eval "$gate_cmd"; then
    echo "land: main gated green after the fact (tree ${main_tree:0:12}); exit 3 so the race stays visible"
  else
    echo "land: main is RED. Revert now:  gh pr view $pr --json mergeCommit --jq .mergeCommit.oid  → git revert <sha> on main (or revert the other landing) and regate."
  fi
  return 3
}

# land_request_and_resolve <pr> <base_sha> <head_sha> <tested_tree> <gate_cmd>
#   The production orchestration seam: sends the merge request and ALWAYS resolves the outcome from the
#   remote afterwards, whatever gh's exit code. land.sh calls this; eng/tests/land-resolve.test.sh drives
#   this same function with mocked git/gh across {gh exit 0|nonzero} x {merged|not} x {base unchanged|advanced},
#   so a mutation that resolves only on gh success (or not at all) fails the enumeration.
land_request_and_resolve() {
  local pr=$1 base_sha=$2 head_sha=$3 tested_tree=$4 gate_cmd=$5
  if ! gh pr merge "$pr" --squash --match-head-commit "$head_sha"; then
    echo "land: gh merge returned nonzero — resolving the remote outcome before believing it"
  fi
  resolve_landing "$pr" "$base_sha" "$tested_tree" "$gate_cmd"
}
