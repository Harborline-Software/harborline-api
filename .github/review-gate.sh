#!/usr/bin/env bash
# review-gate: one commit status on a PR's head that says whether the PR has the review PROC-0001 asks for.
# "Review policy, 2026-11-01": one clean pass from any of the Claude review, Codex or CodeRabbit, and no
# blocker from any reviewer that ran. A risky change also needs a clean Codex security review. A Claude ESCALATE is not a
# blocker: it marks the PR risky, so it passes once Codex's security review is clean and another reviewer passed it
# (ADR-0159). A Claude BLOCK still blocks.
# "Review by risk": a change that touches only paths on the review-exempt list, or a Dependabot
# patch or minor update that only Dependabot wrote, needs no review, as long as it touches nothing risky. A blocker
# from a reviewer that ran still blocks.
#
# Usage: review-gate.sh <pr-number>. Needs GH_TOKEN with statuses:write and pull-requests:read, and
# GITHUB_REPOSITORY. DRY_RUN=1 prints the status instead of setting it. NO_GRAPHQL=1 skips the thread check.
set -euo pipefail

PR=$1
REPO=${GITHUB_REPOSITORY:?}
CONTEXT=review-gate
# Changes that also need a clean Codex security review: CI and its scripts, and the pinned SDK and tool versions.
RISKY=${RISKY_PATHS:-'^(\.github/|global\.json$|\.config/dotnet-tools\.json$)'}
# Plus the paths on the risky list, listed beside this script and read from the same default-branch checkout (or a
# protected base, see below), so a PR can't shorten the list it is judged by. One path per line; / ends a folder.
RISKY_LIST=${RISKY_LIST:-$(dirname "$0")/review-risky-paths}
# Paths that need no review when a PR touches nothing else, read from the same default-branch checkout. Same
# format, plus a line starting with ! takes a path back out; the last line that matches a file decides. Only a PR into
# the default branch can be exempt; a PR into any other branch always needs a review (see below).
EXEMPT_LIST=${EXEMPT_LIST:-$(dirname "$0")/review-exempt-paths}
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
BASE_SHA=$(jq -r .base.sha <<<"$pr")
OWNER=${REPO%%/*}; NAME=${REPO#*/}

passes=(); blockers=(); stale=()

# A pass given before the PR's base last changed reviewed another diff, so it doesn't count. Blockers still do.
rebased_at=$(gh api "repos/$REPO/issues/$PR/events?per_page=100" --paginate \
  --jq '.[] | select(.event == "base_ref_changed" or .event == "automatic_base_change_succeeded") | .created_at')
rebased_at=$(sort <<<"$rebased_at" | tail -n 1)
fresh() { [[ -z $rebased_at || $1 > "$rebased_at" ]]; }   # ISO 8601 UTC times sort as text

