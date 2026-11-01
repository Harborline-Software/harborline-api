#!/usr/bin/env bash
# review-gate: one commit status on a PR's head that says whether the PR has the review PROC-0001 asks for.
# "Review policy, 2026-11-01": one clean pass from any of the Claude review, Codex or CodeRabbit, and no
# blocker from any reviewer that ran. A risky change also needs a clean Codex security review.
#
# Usage: review-gate.sh <pr-number>. Needs GH_TOKEN with statuses:write and pull-requests:read, and
# GITHUB_REPOSITORY. DRY_RUN=1 prints the status instead of setting it. NO_GRAPHQL=1 skips the thread check.
set -euo pipefail

PR=$1
REPO=${GITHUB_REPOSITORY:?}
CONTEXT=review-gate
RISKY=${RISKY_PATHS:-'^\.github/'}
RUN_URL=${GITHUB_SERVER_URL:-https://github.com}/$REPO/actions/runs/${GITHUB_RUN_ID:-0}

pr=$(gh api "repos/$REPO/pulls/$PR")
[ "$(jq -r .state <<<"$pr")" = open ] || { echo "PR $PR is not open"; exit 0; }
[ "$(jq -r .draft <<<"$pr")" = false ] || { echo "PR $PR is a draft"; exit 0; }
HEAD=$(jq -r .head.sha <<<"$pr")
OWNER=${REPO%%/*}; NAME=${REPO#*/}

passes=(); blockers=()

# Claude review: the review workflow sets the claude-review status on the head it reviewed.
# success = PASS; failure = BLOCK or ESCALATE; error or missing = it did not run.
# The combined status holds only the latest status per context, so repeated review-gate posts can't push it out.
latest=$(gh api "repos/$REPO/commits/$HEAD/status?per_page=100" --paginate --jq '.statuses[]
  | select(.context == "claude-review" or .context == "review-gate") | "\(.context)|\(.state)|\(.description // "")"')
claude=$(grep '^claude-review|' <<<"$latest" | head -n 1 | cut -d'|' -f2- || true)
claude=${claude:-none|}
claude_state=${claude%%|*}; claude_desc=${claude#*|}
case $claude_state in
  success) passes+=("Claude") ;;
  failure) blockers+=("Claude review: ${claude_desc:-not PASS}") ;;
esac
escalated=false; [[ $claude_desc == *ESCALATE* ]] && escalated=true

# Codex: its summary comment carries a codex-review-state JSON, with braces written as &#123; and &#125;.
codex=$(gh api "repos/$REPO/issues/$PR/comments?per_page=100" --paginate \
  --jq '.[] | select(.user.login == "chatgpt-codex-connector[bot]") | .body' \
  | grep -o 'codex-review-state:v1 [^>]*-->' | tail -n 1 \
  | sed -E 's/^codex-review-state:v1 //; s/ -->$//; s/&#123;/{/g; s/&#125;/}/g; s/&quot;/"/g' || true)
codex_code=none; codex_security=none
if [ -n "$codex" ] && jq -e . >/dev/null 2>&1 <<<"$codex"; then
  # clean: finished on this head with no findings; dirty: finished on this head with unresolved findings.
  judge='if .commit_sha != $h or .status != "completed" then "none"
         elif .has_unresolved_findings == true then "dirty"
         elif .has_any_findings == true then "resolved" else "clean" end'
  codex_code=$(jq -r --arg h "$HEAD" ".code_review | $judge" <<<"$codex")
  codex_security=$(jq -r --arg h "$HEAD" ".security_review | $judge" <<<"$codex")
fi
[ "$codex_code" = dirty ] && blockers+=("Codex code review has open findings")
[ "$codex_security" = dirty ] && blockers+=("Codex security review has open findings")
[ "$codex_code" = clean ] && [ "$codex_security" = clean ] && passes+=("Codex")

# CodeRabbit: its latest review on this head. APPROVED passes; CHANGES_REQUESTED blocks.
rabbit=$(gh api "repos/$REPO/pulls/$PR/reviews?per_page=100" --paginate \
  --jq ".[] | select(.user.login == \"coderabbitai[bot]\" and .commit_id == \"$HEAD\") | .state" \
  | grep -E '^(APPROVED|CHANGES_REQUESTED|DISMISSED)$' | tail -n 1 || true)
case $rabbit in
  APPROVED) passes+=("CodeRabbit") ;;
  CHANGES_REQUESTED) blockers+=("CodeRabbit requested changes") ;;
esac

# Open review threads from any of the reviewers, on lines the head still has, are findings nobody settled.
if [ "${NO_GRAPHQL:-}" != 1 ]; then
  # --paginate follows pageInfo/$endCursor, printing one count per page; awk adds them up.
  open_threads=$(gh api graphql --paginate -F owner="$OWNER" -F name="$NAME" -F pr="$PR" -f query='
    query($owner: String!, $name: String!, $pr: Int!, $endCursor: String) {
      repository(owner: $owner, name: $name) { pullRequest(number: $pr) {
        reviewThreads(first: 100, after: $endCursor) {
          nodes { isResolved isOutdated comments(first: 1) { nodes { author { login } } } }
          pageInfo { hasNextPage endCursor }
        }
      } } }' --jq '[.data.repository.pullRequest.reviewThreads.nodes[]
        | select((.isResolved | not) and (.isOutdated | not))
        | select(.comments.nodes[0].author.login | test("^(coderabbitai|chatgpt-codex-connector|github-actions)"))] | length' \
    | awk '{ n += $1 } END { print n + 0 }')
  [ "$open_threads" -gt 0 ] && blockers+=("$open_threads open review thread(s)")
fi

# A risky change also needs Codex's security review, clean on this head, whatever else passed.
risky=$escalated
# Read the whole listing first: grep -q on a pipe can exit early, and with pipefail the broken pipe would read as
# "no match". A failed listing stops the script, so the gate keeps its last status rather than passing.
files=$(gh api "repos/$REPO/pulls/$PR/files?per_page=100" --paginate --jq '.[].filename')
if grep -qE "$RISKY" <<<"$files"; then risky=true; fi
if $risky && [ "$codex_security" != clean ]; then
  need_security=true; else need_security=false; fi

if [ ${#blockers[@]} -gt 0 ]; then
  state=failure; desc="Blocked: $(IFS=';'; echo "${blockers[*]}")"
elif [ ${#passes[@]} -eq 0 ]; then
  state=pending; desc="Waiting for one clean review from Claude, Codex or CodeRabbit"
elif $need_security; then
  state=pending; desc="Passed by ${passes[*]}; a risky change also needs a clean Codex security review"
else
  state=success; desc="Passed by ${passes[*]}"
fi
desc=${desc:0:140}

echo "PR $PR head $HEAD: $state: $desc (claude=$claude_state codex=$codex_code/$codex_security coderabbit=${rabbit:-none} risky=$risky)"
if [ "${DRY_RUN:-}" = 1 ]; then exit 0; fi
# The sweep re-checks every 20 minutes; posting an identical status each time only adds noise.
if grep -qxF "$CONTEXT|$state|$desc" <<<"$latest"; then echo "review-gate unchanged"; exit 0; fi
gh api -X POST "repos/$REPO/statuses/$HEAD" -f state="$state" -f context="$CONTEXT" \
  -f description="$desc" -f target_url="$RUN_URL" >/dev/null
