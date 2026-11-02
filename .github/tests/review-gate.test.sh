#!/usr/bin/env bash
# Tests for .github/review-gate.sh's risky-path rules: which list judges a PR, and that a PR can't swap it out.
# Runs the real script in dry-run mode against a stand-in `gh` that serves one PR. Needs bash and jq.
# Usage: bash .github/tests/review-gate.test.sh
set -euo pipefail

gate=$(cd "$(dirname "$0")/.." && pwd)/review-gate.sh
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"

# The stand-in serves PR 1 into $BASE, changing $FILES, with a Claude pass and no other review. $PROTECTED says
# whether a ruleset requires pull requests on the base (yes, no, or error), and $BASE_LIST is the risky list that
# branch carries (a file, or absent). Every call is logged so a test can check what the gate fetched.
cat >"$work/bin/gh" <<'EOF'
#!/usr/bin/env bash
echo "$*" >>"$LOG"
case "$*" in
  *"/rules/branches/"*)
    case $PROTECTED in yes) echo 1 ;; no) echo 0 ;; *) echo "HTTP 502" >&2; exit 1 ;; esac ;;
  *"/contents/.github/review-risky-paths"*)
    if [ -f "${BASE_LIST:-}" ]; then cat "$BASE_LIST"; else echo "HTTP 404" >&2; exit 1; fi ;;
  *"/pulls/1/files"*) printf '%s\n' $FILES ;;
  *"/pulls/1/reviews"*|*"/issues/1/events"*|*"/issues/1/comments"*) : ;;
  *"/pulls/1"*)
    jq -n --arg base "$BASE" '{state: "open", draft: false, head: {sha: "abc"}, changed_files: 1,
                               base: {ref: $base, repo: {default_branch: "main"}}}' ;;
  *"/commits/abc/status"*) echo "claude-review|success|2026-11-01T00:00:00Z|Claude review: PASS (shadow, base $BASE)" ;;
  *"pulls?state=open"*) echo 0 ;;
  *) echo "unexpected gh call: $*" >&2; exit 1 ;;
esac
EOF
chmod +x "$work/bin/gh"

failures=0
# run <name> <want> VAR=value...: <want> is risky=true, risky=false or "fails".
run() {
  local name=$1 want=$2; shift 2
  local log="$work/$name.log" out status=0
  : >"$log"
  out=$(env PATH="$work/bin:$PATH" LOG="$log" GITHUB_REPOSITORY=o/r DRY_RUN=1 NO_GRAPHQL=1 \
          BASE=main PROTECTED=no FILES=src/App.cs "$@" bash "$gate" 1 2>&1) || status=$?
  local got
  if [ "$status" -ne 0 ]; then got=fails; else got=$(grep -o 'risky=[a-z]*' <<<"$out" | tail -n 1); fi
  if [ "$got" = "$want" ]; then echo "ok   $name"; else echo "FAIL $name: want $want, got $got"; echo "$out" | sed 's/^/     /'; failures=$((failures + 1)); fi
  LAST_LOG=$log
}
fetched_base_list() { grep -q '/contents/.github/review-risky-paths' "$LAST_LOG"; }

# A path main's list covers (its first entry, or a file inside it if the entry is a folder), and an edited list that
# leaves it out: what a rules file on some other branch might say.
listed=$(sed -e 's/#.*//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' "$(dirname "$gate")/review-risky-paths" | grep -m 1 .)
case $listed in */) listed=${listed}x.sh ;; esac
printf 'docs/\n' >"$work/edited-list"

run 'main: a change under a listed path is risky' risky=true FILES="$listed"
fetched_base_list && { echo "FAIL main: fetched a base list"; failures=$((failures + 1)); }
run 'main: a product change is not risky' risky=false FILES=src/App.cs
run 'main: a nested Directory.Build.targets is risky' risky=true FILES=src/Lib/Directory.Build.targets
run 'main: a lower-case nested directory.packages.props is risky' risky=true FILES=tests/x/directory.packages.props

# The case the gate exists for: a PR aimed at an unprotected branch that carries an edited rules file is still
# judged by main's list, and the gate never reads that branch's list.
run 'unprotected base with an edited list: main list applies' risky=true \
  BASE=feature/edited-gate PROTECTED=no BASE_LIST="$work/edited-list" FILES="$listed"
fetched_base_list && { echo "FAIL unprotected base: read the base branch's list"; failures=$((failures + 1)); }

# A protected base's own list does apply, and if it can't be read the PR stays risky.
run 'protected base: its own list applies' risky=false \
  BASE=release/1.0 PROTECTED=yes BASE_LIST="$work/edited-list" FILES="$listed"
fetched_base_list || { echo "FAIL protected base: did not read the base branch's list"; failures=$((failures + 1)); }
run 'protected base whose list cannot be read: risky' risky=true \
  BASE=release/1.0 PROTECTED=yes BASE_LIST= FILES=src/App.cs

# If the gate can't tell whether the base is protected, it stops (and the workflow marks the head errored).
run 'protection lookup fails: the gate fails' fails BASE=feature/x PROTECTED=error FILES=src/App.cs

[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "all passed"