# Claude review: the review workflow sets the claude-review status on the head it reviewed.
# success = PASS; failure = BLOCK or ESCALATE; error or missing = it did not run.
# Statuses come newest first, so the first entry per context is the one standing on the head. Each entry names who
# set it: only the review workflow (github-actions[bot]) can give a Claude verdict, so one set by anyone else blocks.
# A line break in a description would start a line of its own here, which could pass for a status from the workflow,
# so each one becomes a space (Claude review on api 497).
history=$(gh api "repos/$REPO/commits/$HEAD/statuses?per_page=100" --paginate --jq '.[]
  | select(.context == "claude-review" or .context == "review-gate")
  | "\(.context)|\(.state)|\(.updated_at)|\(.creator.login // "")|\(.id)|\(.description // "" | gsub("[\r\n]"; " "))"')
statuses=$(awk -F'|' '!seen[$1]++' <<<"$history")
latest=$(cut -d'|' -f1,2,6- <<<"$statuses")
# Which of the two statuses this run read, by id, so it can tell before it posts whether another one landed meanwhile.
standing() {
  local s
  s=$(gh api "repos/$REPO/commits/$HEAD/statuses?per_page=100" --paginate --jq '.[]
    | select(.context == "claude-review" or .context == "review-gate") | "\(.context)|\(.id)"') || return 1
  awk -F'|' '!seen[$1]++' <<<"$s" | sort
}
seen=$(cut -d'|' -f1,5 <<<"$statuses" | sort)
claude_at=$(awk -F'|' '$1 == "claude-review" { print $3; exit }' <<<"$statuses")
claude_by=$(awk -F'|' '$1 == "claude-review" { print $4; exit }' <<<"$statuses")
claude=$( { grep '^claude-review|' <<<"$latest" || true; } | head -n 1 | cut -d'|' -f2-)
claude=${claude:-none|}
claude_state=${claude%%|*}; claude_desc=${claude#*|}
# The verdict is the word right after "Claude review: ", read by position: the description also carries the PR's base
# branch, and a branch named, say, release/ESCALATE-fix must not turn a BLOCK into an ESCALATE.
claude_verdict=$(sed -nE 's/^Claude review: (PASS|BLOCK|ESCALATE) \(.*/\1/p' <<<"$claude_desc")
if [ "$claude_state" != none ] && [ "$claude_by" != 'github-actions[bot]' ]; then
  blockers+=("claude-review was not set by the review workflow"); claude_state=forged
fi
case $claude_state in
  # A pass counts only for the base it reviewed: the review records the PR's base branch in its description, so a
  # review that finished after a retarget, or one given to another PR with the same head, doesn't count here.
  success) if fresh "$claude_at" && [[ $claude_desc == *", base $BASE_REF)" ]]; then passes+=("Claude")
           else stale+=("Claude"); fi ;;
  # A BLOCK holds the PR. An ESCALATE does not block by itself: it makes the PR risky, so it then needs one clean pass
  # from Codex or CodeRabbit and a clean Codex security review (ADR-0159). Any other failure, or one with no verdict
  # in its description, still blocks.
  # An ESCALATE, like a pass, speaks only for the base it reviewed: one given before a retarget blocks until the review
  # of the new base lands (Claude review on api 497).
  failure) if [ "$claude_verdict" != ESCALATE ]; then blockers+=("${claude_desc:-Claude review: not PASS}")
           elif ! fresh "$claude_at" || [[ $claude_desc != *", base $BASE_REF)" ]]; then
             blockers+=("Claude escalated on an earlier base; waiting for a review of the current base"); fi ;;
esac
escalated=false; [[ $claude_state == failure && $claude_verdict == ESCALATE ]] && escalated=true
# The standing status alone can hide an earlier verdict on this head. The review workflow keeps a BLOCK or HELD under a
# later ESCALATE itself now, but an earlier version of it could post an ESCALATE over one (Codex on app 98), and a PASS
# given to another PR with the same head, for another base, lands on top of this PR's verdicts without clearing them
# (Fable on platform 492). So the gate reads back through the head's own history, whatever the standing verdict:
# - a fresh PASS for the current base clears what lies beneath it, and the read stops there;
# - a PASS for another base, or from before a retarget, clears nothing and is read past;
# - a BLOCK, HELD or other failure the workflow set holds the PR, whatever base it names;
# - an ESCALATE makes the PR risky, whatever base it names, as a standing one does, and the read goes on for a BLOCK
#   beneath it: one from before a retarget, read past here, would otherwise leave an exempt PR needing no review
#   (Fable on platform 492).
# Only statuses the review workflow set count, and an error is skipped: a run with no verdict said nothing.
held=""
while IFS='|' read -r ctx st at by sid desc; do
  [ "$ctx" = claude-review ] && [ "$by" = 'github-actions[bot]' ] || continue
  current=false; [[ $desc == *", base $BASE_REF)" ]] && fresh "$at" && current=true
  case $st in
    success) $current && break ;;
    failure) if [[ $desc == "Claude review: ESCALATE ("* ]]; then escalated=true
             else held=$desc; break; fi ;;
  esac
