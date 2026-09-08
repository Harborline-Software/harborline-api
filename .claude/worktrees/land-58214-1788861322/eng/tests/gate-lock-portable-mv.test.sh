#!/usr/bin/env bash
# Ticket 286: the gate lock must acquire, publish and tombstone with a `mv` that rejects -T.
#
# `mv -T` is GNU-only. On macOS the BSD mv rejected it, the publish silently failed, and
# gate_lock_acquire looped forever without printing a step. This test puts a fake mv on PATH that
# refuses -T exactly the way BSD mv does, then drives the three paths that used the flag with a
# bounded wait, so a regression shows up as a failure rather than as a hang.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
lock_source="$here/../gate-lock.sh"
real_mv=$(command -v mv) || { echo "no mv on PATH" >&2; exit 1; }
fixture=$(mktemp -d)
repo="$fixture/repo"
fails=0

cleanup() { rm -rf "$fixture"; }
trap cleanup EXIT

git init -q "$repo"
git -C "$repo" -c user.email=t@t -c user.name=t commit --allow-empty -qm init
common=$(git -C "$repo" rev-parse --git-common-dir)
case "$common" in /*|[A-Za-z]:/*) ;; *) common="$repo/$common" ;; esac
lock="$common/harborline-gate.lock"

mkdir "$fixture/bin"
cat > "$fixture/bin/mv" <<FAKE
#!/usr/bin/env bash
for arg in "\$@"; do
  case "\$arg" in
    --) break ;;
    -T|--no-target-directory) printf 'mv: illegal option -- T\n' >&2; exit 1 ;;
  esac
done
exec "$real_mv" "\$@"
FAKE
chmod +x "$fixture/bin/mv"

check() {
  local name=$1 ok=$2
  if [ "$ok" = 0 ]; then
    printf 'ok   %s\n' "$name"
  else
    printf 'FAIL %s\n' "$name" >&2
    fails=$((fails+1))
  fi
}

# Bounded: the defect this test guards is an infinite retry loop, so a wait that never ends is
# itself the failure. `timeout` is not on every macOS box, so poll the child instead.
run_bounded() {
  local seconds=$1 pid status=0 waited=0
  shift
  "$@" > "$fixture/out" 2>&1 &
  pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$waited" -ge $((seconds * 10)) ]; then
      kill -TERM "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
      return 124
    fi
    sleep 0.1
    waited=$((waited+1))
  done
  wait "$pid" || status=$?
  return "$status"
}

# Acquire (mkdir candidate, write owner, publish by rename) then release (tombstone rename), all
# with the fake mv in front. The child asserts the lock exists while held; the parent asserts it is
# gone afterwards, which is the tombstone path.
acquire_and_release() {
  PATH="$fixture/bin:$PATH" bash -c '
    cd "$1" || exit 1
    source "$2"
    gate_lock_acquire "gate-lock-portable-mv.test" || exit 3
    [ -f "$3/owner" ] || exit 4
    [ "${HARBORLINE_GATE_LOCK_OWNED:-0}" = 1 ] || exit 5
  ' bash "$repo" "$lock_source" "$lock"
}

run_bounded 20 acquire_and_release
status=$?
[ "$status" = 0 ] || cat "$fixture/out" >&2
check "acquire and publish succeed with an mv that rejects -T (exit $status)" "$([ "$status" = 0 ] && echo 0 || echo 1)"
check "release tombstones the lock directory" "$([ ! -d "$lock" ] && echo 0 || echo 1)"

# Stale takeover: a lock owned by a dead pid must be tombstoned and replaced, which is the third
# `mv -T` site.
dead_pid=$( ( exit 0 ) & echo $! )
wait "$dead_pid" 2>/dev/null || true
mkdir -p "$lock"
printf '%s\nnot-the-process-start\nstale-nonce\nstale-holder\n2000-01-01T00:00:00Z\n' \
  "$dead_pid" > "$lock/owner"

run_bounded 20 acquire_and_release
status=$?
[ "$status" = 0 ] || cat "$fixture/out" >&2
check "stale takeover tombstones and republishes (exit $status)" "$([ "$status" = 0 ] && echo 0 || echo 1)"
check "stale takeover leaves no lock behind" "$([ ! -d "$lock" ] && echo 0 || echo 1)"

if [ "$fails" -gt 0 ]; then
  printf 'gate-lock portable-mv self-test: %d failure(s)\n' "$fails" >&2
  exit 1
fi
printf 'gate-lock portable-mv self-test: PASS\n'
