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
  # 403: the real endpoint answers 302 to blob storage, and the artifact is megabytes. A download
  # without --location fails on the redirect; one on the listing's 10s budget fails on size. This
  # stub refuses both, so dropping either flag reds this test instead of silently falling back.
  location=no; max_time=0
  saved=("$@")
  while [ "$#" -gt 0 ]; do
    case "$1" in
      -L|--location) location=yes;;
      --max-time) max_time=$2;;
    esac
    shift
  done
  [ "$location" = yes ] || exit 47
  [ "$max_time" -ge 60 ] || exit 28
  set -- "${saved[@]}"
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
cat > "$fixture/bin/tar" <<'SH'
#!/usr/bin/env bash
# 403: proves the tar fallback really extracts when unzip is absent (the Windows runner has no
# unzip). Mirrors the unzip stub's effect for `tar -xf findings.zip` in the destination directory.
set -euo pipefail
cp "$WAIT_STUB_FINDINGS" ./findings.json
SH
chmod +x "$fixture/bin/curl" "$fixture/bin/unzip" "$fixture/bin/sleep" "$fixture/bin/tar"
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

# 403: the Windows runner has curl but no unzip, and this job fetches a baseline now. Re-run the
# success case with unzip removed from PATH: it must still extract, through tar.
rm -f "$fixture/bin/unzip"
out=$(run_case waited-use)
grep -Fq 'quality-baseline: waited then used merge-base artifact quality-findings-base405 after 30s' <<<"$out" || { echo "FAIL no-unzip host did not fall back to tar: $out"; exit 1; }
grep -Fq 'HARBORLINE_QUALITY_BASELINE=' "$fixture/github-env" || { echo 'FAIL no-unzip host did not export the artifact'; exit 1; }
echo 'PASS no-unzip host extracts through tar'

upload_if=$(awk '/- name: Publish main quality findings/{found=1; next} found && /^[[:space:]]*if:/{sub(/^[[:space:]]*if: /, ""); print; exit}' "$root/.github/workflows/verify.yml")
[ "$upload_if" = "always() && github.event_name == 'push'" ] || { echo "FAIL main findings upload must be push-only and run after a failed gate: $upload_if"; exit 1; }
echo 'quality-baseline-artifact-wait: 6 checks passed (waited-use; waited-fallback after 600s; mid-wait-completion; api-error; no-unzip host extracts through tar; push-only findings upload)'
