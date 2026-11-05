#!/usr/bin/env bash
# review-gate-queue: sets review-gate on a merge group's commit by carrying over the result the PR earned on its own
# head. PROC-0001 ("Review policy, 2026-11-01"): a pass reviews the PR's own changes, and the merge queue tests the
# combined result; before review-gate can be a required context it must report on the queue's commits, or the queue
# waits for a status that never comes.
#
# The queue names each group's ref gh-readonly-queue/<base branch>/pr-<number>-<base sha>, where the sha is the
# commit the group was built on, not the PR's head. So the PR number comes from GitHub's ref, the ref must agree with
# the event's own base branch and base sha, and the head is read from the PR itself: GitHub takes a PR out of the
# queue when its head changes, so the head it has now is the one that was queued. The carried status counts only if
# this workflow set it (creator github-actions[bot]): a status posted from anyone's personal token, an agent's
# included, never carries. Whatever went wrong, the group's commit gets a failure or error, never a pass.
#
# Usage: review-gate-queue.sh. Needs GITHUB_REPOSITORY, MERGE_GROUP_SHA, MERGE_GROUP_REF, MERGE_GROUP_BASE_REF,
# MERGE_GROUP_BASE_SHA and GH_TOKEN with statuses:write and pull-requests:read.
set -euo pipefail

REPO=${GITHUB_REPOSITORY:?}
GROUP_SHA=${MERGE_GROUP_SHA:?}
GROUP_REF=${MERGE_GROUP_REF:?}
GROUP_BASE_REF=${MERGE_GROUP_BASE_REF:?}
GROUP_BASE_SHA=${MERGE_GROUP_BASE_SHA:?}
CONTEXT=review-gate
GATE_APP=github-actions[bot]
RUN_URL=${GITHUB_SERVER_URL:-https://github.com}/$REPO/actions/runs/${GITHUB_RUN_ID:-0}

posted=false
post() {
  echo "merge group $GROUP_SHA: $1: $2"
  gh api -X POST "repos/$REPO/statuses/$GROUP_SHA" -f state="$1" -f context="$CONTEXT" \
    -f description="${2:0:140}" -f target_url="$RUN_URL" >/dev/null
  posted=true
}
on_exit() {
  local rc=$?
  if [ "$rc" -ne 0 ] && ! $posted; then
    gh api -X POST "repos/$REPO/statuses/$GROUP_SHA" -f state=error -f context="$CONTEXT" \
      -f description="review-gate could not check the merge group; see the run" -f target_url="$RUN_URL" >/dev/null || true
  fi
}
trap on_exit EXIT

ref_re='^refs/heads/gh-readonly-queue/(.+)/pr-([0-9]+)-([0-9a-f]{40})$'
if ! [[ $GROUP_REF =~ $ref_re ]]; then
  post failure "Not a merge queue ref: ${GROUP_REF:0:80}"; exit 0
fi
BASE=${BASH_REMATCH[1]}; PR=${BASH_REMATCH[2]}; REF_BASE_SHA=${BASH_REMATCH[3]}
if [ "refs/heads/$BASE" != "$GROUP_BASE_REF" ] || [ "$REF_BASE_SHA" != "$GROUP_BASE_SHA" ]; then
  post failure "Merge queue ref does not match the group's base"; exit 0
fi

# The PR must still be open into that base. Its head now is the head that was queued, since a push takes it out.
pr=$(gh api "repos/$REPO/pulls/$PR")
HEAD=$(jq -r .head.sha <<<"$pr"); short=${HEAD:0:7}
if [ "$(jq -r .state <<<"$pr")" != open ] || [ "$(jq -r .base.ref <<<"$pr")" != "$BASE" ]; then
  post failure "PR #$PR is no longer open into $BASE"; exit 0
fi
[[ $HEAD =~ ^[0-9a-f]{40}$ ]] || { post failure "PR #$PR has no readable head"; exit 0; }

# Statuses come newest first, so the first review-gate entry is the one standing on the head. The whole listing is
# read before it is searched, so a failed request stops the script instead of looking like no status.
listing=$(gh api "repos/$REPO/commits/$HEAD/statuses?per_page=100" --paginate \
  --jq '.[] | select(.context == "review-gate") | "\(.state)|\(.creator.login // "")|\(.description // "")"')
latest=$(head -n 1 <<<"$listing")
state=${latest%%|*}; rest=${latest#*|}; creator=${rest%%|*}; desc=${rest#*|}

if [ -z "$latest" ]; then
  post failure "PR #$PR has no review-gate on $short"
elif [ "$creator" != "$GATE_APP" ]; then
  post failure "PR #$PR's review-gate on $short was not set by the gate workflow"
elif [ "$state" != success ]; then
  post failure "PR #$PR's review-gate on $short is $state"
else
  post success "Carried from PR #$PR at $short: $desc"
fi
