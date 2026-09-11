#!/usr/bin/env bash
# Ticket 333: prove a held owner lock can be reused by the exact landing subshell shape even
# when Bash forks verify one level deeper. The deadline is an assertion, never a settling sleep.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
lock_source="$here/../gate-lock.sh"

if [ "${1:-}" = stub ]; then
  source "$lock_source"
  expected=$2
  case "$expected" in
    last) [ "$PPID" = "$HARBORLINE_GATE_LOCK_OWNER_PID" ] || exit 25 ;;
    trailing|wrong|missing|stale) [ "$PPID" != "$HARBORLINE_GATE_LOCK_OWNER_PID" ] || exit 21 ;;
  esac
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
  secret_nonce=$HARBORLINE_GATE_LOCK_REENTRY_NONCE
  [ -n "$secret_nonce" ] && ! grep -Fq "$secret_nonce" "$HARBORLINE_GATE_LOCK_PATH/owner" || exit 26
  case "$2" in
    last) ( cd . && bash "$0" stub last ) ;;
    trailing) ( cd . && bash "$0" stub trailing && true ) ;;
    wrong)
      HARBORLINE_GATE_LOCK_REENTRY_NONCE="wrong-$secret_nonce"
      export HARBORLINE_GATE_LOCK_REENTRY_NONCE
      ( cd . && bash "$0" stub wrong && true ) ;;
    missing)
      unset HARBORLINE_GATE_LOCK_REENTRY_NONCE
      ( cd . && bash "$0" stub missing && true ) ;;
    stale)
      printf 'export %s=%q\n' \
        HARBORLINE_GATE_LOCK_OWNER_PID "$HARBORLINE_GATE_LOCK_OWNER_PID" \
        HARBORLINE_GATE_LOCK_OWNER_START "$HARBORLINE_GATE_LOCK_OWNER_START" \
        HARBORLINE_GATE_LOCK_REENTRY_NONCE "$HARBORLINE_GATE_LOCK_REENTRY_NONCE" > "$3/token.env"
      # Reserve takeover after the owner exits so the stale-nonce test must refuse rather than
      # legitimately acquire a newly vacant lock.
      mkdir "$HARBORLINE_GATE_LOCK_PATH/takeover"
      trap - EXIT ;;
  esac
  exit $?
fi

case "${1:-all}" in all|last|trailing|wrong|missing|stale) ;; *) echo 'unknown test case' >&2; exit 2 ;; esac
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
unset HARBORLINE_GATE_LOCK_PATH HARBORLINE_GATE_LOCK_REENTRY_NONCE
unset HARBORLINE_GATE_LOCK_OWNER_PID HARBORLINE_GATE_LOCK_OWNER_START
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

for shape in last trailing wrong missing; do
  [ "${1:-all}" = all ] || [ "$1" = "$shape" ] || continue
  expected=0
  case "$shape" in wrong|missing) expected=143 ;; esac
  bash "$test_script" owner "$shape" > "$fixture/out" 2>&1
  status=$?
  if [ "$status" = 25 ]; then
    printf 'SKIP %s subshell (this Bash forks a final subshell command)\n' "$shape"
    continue
  fi
  if [ "$status" = 0 ] && [ "$expected" = 0 ] && ! grep -Fq "acquired $shape" "$fixture/out"; then status=1; fi
  check "$shape nonce re-entry" "$expected" "$status"
done

if [ "${1:-all}" = all ] || [ "$1" = stale ]; then
  bash "$test_script" owner stale "$fixture" > "$fixture/out" 2>&1 || exit 1
  (
    source "$fixture/token.env"
    kill -0 "$HARBORLINE_GATE_LOCK_OWNER_PID" 2>/dev/null && exit 23
    exec bash "$test_script" stub stale
  ) > "$fixture/out" 2>&1
  check 'stale nonce cannot reuse' 143 "$?"
fi
printf 'gate-lock reentry: %s failures\n' "$fails"
[ "$fails" -eq 0 ]