done <<<"$history"
# A standing BLOCK already blocks with its own description; name only one found beneath the standing status.
if [ -n "$held" ] && ! [[ $claude_state == failure && $claude_verdict != ESCALATE ]]; then
  blockers+=("an earlier Claude verdict on this head still holds: $held")
fi
# An escalated PR lands only when a person queues it (ADR-0159 decision 4). The auto-merge job never arms a risky PR,
# but one it armed before Claude escalated would still merge on its own, so the gate holds the PR while auto-merge
# stands that anything but a person turned on, until the owner turns it off or queues the PR (Fable on platform
# 492). Read from GitHub's own record of who enabled it, so a missing name holds the PR too.
armed=$(jq -r 'if .auto_merge then "\(.auto_merge.enabled_by.type // "")|\(.auto_merge.enabled_by.login // "")"
               else "" end' <<<"$pr")
if $escalated && [ -n "$armed" ] && [ "${armed%%|*}" != User ]; then
  armed_by=${armed#*|}
  blockers+=("Claude escalated after ${armed_by:-an unnamed account} armed auto-merge; turn it off and queue the PR yourself")
fi

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
# MSBuild imports the nearest Directory.Build.props, Directory.Build.targets and Directory.Packages.props above each
# project, and reads the nearest Directory.Build.rsp as extra command-line switches; NuGet reads a nuget.config from
# every folder between a project and the root, in any of its accepted casings. So one at any depth changes every build
# or restore below it. Case-insensitive, as Windows resolves them.
if grep -qiE '(^|/)(Directory\.(Build\.(props|targets|rsp)|Packages\.props)|nuget\.config)$' <<<"$files"; then risky=true; fi
# The risky list is the default branch's, checked out beside this script. A PR into another branch uses that branch's
# list only when a ruleset there requires a pull request, so nobody can aim a PR at a branch carrying an edited list.
# The base's list is read as data through the API; nothing from the base runs. A failed lookup stops the script,
# which marks the head errored, and a protected base whose list can't be read leaves the PR risky.
default_branch=$(jq -r .base.repo.default_branch <<<"$pr")
# A no-review pass sits on the head commit whatever the base, so it would stand after a retarget until the next run
# replaces it. So only the default branch grants one. A pass against it covers the PR's own files plus commits
# already reviewed into the default branch; into any other branch, protected or not, every PR needs a review.
exempt_ok=true
if [ "$BASE_REF" != "$default_branch" ]; then
  exempt_ok=false
  echo "no review exemption: base $BASE_REF is not $default_branch"
  guarded=$(gh api "repos/$REPO/rules/branches/$(jq -rn --arg b "$BASE_REF" '$b | @uri | gsub("%2F"; "/")')" --jq '[.[] | select(.type == "pull_request")] | length')
  if [ "$guarded" -gt 0 ]; then
    base_list=$(mktemp)
    if gh api -H 'Accept: application/vnd.github.raw' \
         "repos/$REPO/contents/.github/review-risky-paths?ref=$(jq -rn --arg r "$BASE_REF" '$r | @uri')" >"$base_list"; then
      RISKY_LIST=$base_list; echo "risky list: from protected base $BASE_REF"
    else
      echo "::warning::could not read the risky list from protected base $BASE_REF; treating the PR as risky"
      RISKY_LIST=/dev/null/missing
    fi
  else
    echo "risky list: from $default_branch, because base $BASE_REF has no ruleset requiring a pull request"
  fi
fi
if [ -r "$RISKY_LIST" ]; then
  # The entries go in through the environment, so a list with no entries can't make awk read the PR's files as the
  # list. A list that exists but names nothing fails closed: every PR needs the security review until it's fixed.
  want=$(sed -e 's/#.*//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' "$RISKY_LIST" | grep -v '^$' || true)
  if [ -z "$want" ]; then
    echo "::warning::$RISKY_LIST lists no paths; treating every PR as risky"
    risky=true
  elif WANT=$want awk 'BEGIN { n = split(ENVIRON["WANT"], w, "\n") }
      { for (i = 1; i <= n; i++) if ($0 == w[i] || (w[i] ~ /\/$/ && index($0, w[i]) == 1)) { found = 1; exit } }
      END { exit !found }' <<<"$files"; then
    risky=true
  fi
else
  # The list ships in the same base commit as this script, so a missing one means the checkout didn't fetch it or
  # someone deleted it. Fail closed rather than quietly drop the protection it adds.
  echo "::warning::no readable $RISKY_LIST in the base checkout; treating every PR as risky"
  risky=true
fi
# The files endpoint lists at most 3,000 files, so a bigger PR may hide a risky path past the cutoff. Treat it as risky.
[ "$(jq -r '.changed_files // 0' <<<"$pr")" -ge 3000 ] && risky=true
if $risky && [ "$codex_security" != clean ]; then
  need_security=true; else need_security=false; fi

# Review by risk. Into the default branch, two kinds of PR need no review, and both are decided from the trusted lists and the PR's own record,
# never from anything the PR says about itself. A blocker from a reviewer that ran still blocks either of them.
needs_review=true; why=''
changed=$(jq -r '.changed_files // 0' <<<"$pr")

# Both exemptions are judged from one fixed snapshot: GitHub's comparison of the base and head commits read above.
# The files were listed by PR number after the head was read, so a push in between could have swapped what was listed.
# The comparison must list exactly the same files, or nothing is exempt. It lists at most 300 files and 250 commits,
# so a bigger PR gets no exemption either.
snap=''
if $exempt_ok && ! $risky && [ "$changed" -gt 0 ] && [ "$changed" -le 300 ]; then
  if snap=$(gh api "repos/$REPO/compare/$BASE_SHA...$HEAD"); then
    snap_files=$(jq -r '.files[] | .filename, (.previous_filename // empty)' <<<"$snap" | sort -u)
    if [ "$snap_files" != "$(sort -u <<<"$files")" ]; then
      echo "::warning::the PR's files don't match the comparison of $BASE_SHA and $HEAD; no review exemption"
      exempt_ok=false
    fi
  else
    echo "::warning::could not compare $BASE_SHA and $HEAD; no review exemption"
    exempt_ok=false
  fi
else
  exempt_ok=false
fi

# 1. Every changed file, by its new and its old path, is on the exempt list, and nothing about the PR is risky.
#    An empty PR, a missing list or one that names nothing exempts nothing, and no agent instruction file is exempt.
if $exempt_ok && ! $risky && [ "$changed" -gt 0 ] && [ -n "$files" ] && [ -r "$EXEMPT_LIST" ]; then
  rules=$(sed -e 's/#.*//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' "$EXEMPT_LIST" | grep -v '^$' || true)
  if [ -n "$rules" ] && RULES=$rules awk '
      BEGIN { n = split(ENVIRON["RULES"], r, "\n") }
      { hit = 0
        # Agent instruction files steer the agents that write and review code below them, so none is exempt at any
        # depth or in any casing, whatever the list says (Codex reads a nested AGENTS.md for its subtree).
        f = tolower($0); sub(/.*\//, "", f)
        if (f == "agents.md" || f == "agents.override.md" || f == "claude.md" || f == "claude.local.md") { bad = 1; exit }
        for (i = 1; i <= n; i++) {
          neg = substr(r[i], 1, 1) == "!"; p = neg ? substr(r[i], 2) : r[i]
          if ($0 == p || (p ~ /\/$/ && index($0, p) == 1)) hit = neg ? 0 : 1
        }
        if (!hit) { bad = 1; exit } }
      END { exit bad }' <<<"$files"; then
    needs_review=false; why="every change is on the review-exempt list"
  fi
elif $exempt_ok && [ ! -r "$EXEMPT_LIST" ]; then
  echo "::warning::no readable review-exempt list; every PR needs a review"
fi

# 2. A Dependabot version update that touches nothing risky. The PR is Dependabot's, and every commit on it was
#    written by Dependabot and committed and signed by GitHub itself (committer web-flow, verified), which is how
#    Dependabot's own commits look. A commit someone else pushed, even one that keeps Dependabot as its author and is
#    signed with their own key, has another committer and needs a review. Every update must be a patch or minor one,
#    from the update-type lines Dependabot writes in its commit messages, and every commit must carry such lines. A major update, a security update with no
#    update-type, or a bump that touches a risky path (anything under .github/, so every GitHub Actions bump, or a
#    file on the risky list) needs a review like any other PR, and a risky one its Codex security review too.
#    The commits come from the same snapshot. It lists at most 250 commits, so it must hold as many as it and the PR
#    say there are; a PR with more commits than were listed needs a review.
if $exempt_ok && $needs_review && ! $risky && [ "$(jq -r .user.login <<<"$pr")" = 'dependabot[bot]' ] \
   && [ "$(jq -r .head.repo.full_name <<<"$pr")" = "$REPO" ]; then
  commits=$(jq -c '.commits[] | {a: (.author.login // ""), c: (.committer.login // ""),
                                    v: (.commit.verification.verified == true), m: .commit.message}' <<<"$snap")
  others=$(jq -s '[.[] | select(.a != "dependabot[bot]" or .c != "web-flow" or .v != true)] | length' <<<"$commits")
  listed=$(jq -s length <<<"$commits")
  # Every commit must carry its own update-type lines; one with none must not hide behind another's.
  untyped=$(jq -s '[.[] | select(.m | split("\n") | map(select(test("^\\s*update-type:"))) | length == 0)] | length' <<<"$commits")
  types=$(jq -rs '.[].m' <<<"$commits" | sed -nE 's/^[[:space:]]*update-type:[[:space:]]*//p')
  if [ -n "$commits" ] && [ "$others" -eq 0 ] && [ "$listed" = "$(jq -r '.commits // empty' <<<"$pr")" ] \
     && [ "$listed" = "$(jq -r '.total_commits // empty' <<<"$snap")" ] \
     && [ "$untyped" -eq 0 ] && [ -n "$types" ] \
     && ! grep -qvxE 'version-update:semver-(patch|minor)' <<<"$types"; then
    needs_review=false; why="a Dependabot patch or minor update"
  fi
fi

if [ ${#blockers[@]} -gt 0 ]; then
  state=failure; desc="Blocked: $(IFS=';'; echo "${blockers[*]}")"
elif ! $needs_review && ! $risky; then
  state=success; desc="No review needed: $why"
elif [ ${#passes[@]} -eq 0 ]; then
  state=pending; desc="Waiting for one clean review from Claude, Codex or CodeRabbit"
  [ ${#stale[@]} -gt 0 ] && desc="Waiting for a clean review of the current base (${stale[*]} reviewed an earlier one)"
  # CodeRabbit doesn't review PRs over 150 files, so only Claude or Codex can pass one.
  [ ${#stale[@]} -eq 0 ] && [ "$changed" -gt 150 ] && desc="Waiting for a clean review from Claude or Codex (CodeRabbit skips PRs over 150 files)"
elif $need_security; then
  state=pending; desc="Passed by ${passes[*]}; a risky change also needs a clean Codex security review"
else
  state=success; desc="Passed by ${passes[*]}"
  $escalated && desc="$desc; Claude escalated, Codex security review clean"
fi
desc=${desc:0:140}

echo "PR $PR head $HEAD: $state: $desc (claude=$claude_state codex=$codex_code/$codex_security coderabbit=${rabbit:-none} risky=$risky needs_review=$needs_review)"
if [ "${DRY_RUN:-}" = 1 ]; then exit 0; fi
# The auto-merge job in review-gate.yml reads these, so a later job never has to re-judge the PR. They are written
# only once the status stands on the head, so the job can't act on a verdict that was never posted.
emit() {
  if [ -n "${GITHUB_OUTPUT:-}" ]; then printf 'state=%s\nrisky=%s\nhead=%s\n' "$state" "$risky" "$HEAD" >>"$GITHUB_OUTPUT"; fi
}
# The review workflow sets a new claude-review, then sets review-gate back to pending, then starts a run of this
# workflow. A verdict worked out before the new claude-review landed may answer an earlier Claude review, so this run
# posts nothing, tells the auto-merge job nothing, and leaves the PR to the run started after it, or failing that to
# the sweep (Codex on app 98).
# A review-gate that changed under an unchanged claude-review came from another gate run, and nothing says which run is
# newer: runs of one PR share a group that cancels the older one, but an older run can still post as it is cancelled.
# This run posts a hold over it, so an older run's pass doesn't stand until the sweep (Claude review on app 98), but
# never a pass, which could replace a newer run's hold (Codex on platform 492); the sweep settles that case.
now=$(standing)
if [ "$(grep '^claude-review|' <<<"$now" || true)" != "$(grep '^claude-review|' <<<"$seen" || true)" ]; then
  posted=true; echo "review-gate left as it is: the Claude review changed while this run read it"; exit 0
fi
if [ "$state" = success ] && [ "$(grep "^$CONTEXT|" <<<"$now" || true)" != "$(grep "^$CONTEXT|" <<<"$seen" || true)" ]; then
  posted=true; echo "review-gate left as it is: another gate run set it while this run read the PR"; exit 0
fi
# The sweep re-checks every 20 minutes; posting an identical status each time only adds noise.
if [ "$now" = "$seen" ] && grep -qxF "$CONTEXT|$state|$desc" <<<"$latest"; then
  posted=true; echo "review-gate unchanged"; emit; exit 0
fi
gh api -X POST "repos/$REPO/statuses/$HEAD" -f state="$state" -f context="$CONTEXT" \
  -f description="$desc" -f target_url="$RUN_URL" >/dev/null
posted=true
# A new Claude verdict can still land after that check. The review workflow resets review-gate after the verdict, so
# a reset that lands after this post replaces the pass; if the reset landed first, so did the verdict, and this read
# finds it and takes the pass back.
# An escalated PR's pass rests on the auto-merge read at the start; a bot or app that armed auto-merge since then
# would have it merge on this pass, so the PR is read again and the pass taken back, unreadable counting as armed
# (CodeRabbit on api #497). The run that auto_merge_enabled starts holds the PR too; this only narrows the window.
if [ "$state" = success ]; then
  now=$(standing || echo 'claude-review|unreadable')
  takeback=""
  if [ "$(grep '^claude-review|' <<<"$now" || true)" != "$(grep '^claude-review|' <<<"$seen" || true)" ]; then
    takeback=pending; desc="The Claude review changed while review-gate read it; waiting for the next check"
  elif $escalated; then
    armed=$(gh api "repos/$REPO/pulls/$PR" --jq 'if .auto_merge then "\(.auto_merge.enabled_by.type // "")|\(.auto_merge.enabled_by.login // "")"
                                               else "" end') || armed="|unreadable"
    if [ -n "$armed" ] && [ "${armed%%|*}" != User ]; then
      armed_by=${armed#*|}
      takeback=failure; desc="Blocked: Claude escalated after ${armed_by:-an unnamed account} armed auto-merge; turn it off and queue the PR yourself"
    fi
  fi
  if [ -n "$takeback" ]; then
    state=$takeback
    echo "review-gate: $desc"
    # If this post fails, the exit handler sets review-gate to error, so the pass doesn't stay (Claude review on App #98).
    posted=false
    gh api -X POST "repos/$REPO/statuses/$HEAD" -f state="$state" -f context="$CONTEXT" \
      -f description="$desc" -f target_url="$RUN_URL" >/dev/null
    posted=true
  fi
fi
emit
