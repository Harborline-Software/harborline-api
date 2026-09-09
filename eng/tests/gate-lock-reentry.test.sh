#!/usr/bin/env bash
# Ticket 333: exercise real nested shells, with only a disposable repository lock.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
lock_source="$here/../gate-lock.sh"

if [ "${1:-}" = stub ]; then
  source "$lock_source"
  expected=$2
  case "$expected" in
    # bash 5 execs the last command of a subshell in place, so the stub's parent IS the owner. bash 3.2
    # (macOS /bin/bash) forks it, so these two shapes cannot be produced there: report that as 25, not
    # as a lock failure (ticket 345). The trailing and missing shapes cover the forked grandchild.
    last|legacy) [ "$PPID" = "$HARBORLINE_GATE_LOCK_OWNER_PID" ] || exit 25 ;;
    trailing|missing) [ "$PPID" != "$HARBORLINE_GATE_LOCK_OWNER_PID" ] || exit 21 ;;
  esac
  # The acquisition ceiling: five seconds idle, twenty under load (333 item 7: a chain plus two lanes made the trailing shape miss five on Windows).
  SECONDS=0
  target=$$
  ( sleep "${HARBORLINE_GATE_LOCK_TEST_CEILING:-20}"; kill -TERM "$target" 2>/dev/null ) & watchdog=$!
  trap 'kill "$watchdog" 2>/dev/null || true; wait "$watchdog" 2>/dev/null || true' EXIT
  trap 'exit 143' TERM
  gate_lock_acquire "stub-$expected" || exit $?
  [ "${HARBORLINE_GATE_LOCK_OWNED:-0}" = 0 ] || exit 22
  printf 'acquired %s in %ss\n' "$expected" "$SECONDS"
  exit 0
fi

if [ "${1:-}" = owner ]; then
  source "$lock_source"
  requested_path=$HARBORLINE_GATE_LOCK_PATH
  gate_lock_acquire test-owner || exit $?
  [ "$HARBORLINE_GATE_LOCK_PATH" = "$requested_path" ] || exit 24
  case "$2" in
    last) ( cd . && bash "$0" stub last ) ;;
    trailing) ( cd . && bash "$0" stub trailing && true ) ;;
    legacy) unset HARBORLINE_GATE_LOCK_REENTRY_TOKEN; ( cd . && bash "$0" stub legacy ) ;;
    missing)
      unset HARBORLINE_GATE_LOCK_REENTRY_TOKEN
      ( cd . && bash "$0" stub missing && true ) ;;
    stale)
      printf 'export %s=%q\n' \
        HARBORLINE_GATE_LOCK_OWNER_PID "$HARBORLINE_GATE_LOCK_OWNER_PID" \
        HARBORLINE_GATE_LOCK_OWNER_START "$HARBORLINE_GATE_LOCK_OWNER_START" \
        HARBORLINE_GATE_LOCK_OWNER_NONCE "$HARBORLINE_GATE_LOCK_OWNER_NONCE" \
        HARBORLINE_GATE_LOCK_REENTRY_TOKEN "${HARBORLINE_GATE_LOCK_REENTRY_TOKEN:-}" > "$3/token.env"
      # Leave the owner record behind, as after a crash, and reserve takeover so
      # acquire must wait/refuse instead of legitimately becoming a new owner.
      mkdir "$HARBORLINE_GATE_LOCK_PATH/takeover"
      trap - EXIT ;;
  esac
  exit $?
fi

case "${1:-all}" in all|last|trailing|legacy|missing|stale) ;; *) echo 'unknown test case' >&2; exit 2 ;; esac
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
unset HARBORLINE_GATE_LOCK_PATH HARBORLINE_GATE_LOCK_REENTRY_TOKEN
unset HARBORLINE_GATE_LOCK_OWNER_PID HARBORLINE_GATE_LOCK_OWNER_START HARBORLINE_GATE_LOCK_OWNER_NONCE
git init -q "$fixture/repo" || exit 1
cd "$fixture/repo" || exit 1
export HARBORLINE_GATE_LOCK_PATH="$fixture/test.lock"
test_script="$here/gate-lock-reentry.test.sh"
fails=0
check() {
  local name=$1 expected=$2 status=$3
  if [ "$status" -eq "$expected" ]; then
    printf 'PASS %s (exit %s)\n' "$name" "$status"
    cat "$fixture/out"
  else
    printf 'FAIL %s (exit %s, expected %s)\n' "$name" "$status" "$expected"
    cat "$fixture/out"
    fails=$((fails+1))
  fi
}

for shape in last trailing legacy missing; do
  [ "${1:-all}" = all ] || [ "$1" = "$shape" ] || continue
  expected=0
  [ "$shape" != missing ] || expected=143
  bash "$test_script" owner "$shape" > "$fixture/out" 2>&1
  status=$?
  if [ "$status" = 25 ]; then printf 'SKIP %s subshell (this bash forks the last command of a subshell; shape not producible)
' "$shape"; continue; fi
  if [ "$status" = 0 ] && [ "$expected" = 0 ] && ! grep -Fq "acquired $shape" "$fixture/out"; then status=1; fi
  check "$shape subshell" "$expected" "$status"
done

if [ "${1:-all}" = all ] || [ "$1" = stale ]; then
bash "$test_script" owner stale "$fixture" > "$fixture/out" 2>&1 || exit 1
(
  source "$fixture/token.env"
  # The owner was waited for above: this is a real exited process, not a made-up PID.
  kill -0 "$HARBORLINE_GATE_LOCK_OWNER_PID" 2>/dev/null && exit 23
  exec bash "$test_script" stub stale
) > "$fixture/out" 2>&1
check 'exited owner token cannot reuse' 143 "$?"
fi
printf 'gate-lock reentry: %s failures\n' "$fails"
[ "$fails" -eq 0 ]
