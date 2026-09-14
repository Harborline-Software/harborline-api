#!/usr/bin/env bash
# Ticket 421: the lane filter in eng/verify.sh. A step that falls out of BOTH lanes stops running in
# CI without anything saying so, which is the failure this test exists to catch.
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fails=0
check() { if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1 (expected $2, got $3)"; fails=$((fails+1)); fi; }

# Exercise the real function by sourcing just its definition out of verify.sh.
eval "$(sed -n '/^in_lane() {/,/^}/p' "$root/eng/verify.sh")"

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
check "the two lanes cover every step" "$(echo "$steps" | tr ' ' '\n' | sort | tr -d '\n')" \
  "$(cat "/tmp/lane-shared.$$" "/tmp/lane-hostquality.$$" | sort | tr -d '\n')"
# quality reads what exact-clone writes, so it must never run in a lane that has no exact-clone.
check "shared runs no quality step" "0" "$(grep -cE '^quality' "/tmp/lane-shared.$$")"
check "a host without the quality flag runs no quality step" "0" "$(grep -cE '^quality' "/tmp/lane-host.$$")"
check "the quality host runs both quality steps" "2" "$(grep -cE '^quality' "/tmp/lane-hostquality.$$")"
rm -f "/tmp/lane-all.$$" "/tmp/lane-shared.$$" "/tmp/lane-host.$$" "/tmp/lane-hostquality.$$"
printf 'verify-lane: %s failures\n' "$fails"
[ "$fails" -eq 0 ]
