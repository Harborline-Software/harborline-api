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
# Changes that also need a clean Codex security review: CI and its scripts, and the pinned SDK and tool versions.
RISKY=${RISKY_PATHS:-'^(\.github/|global\.json$|\.config/dotnet-tools\.json$)'}
RUN_URL=${GITHUB_SERVER_URL:-https://github.com}/$REPO/actions/runs/${GITHUB_RUN_ID:-0}

# Armed before the first lookup: if anything fails, mark the gate as errored rather than leave an older status
# (maybe a success) standing. Until the PR is read, HEAD is the head the event named, if it named one.
HEAD=${EVENT_HEAD:-}
posted=false
on_exit() {
  local rc=$?
  if [ "$rc" -ne 0 ] && [ "${DRY_RUN:-}" != 1 ] && ! $posted && [ -n "$HEAD" ]; then
    gh api -X POST "repos/$REPO/statuses/$HEAD" -f state=error -f context="$CONTEXT" \
      -f description="review-gate could not finish; see the run" -f target_url="$RUN_URL" >/dev/null || true
  fi
}
trap on_exit EXIT

pr=$(gh api "repos/$REPO/pulls/$PR")
[ "$(jq -r .state <<<"$pr")" = open ] || { echo "PR $PR is not open"; exit 0; }
[ "$(jq -r .draft <<<"$pr")" = false ] || { echo "PR $PR is a draft"; exit 0; }
HEAD=$(jq -r .head.sha <<<"$pr")
BASE_REF=$(jq -r .base.ref <<<"$pr")
OWNER=${REPO%%/*}; NAME=${REPO#*/}

passes=(); blockers=(); stale=()

# A pass given before the PR's base last changed reviewed another diff, so it doesn't count. Blockers still do.
rebased_at=$(gh api "repos/$REPO/issues/$PR/events?per_page=100" --paginate \
  --jq '.[] | select(.event == "base_ref_changed" or .event == "automatic_base_change_succeeded") | .created_at')
rebased_at=$(sort <<<"$rebased_at" | tail -n 1)
fresh() { [[ -z $rebased_at || $1 > "$rebased_at" ]]; }   # ISO 8601 UTC times sort as text

# Claude review: the review workflow sets the claude-review status on the head it reviewed.
# success = PASS; failure = BLOCK or ESCALATE; error or missing = it did not run.
# The combined status holds only the latest status per context, so repeated review-gate posts can't push it out.
statuses=$(gh api "repos/$REPO/commits/$HEAD/status?per_page=100" --paginate --jq '.statuses[]
  | select(.context == "claude-review" or .context == "review-gate")
  | "\(.context)|\(.state)|\(.updated_at)|\(.description // "")"')
latest=$(cut -d'|' -f1,2,4- <<<"$statuses")
claude_at=$(awk -F'|' '$1 == "claude-review" { print $3; exit }' <<<"$statuses")
claude=$( { grep '^claude-review|' <<<"$latest" || true; } | head -n 1 | cut -d'|' -f2-)
claude=${claude:-none|}
claude_state=${claude%%|*}; claude_desc=${claude#*|}
case $claude_state in
  # A pass counts only for the base it reviewed: the review records the PR's base branch in its description, so a
  # review that finished after a retarget, or one given to another PR with the same head, doesn't count here.
  success) if fresh "$claude_at" && [[ $claude_desc == *", base $BASE_REF)" ]]; then passes+=("Claude")
           else stale+=("Claude"); fi ;;
  failure) blockers+=("${claude_desc:-Claude review: not PASS}") ;;
esac
escalated=false; [[ $claude_desc == *ESCALATE* ]] && escalated=true

# Codex: its summary comment carries a codex-review-state JSON, with braces written as &#123; and &#125;, and a
# table row per review with the time that review completed. The verdicts and their times are read from one copy of
# that comment, so an edit between two reads can't pair an old verdict with a new time.
# The listing is read in full before it is searched, so a failed request stops the script (and the gate reads as
# errored) instead of looking like a reviewer that never ran.
codex_comments=$(gh api "repos/$REPO/issues/$PR/comments?per_page=100" --paginate \
  --jq '.[] | select(.user.login == "chatgpt-codex-connector[bot]") | .body | @json')
codex_body=$( { grep -F 'codex-review-state:v1' <<<"$codex_comments" || true; } | tail -n 1)
[ -n "$codex_body" ] && codex_body=$(jq -r . <<<"$codex_body")
codex=$( { grep -o 'codex-review-state:v1 [^>]*-->' <<<"$codex_body" || true; } | tail -n 1 \
  | sed -E 's/^codex-review-state:v1 //; s/ -->$//; s/&#123;/{/g; s/&#125;/}/g; s/&quot;/"/g')
# When each review completed, from its own row; empty if the row or its time can't be read, which counts as stale.
codex_time() { { grep -F "**$1**" <<<"$codex_body" || true; } | head -n 1 | sed -nE 's/.*datetime="([^"]+)".*/\1/p'; }
codex_code_at=$(codex_time "Code Review"); codex_security_at=$(codex_time "Security Review")
codex_code=none; codex_security=none
if [ -n "$codex" ] && jq -e . >/dev/null 2>&1 <<<"$codex"; then
  # clean: finished on this head with no findings; dirty: finished on this head with unresolved findings.
  judge='if .commit_sha != $h or .status != "completed" then "none"
         elif .has_unresolved_findings == true then "dirty"
         elif .has_any_findings == true then "resolved" else "clean" end'
  codex_code=$(jq -r --arg h "$HEAD" ".code_review | $judge" <<<"$codex")
  codex_security=$(jq -r --arg h "$HEAD" ".security_review | $judge" <<<"$codex")
fi
# Each clean verdict must have completed after the last base change on its own, so rerunning one review after a
# retarget doesn't refresh the other.
# A review that started before a retarget can finish after it, and the summary keeps no start time. So after a base
# change a Codex verdict counts only if the head itself was pushed after the change: then any review of it began
# later too. The push time is when GitHub first made a check suite for the head, which no one can set.
codex_was_stale=false
head_after_rebase=true
if [ -n "$rebased_at" ]; then
  head_at=$(gh api "repos/$REPO/commits/$HEAD/check-suites?per_page=100" --paginate --jq '.check_suites[].created_at')
  head_at=$( { sort <<<"$head_at" | grep . || true; } | head -n 1)
  [ -n "$head_at" ] && fresh "$head_at" || head_after_rebase=false
fi
if [ "$codex_code" = clean ] && [ -n "$rebased_at" ] \
   && { ! $head_after_rebase || [ -z "$codex_code_at" ] || ! fresh "$codex_code_at"; }; then
  codex_code=stale; codex_was_stale=true; fi
if [ "$codex_security" = clean ] && [ -n "$rebased_at" ] \
   && { ! $head_after_rebase || [ -z "$codex_security_at" ] || ! fresh "$codex_security_at"; }; then
  codex_security=stale; codex_was_stale=true; fi
$codex_was_stale && stale+=("Codex")
[ "$codex_code" = dirty ] && blockers+=("Codex code review has open findings")
[ "$codex_security" = dirty ] && blockers+=("Codex security review has open findings")
[ "$codex_code" = clean ] && [ "$codex_security" = clean ] && passes+=("Codex")

# CodeRabbit: its latest review on this head. APPROVED passes; CHANGES_REQUESTED blocks.
rabbit_states=$(gh api "repos/$REPO/pulls/$PR/reviews?per_page=100" --paginate \
  --jq ".[] | select(.user.login == \"coderabbitai[bot]\" and .commit_id == \"$HEAD\") | \"\\(.state)|\\(.submitted_at)\"")
rabbit=$( { grep -E '^(APPROVED|CHANGES_REQUESTED|DISMISSED)[|]' <<<"$rabbit_states" || true; } | tail -n 1)
rabbit_at=${rabbit#*|}; rabbit=${rabbit%%|*}
case $rabbit in
  # submitted_at is when the review finished, not the base it read, so after a retarget it also needs a later head.
  APPROVED) if fresh "$rabbit_at" && $head_after_rebase; then passes+=("CodeRabbit"); else stale+=("CodeRabbit"); fi ;;
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
        | select((.comments.nodes[0].author.login // "") | test("^(coderabbitai|chatgpt-codex-connector|github-actions)$"))] | length' \
    | awk '{ n += $1 } END { print n + 0 }')
  [ "$open_threads" -gt 0 ] && blockers+=("$open_threads open review thread(s)")
fi

# The status sits on the head commit, so two open PRs with the same head would overwrite each other's gate.
# This lists the repo's open PRs rather than the commit's PRs: for a commit already on the default branch, the
# commit endpoint returns only the merged PR that introduced it, so it can miss open PRs with that head.
shared=$(gh api "repos/$REPO/pulls?state=open&per_page=100" --paginate \
  --jq "[.[] | select(.head.sha == \"$HEAD\" and .number != $PR)] | length" \
  | awk '{ n += $1 } END { print n + 0 }')
[ "$shared" -gt 0 ] && blockers+=("$shared other open PR(s) share this head; open one PR per head commit")

# A risky change also needs Codex's security review, clean on this head, whatever else passed.
risky=$escalated
# Read the whole listing first: grep -q on a pipe can exit early, and with pipefail the broken pipe would read as
# "no match". A failed listing stops the script, so the gate reads as errored rather than passing.
# A renamed file counts by both its old and new path, so moving a workflow out of .github/ is still risky.
if ! files=$(gh api "repos/$REPO/pulls/$PR/files?per_page=100" --paginate \
               --jq '.[] | .filename, (.previous_filename // empty)'); then
  echo "::error::could not list the PR's changed files" >&2; exit 1
fi
if grep -qE "$RISKY" <<<"$files"; then risky=true; fi
# The files endpoint lists at most 3,000 files, so a bigger PR may hide a risky path past the cutoff. Treat it as risky.
[ "$(jq -r '.changed_files // 0' <<<"$pr")" -ge 3000 ] && risky=true
if $risky && [ "$codex_security" != clean ]; then
  need_security=true; else need_security=false; fi

if [ ${#blockers[@]} -gt 0 ]; then
  state=failure; desc="Blocked: $(IFS=';'; echo "${blockers[*]}")"
elif [ ${#passes[@]} -eq 0 ]; then
  state=pending; desc="Waiting for one clean review from Claude, Codex or CodeRabbit"
  [ ${#stale[@]} -gt 0 ] && desc="Waiting for a clean review of the current base (${stale[*]} reviewed an earlier one)"
elif $need_security; then
  state=pending; desc="Passed by ${passes[*]}; a risky change also needs a clean Codex security review"
else
  state=success; desc="Passed by ${passes[*]}"
fi
desc=${desc:0:140}

echo "PR $PR head $HEAD: $state: $desc (claude=$claude_state codex=$codex_code/$codex_security coderabbit=${rabbit:-none} risky=$risky)"
if [ "${DRY_RUN:-}" = 1 ]; then exit 0; fi
# The sweep re-checks every 20 minutes; posting an identical status each time only adds noise.
if grep -qxF "$CONTEXT|$state|$desc" <<<"$latest"; then posted=true; echo "review-gate unchanged"; exit 0; fi
gh api -X POST "repos/$REPO/statuses/$HEAD" -f state="$state" -f context="$CONTEXT" \
  -f description="$desc" -f target_url="$RUN_URL" >/dev/null
posted=true
