#!/usr/bin/env bash
# Drives a mocked red merge-tree gate, then exercises the production evidence-preservation helper.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=../land-evidence.sh
source "$here/../land-evidence.sh"

repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
land_dir="$repo/.claude/worktrees/land-test"
head_sha=abc123def456

red_gate() {
  mkdir -p "$land_dir/.claude/land-evidence"
  cat > "$land_dir/.claude/land-evidence/exact-clone-fail.json" <<'JSON'
{
  "status": "FAIL",
  "steps": [
    {"id": "analyzer-canary", "passed": false, "tail": "first analyzer line\nlast analyzer line"},
    {"id": "boundary-check", "passed": false, "tail": "boundary detail"},
    {"id": "capability-typecheck", "passed": true, "tail": "not printed"}
  ]
}
JSON
  return 1
}

fails=0
if red_gate; then
  echo "FAIL mock gate unexpectedly passed"
  fails=$((fails+1))
else
  out=$(preserve_land_evidence "$repo" "$land_dir" "$head_sha" 2>&1); rc=$?
  [ "$rc" -eq 0 ] || { echo "FAIL helper rc=$rc :: $out"; fails=$((fails+1)); }
fi

copies=("$repo"/.claude/land-evidence/*-"$head_sha".json)
[ "${#copies[@]}" -eq 1 ] && [ -f "${copies[0]}" ] || { echo "FAIL preserved evidence copy missing"; fails=$((fails+1)); }
grep -q 'analyzer-canary:' <<<"$out" && grep -q '  first analyzer line' <<<"$out" && grep -q '  last analyzer line' <<<"$out" \
  || { echo "FAIL analyzer tail missing :: $out"; fails=$((fails+1)); }
grep -q 'boundary-check:' <<<"$out" && grep -q '  boundary detail' <<<"$out" \
  || { echo "FAIL boundary tail missing :: $out"; fails=$((fails+1)); }
! grep -q 'not printed' <<<"$out" || { echo "FAIL passing step tail was printed :: $out"; fails=$((fails+1)); }

if [ "$fails" -eq 0 ]; then echo "ok   red gate preserves evidence and prints failed tails"; fi
[ "$fails" -eq 0 ]
