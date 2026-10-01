#!/usr/bin/env bash
# Ticket 421: the lane filter in eng/verify.sh. A step that falls out of BOTH lanes stops running in
# CI without anything saying so, which is the failure this test exists to catch.
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fails=0
check() { if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1 (expected $2, got $3)"; fails=$((fails+1)); fi; }

# Exercise the real function by sourcing just its definition out of verify.sh.
eval "$(sed -n '/^in_lane() {/,/^}/p' "$root/eng/verify.sh")"
eval "$(sed -n '/^configure_quality() {/,/^}/p' "$root/eng/verify.sh")"
eval "$(sed -n '/^configure_checkouts() {/,/^}/p' "$root/eng/verify.sh")"
checkout_checks=$(cd "$root" && {
  lane=all
  HARBORLINE_GATE_QUALITY=
  unset HARBORLINE_CONTROL_REPO HARBORLINE_QUALITY_REPO
  configure_checkouts || exit 1
  node - "$HARBORLINE_CONTROL_REPO" "$HARBORLINE_QUALITY_REPO" <<'NODE' || exit 1
const assert = require('node:assert/strict')
const path = require('node:path')
const {execFileSync} = require('node:child_process')
const common = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], {encoding: 'utf8'}).trim()
const parent = path.dirname(path.dirname(common))
assert.equal(process.argv[2], path.join(parent, 'harborline-control'))
assert.equal(process.argv[3], path.join(parent, 'harborline-quality'))
NODE
  HARBORLINE_CONTROL_REPO='relative control with spaces'
  HARBORLINE_QUALITY_REPO='relative quality with spaces'
  configure_checkouts || exit 1
  node - "$HARBORLINE_CONTROL_REPO" "$HARBORLINE_QUALITY_REPO" <<'NODE' || exit 1
const assert = require('node:assert/strict')
const path = require('node:path')
assert.equal(process.argv[2], path.resolve('relative control with spaces'))
assert.equal(process.argv[3], path.resolve('relative quality with spaces'))
NODE
})
check 'checkout defaults and relative overrides survive scratch-clone cwd changes' 0 "$?"
for checkout_lane in all shared host; do
  for checkout_flag in '' 1; do
    checkout_status=$(cd "$root" && {
      lane=$checkout_lane
      HARBORLINE_GATE_QUALITY=$checkout_flag
      unset HARBORLINE_CONTROL_REPO HARBORLINE_QUALITY_REPO
      configure_checkouts || exit 1
      if [ "$lane" = host ] && [ "$checkout_flag" != 1 ]; then
        [ -z "${HARBORLINE_CONTROL_REPO:-}" ] && [ -z "${HARBORLINE_QUALITY_REPO:-}" ] || exit 1
        # Execute the unchanged real normalizer assertions in the hosted non-quality environment.
        node --test eng/tests/normalize-roslyn-sarif.test.mjs || exit 1
      else
        [ -n "${HARBORLINE_CONTROL_REPO:-}" ] && [ -n "${HARBORLINE_QUALITY_REPO:-}" ] || exit 1
      fi
    })
    check "$checkout_lane flag=$checkout_flag exports only required checkout defaults" 0 "$?"
  done
done
for lane in all shared host; do
  for flag in '' 1; do
    HARBORLINE_GATE_QUALITY=$flag
    HARBORLINE_VERIFY_QUALITY_RUN=stale-parent-run
    configure_quality || exit 1
    expected=0
    if [ "$lane" = all ] || { [ "$lane" = host ] && [ "$flag" = 1 ]; }; then expected=1; fi
    actual=0
    if [ -n "${HARBORLINE_VERIFY_QUALITY_RUN:-}" ] && [ "$HARBORLINE_VERIFY_QUALITY_RUN" != stale-parent-run ] && [ "$HARBORLINE_GATE_QUALITY" = 1 ]; then actual=1; fi
    check "$lane flag=$flag configures fresh analyzer production" "$expected" "$actual"
  done
done

steps=$(grep -oE '^step +[a-z0-9-]+' "$root/eng/verify.sh" | awk '{print $2}')
# The host lane carries quality only on the ONE host that sets HARBORLINE_GATE_QUALITY, because the
# SARIF those steps read is written by exact-clone under that flag.
for want in all shared host hostquality; do
  case "$want" in
    hostquality) lane=host; HARBORLINE_GATE_QUALITY=1 ;;
    *) lane=$want; HARBORLINE_GATE_QUALITY= ;;
  esac
  for id in $steps; do
    in_lane "$id" && echo "$id"
  done > "/tmp/lane-$want.$$"
done
check "all runs every step" "$(echo "$steps" | wc -w | tr -d ' ')" "$(wc -l < "/tmp/lane-all.$$" | tr -d ' ')"
check "host runs only exact-clone" "exact-clone" "$(cat "/tmp/lane-host.$$")"
check "shared excludes exact-clone" "0" "$(grep -c '^exact-clone$' "/tmp/lane-shared.$$")"
# The package lanes run in CI as the required packages.yml jobs (2026-09-29, owner), so the shared
# lane skips them and only `all` runs them here.
package_lanes="contracts-typescript contracts-csharp contracts-rust operator-cli-headless packages"
check "the lanes plus the package lanes cover every step" "$(echo "$steps" | tr ' ' '\n' | sort | tr -d '\n')" \
  "$( (cat "/tmp/lane-shared.$$" "/tmp/lane-hostquality.$$"; printf '%s\n' $package_lanes) | sort | tr -d '\n')"
check "shared runs no package lane" "0" "$(grep -cxE "$(echo $package_lanes | tr ' ' '|')" "/tmp/lane-shared.$$")"
# quality reads what exact-clone writes, so it must never run in a lane that has no exact-clone.
check "shared runs no quality step" "0" "$(grep -cE '^quality' "/tmp/lane-shared.$$")"
check "a host without the quality flag runs no quality step" "0" "$(grep -cE '^quality' "/tmp/lane-host.$$")"
check "the quality host runs both quality steps" "2" "$(grep -cE '^quality' "/tmp/lane-hostquality.$$")"
rm -f "/tmp/lane-all.$$" "/tmp/lane-shared.$$" "/tmp/lane-host.$$" "/tmp/lane-hostquality.$$"
printf 'verify-lane: %s failures\n' "$fails"
[ "$fails" -eq 0 ]
