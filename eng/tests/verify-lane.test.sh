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
for want in all shared host; do
  lane=$want
  for id in $steps; do
    in_lane "$id" && echo "$id"
  done > "/tmp/lane-$want.$$"
done
check "all runs every step" "$(echo "$steps" | wc -w | tr -d ' ')" "$(wc -l < "/tmp/lane-all.$$" | tr -d ' ')"
check "host runs only exact-clone" "exact-clone" "$(cat "/tmp/lane-host.$$")"
check "shared excludes exact-clone" "0" "$(grep -c '^exact-clone$' "/tmp/lane-shared.$$")"
check "the two lanes cover every step" "$(echo "$steps" | tr ' ' '\n' | sort | tr -d '\n')" \
  "$(cat "/tmp/lane-shared.$$" "/tmp/lane-host.$$" | sort | tr -d '\n')"
rm -f "/tmp/lane-all.$$" "/tmp/lane-shared.$$" "/tmp/lane-host.$$"
printf 'verify-lane: %s failures\n' "$fails"
[ "$fails" -eq 0 ]
