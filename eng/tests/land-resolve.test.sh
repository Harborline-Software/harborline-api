#!/usr/bin/env bash
# Enumerates the landing-outcome matrix for eng/land-resolve.sh with mocked `git` and `gh`:
#   {gh merge exit 0 | nonzero} x {server merged | not merged} x {base unchanged | base advanced}
# Invariant: every case where something landed reaches the tree proof (return 0 when the landed tree is
# the gated tree, 3 otherwise, never "nothing landed"); the only "nothing landed" (return 1) is
# PR-not-merged AND base unchanged. The gh exit code must not influence the outcome at all.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=../land-resolve.sh
source "$here/../land-resolve.sh"

BASE=aaaa1111; TESTED=tree-gated; OTHER=tree-other
shim=$(mktemp -d)
cat > "$shim/git" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  "fetch -q origin") exit 0 ;;
  "rev-parse origin/main") echo "$MOCK_MAIN_SHA" ;;
  "rev-parse origin/main^{tree}") echo "$MOCK_MAIN_TREE" ;;
  "rev-parse --short "*) echo "${3:0:7}" ;;
  *) echo "unexpected git $*" >&2; exit 99 ;;
esac
EOF
cat > "$shim/gh" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  "pr view 7 --json state --jq .state") echo "$MOCK_PR_STATE" ;;
  "pr merge 7 --squash --match-head-commit headsha1") echo "gh: merge request (mock exit $MOCK_GH_MERGE_EXIT)"; exit "$MOCK_GH_MERGE_EXIT" ;;
  *) echo "unexpected gh $*" >&2; exit 99 ;;
esac
EOF
chmod +x "$shim/git" "$shim/gh"
export PATH="$shim:$PATH"

fails=0; n=0
check() { # name expected_rc merged base_advanced landed_tree  (gh exit comes from $GHEXIT)
  local name=$1 want=$2 merged=$3 advanced=$4 tree=$5
  export MOCK_PR_STATE=$([ "$merged" = yes ] && echo MERGED || echo OPEN)
  export MOCK_MAIN_SHA=$([ "$advanced" = yes ] && echo bbbb2222 || echo $BASE)
  export MOCK_MAIN_TREE=$tree
  export MOCK_GH_MERGE_EXIT=$GHEXIT
  # drive the PRODUCTION orchestration seam (merge request + resolve), not the resolver alone
  out=$(land_request_and_resolve 7 $BASE headsha1 $TESTED 'echo GATE-RAN; true' 2>&1); rc=$?
  grep -q "merge request (mock exit $GHEXIT)" <<<"$out" || { echo "FAIL $name: merge request was not sent"; fails=$((fails+1)); }
  n=$((n+1))
  if [ "$rc" != "$want" ]; then echo "FAIL $name: rc=$rc want=$want :: $out"; fails=$((fails+1)); else echo "ok   $name (rc=$rc)"; fi
  if [ "$rc" = 3 ] && ! grep -q GATE-RAN <<<"$out"; then echo "FAIL $name: rc 3 but main was not gated"; fails=$((fails+1)); fi
  if [ "$rc" != 1 ] && grep -q "nothing landed" <<<"$out"; then echo "FAIL $name: claimed nothing landed on rc=$rc"; fails=$((fails+1)); fi
}
for GHEXIT in 0 1; do   # the gh exit code must not change any outcome; both rows must agree
  check "gh=$GHEXIT merged base-unchanged tree=gated"      0 yes no  $TESTED
  check "gh=$GHEXIT merged base-advanced tree=gated"       0 yes yes $TESTED
  check "gh=$GHEXIT merged base-advanced tree=other"       3 yes yes $OTHER
  check "gh=$GHEXIT merged base-unchanged tree=other"      3 yes no  $OTHER
  check "gh=$GHEXIT not-merged base-unchanged"             1 no  no  $TESTED
  check "gh=$GHEXIT not-merged base-advanced tree=other"   3 no  yes $OTHER
  check "gh=$GHEXIT not-merged base-advanced tree=gated"   0 no  yes $TESTED
done
echo "$n cases, $fails failures"
rm -rf "$shim"
[ $fails -eq 0 ]
