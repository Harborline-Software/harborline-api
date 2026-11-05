#!/usr/bin/env bash
# review-gate-arm: decides whether a PR that review-gate just passed may merge on its own, and in arm mode turns on
# auto-merge for exactly the head that passed. The merge queue then tests it on top of main and lands it only when
# every required check passes, so arming never skips a check.
#
# Watch-only by default (MODE=watch): it only reports "would arm", so the owner can compare it with their own calls on
# about ten PRs before anything merges on its own. MODE=arm needs GH_TOKEN from the merge app, because GitHub's
# workflow token can't add a pull request to a merge queue.
#
# A PR qualifies only when all of these hold, each read from GitHub rather than from anything the PR says:
# - review-gate passed it and found nothing risky (the caller passes on the gate's own verdict);
# - the review-gate status standing on that head is success and was set by the gate workflow, not by a person's token;
# - it is open, ready (not a draft), still at that head, from a branch in this repository, into the default branch;
# - it is not Dependabot's (dependabot-automerge.yml owns those), and carries neither the `stacked` nor the
#   `no-auto-merge` label (labels only opt a PR out, never in);
# - auto-merge isn't on already.
#
# Usage: review-gate-arm.sh <pr-number> <head-sha>. Needs GITHUB_REPOSITORY and GH_TOKEN.
set -euo pipefail

PR=$1
HEAD=$2
REPO=${GITHUB_REPOSITORY:?}
MODE=${MODE:-watch}
GATE_APP=github-actions[bot]
short=${HEAD:0:7}

case $MODE in watch|arm) ;; *) echo "::error::unknown MODE '$MODE' (watch or arm)" >&2; exit 1 ;; esac

skip() { echo "PR #$PR at $short: stays manual: $1"; exit 0; }

pr=$(gh api "repos/$REPO/pulls/$PR")
[ "$(jq -r .state <<<"$pr")" = open ] || skip "not open"
[ "$(jq -r .draft <<<"$pr")" = false ] || skip "a draft"
[ "$(jq -r .head.sha <<<"$pr")" = "$HEAD" ] || skip "the head moved since review-gate ran"
[ "$(jq -r .head.repo.full_name <<<"$pr")" = "$REPO" ] || skip "from a fork"
[ "$(jq -r .base.ref <<<"$pr")" = "$(jq -r .base.repo.default_branch <<<"$pr")" ] || skip "not into the default branch"
[ "$(jq -r .user.login <<<"$pr")" != 'dependabot[bot]' ] || skip "a Dependabot PR (dependabot-automerge.yml)"
held=$(jq -r '[.labels[]?.name | select(. == "stacked" or . == "no-auto-merge")] | join(", ")' <<<"$pr")
[ -z "$held" ] || skip "labelled $held"
[ "$(jq -r '.auto_merge != null' <<<"$pr")" = false ] || skip "auto-merge is already on"

# Newest first; the whole listing is read before it is searched, so a failed request stops the script.
listing=$(gh api "repos/$REPO/commits/$HEAD/statuses?per_page=100" --paginate \
  --jq '.[] | select(.context == "review-gate") | "\(.state)|\(.creator.login // "")"')
latest=$(head -n 1 <<<"$listing")
[ -n "$latest" ] || skip "no review-gate on this head"
[ "${latest#*|}" = "$GATE_APP" ] || skip "review-gate on this head was not set by the gate workflow"
[ "${latest%%|*}" = success ] || skip "review-gate on this head is ${latest%%|*}"

if [ "$MODE" = watch ]; then
  echo "::notice title=auto-merge (watch-only)::would arm auto-merge for PR #$PR at $short"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "Watch-only: PR #$PR at \`$short\` qualifies; auto-merge would be armed." >>"$GITHUB_STEP_SUMMARY"
  fi
  exit 0
fi

gh pr merge "$PR" --repo "$REPO" --auto --squash --match-head-commit "$HEAD"
echo "PR #$PR at $short: auto-merge armed"
