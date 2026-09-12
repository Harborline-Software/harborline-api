#!/usr/bin/env bash
# Fetch the main run's findings before the quality gate decides whether a committed fallback is needed.
set -euo pipefail

base=$1
name=$2
repository=$3
token=$4
api_root="https://api.github.com/repos/$repository/actions"
poll_interval_seconds=30
max_wait_seconds=600
max_poll_attempts=$((max_wait_seconds / poll_interval_seconds))
curl_connect_timeout_seconds=5
curl_max_time_seconds=10
poll_request_reserve_seconds=$((curl_max_time_seconds * 2))
wait_deadline=$(($(date +%s) + max_wait_seconds))

api_get() {
  local max_time=$curl_max_time_seconds
  if [ -n "${wait_deadline:-}" ]; then
    local remaining_seconds
    remaining_seconds=$((wait_deadline - $(date +%s)))
    [ "$remaining_seconds" -gt 0 ] || return 1
    if [ "$remaining_seconds" -lt "$max_time" ]; then
      max_time=$remaining_seconds
    fi
  fi
  curl --fail --silent --show-error --retry 0 \
    --connect-timeout "$curl_connect_timeout_seconds" --max-time "$max_time" \
    -H 'Accept: application/vnd.github+json' \
    -H "Authorization: Bearer $token" "$@"
}

artifact_url() {
  node -e "let data = ''; process.stdin.on('data', chunk => data += chunk).on('end', () => { const artifact = (JSON.parse(data).artifacts || []).find(row => !row.expired && row.name === process.argv[1]); process.stdout.write(artifact?.archive_download_url || '') })" "$name"
}

find_artifact() {
  local listing
  listing=$(api_get "$api_root/artifacts?name=$name&per_page=100") || return 1
  printf '%s' "$listing" | artifact_url
}

download_artifact() {
  local url=$1 destination="$RUNNER_TEMP/quality-findings-$base"
  mkdir -p "$destination"
  if api_get "$url" -o "$destination/findings.zip" \
    && unzip -q "$destination/findings.zip" -d "$destination" \
    && [ -f "$destination/findings.json" ]; then
    echo "HARBORLINE_QUALITY_BASELINE=$destination/findings.json" >> "$GITHUB_ENV"
    return 0
  fi
  return 1
}

main_verify_status() {
  local runs
  runs=$(api_get "$api_root/runs?head_sha=$base&per_page=100") || return 1
  printf '%s' "$runs" | node -e "let data = ''; process.stdin.on('data', chunk => data += chunk).on('end', () => { const run = (JSON.parse(data).workflow_runs || []).find(row => row.name === 'verify' && row.event === 'push'); process.stdout.write(run?.status || 'not-found') })"
}

if ! url=$(find_artifact); then
  echo "quality-baseline: could not query merge-base artifact $name because the GitHub API request failed; committed fallback will be used"
  exit 0
fi
if [ -n "$url" ]; then
  if download_artifact "$url"; then
    echo "quality-baseline: used merge-base artifact $name"
    exit 0
  fi
  echo "quality-baseline: merge-base artifact $name was listed but could not be downloaded; committed fallback will be used"
  exit 0
fi

if ! status=$(main_verify_status); then
  echo "quality-baseline: merge-base artifact $name was unavailable, but main verify for $base could not be queried because the GitHub API request failed; committed fallback will be used"
  exit 0
fi
if [ "$status" != queued ] && [ "$status" != in_progress ]; then
  echo "quality-baseline: merge-base artifact $name unavailable; main verify for $base is $status; committed fallback will be used"
  exit 0
fi

for attempt in $(seq 1 "$max_poll_attempts"); do
  remaining_seconds=$((wait_deadline - $(date +%s)))
  [ "$remaining_seconds" -gt 0 ] || break
  sleep_seconds=$poll_interval_seconds
  if [ "$remaining_seconds" -le "$poll_request_reserve_seconds" ]; then
    break
  fi
  if [ "$sleep_seconds" -gt $((remaining_seconds - poll_request_reserve_seconds)) ]; then
    sleep_seconds=$((remaining_seconds - poll_request_reserve_seconds))
  fi
  sleep "$sleep_seconds"
  if ! status=$(main_verify_status); then
    echo "quality-baseline: waited $((attempt * poll_interval_seconds))s and fell back to the committed baseline because the GitHub API request to re-check main verify for $base failed"
    exit 0
  fi
  if [ "$status" != queued ] && [ "$status" != in_progress ]; then
    echo "quality-baseline: waited $((attempt * poll_interval_seconds))s and fell back to the committed baseline because main verify for $base is $status; stopped waiting for artifact $name"
    exit 0
  fi
  if ! url=$(find_artifact); then
    echo "quality-baseline: waited $((attempt * poll_interval_seconds))s and fell back to the committed baseline because the GitHub API request to query merge-base artifact $name failed"
    exit 0
  fi
  if [ -n "$url" ]; then
    if download_artifact "$url"; then
      echo "quality-baseline: waited then used merge-base artifact $name after $((attempt * poll_interval_seconds))s"
      exit 0
    fi
    echo "quality-baseline: waited $((attempt * poll_interval_seconds))s and fell back to the committed baseline because merge-base artifact $name could not be downloaded"
    exit 0
  fi
done

echo "quality-baseline: waited ${max_wait_seconds}s and fell back to the committed baseline because main verify for $base remained $status and artifact $name was unavailable"
