#!/usr/bin/env bash
# Exercises the workflow helper with API and sleep stubs; it never waits in real time.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/runner"

cat > "$fixture/bin/curl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
url=''
for argument in "$@"; do
  case "$argument" in http://*|https://*) url=$argument;; esac
done
printf '%s\n' "$url" >> "$WAIT_STUB_CALLS"
if [[ "$url" == *'/actions/artifacts?'* ]]; then
  count=$(grep -c '/actions/artifacts?' "$WAIT_STUB_CALLS" || true)
  if [ "$WAIT_STUB_SCENARIO" = api-error ]; then
    exit 22
  fi
  if [ "$WAIT_STUB_SCENARIO" = waited-use ] && [ "$count" -ge 2 ]; then
    printf '{"artifacts":[{"name":"quality-findings-base405","expired":false,"archive_download_url":"https://artifact.invalid/findings"}]}'
  else
    printf '{"artifacts":[]}'
  fi
elif [[ "$url" == *'/actions/runs?'* ]]; then
  count=$(grep -c '/actions/runs?' "$WAIT_STUB_CALLS" || true)
  if [ "$WAIT_STUB_SCENARIO" = mid-wait-completion ] && [ "$count" -ge 2 ]; then
    printf '{"workflow_runs":[{"name":"verify","event":"push","status":"completed"}]}'
  else
    printf '{"workflow_runs":[{"name":"verify","event":"push","status":"in_progress"}]}'
  fi
elif [[ "$url" == 'https://artifact.invalid/findings' ]]; then
  while [ "$#" -gt 0 ]; do
    if [ "$1" = -o ]; then cp "$WAIT_STUB_FINDINGS" "$2"; exit 0; fi
    shift
  done
  exit 1
else
  exit 1
fi
SH
cat > "$fixture/bin/unzip" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
while [ "$#" -gt 0 ]; do
  if [ "$1" = -d ]; then cp "$WAIT_STUB_FINDINGS" "$2/findings.json"; exit 0; fi
  shift
done
exit 1
SH
cat > "$fixture/bin/sleep" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$1" >> "$WAIT_STUB_SLEEPS"
SH
chmod +x "$fixture/bin/curl" "$fixture/bin/unzip" "$fixture/bin/sleep"
printf '{"schemaVersion":1,"findings":[]}' > "$fixture/findings.json"

run_case() {
  local scenario=$1
  : > "$fixture/calls"; : > "$fixture/sleeps"; : > "$fixture/github-env"
  WAIT_STUB_SCENARIO="$scenario" WAIT_STUB_CALLS="$fixture/calls" WAIT_STUB_SLEEPS="$fixture/sleeps" WAIT_STUB_FINDINGS="$fixture/findings.json" \
    PATH="$fixture/bin:$PATH" RUNNER_TEMP="$fixture/runner" GITHUB_ENV="$fixture/github-env" \
    bash "$root/eng/quality-baseline-artifact-wait.sh" base405 quality-findings-base405 Harborline-Software/harborline-api token405
}

out=$(run_case waited-use)
grep -Fq 'quality-baseline: waited then used merge-base artifact quality-findings-base405 after 30s' <<<"$out" || { echo "FAIL waited-use message: $out"; exit 1; }
grep -Fq 'HARBORLINE_QUALITY_BASELINE=' "$fixture/github-env" || { echo 'FAIL waited-use did not export the artifact'; exit 1; }
[ "$(wc -l < "$fixture/sleeps" | tr -d ' ')" = 1 ] || { echo 'FAIL waited-use did not use exactly one stubbed wait'; exit 1; }

out=$(run_case waited-fallback)
grep -Fq 'quality-baseline: waited 600s and fell back to the committed baseline because main verify for base405 remained in_progress and artifact quality-findings-base405 was unavailable' <<<"$out" || { echo "FAIL waited-fallback message: $out"; exit 1; }
[ "$(wc -l < "$fixture/sleeps" | tr -d ' ')" = 20 ] || { echo 'FAIL waited-fallback did not enforce the ten-minute bound'; exit 1; }

out=$(run_case mid-wait-completion)
grep -Fq 'quality-baseline: waited 30s and fell back to the committed baseline because main verify for base405 is completed; stopped waiting for artifact quality-findings-base405' <<<"$out" || { echo "FAIL mid-wait-completion message: $out"; exit 1; }
[ "$(wc -l < "$fixture/sleeps" | tr -d ' ')" = 1 ] || { echo 'FAIL mid-wait-completion did not stop after one stubbed wait'; exit 1; }
echo 'PASS mid-wait-completion'

out=$(run_case api-error)
grep -Fq 'quality-baseline: could not query merge-base artifact quality-findings-base405 because the GitHub API request failed; committed fallback will be used' <<<"$out" || { echo "FAIL api-error message: $out"; exit 1; }
[ "$(wc -l < "$fixture/sleeps" | tr -d ' ')" = 0 ] || { echo 'FAIL api-error waited after the failed request'; exit 1; }
echo 'PASS api-error'

upload_if=$(awk '/- name: Publish main quality findings/{found=1; next} found && /^[[:space:]]*if:/{sub(/^[[:space:]]*if: /, ""); print; exit}' "$root/.github/workflows/verify.yml")
[ "$upload_if" = "always() && github.event_name == 'push'" ] || { echo "FAIL main findings upload must be push-only and run after a failed gate: $upload_if"; exit 1; }
echo 'quality-baseline-artifact-wait: 5 checks passed (waited-use; waited-fallback after 600s; mid-wait-completion; api-error; push-only findings upload)'
