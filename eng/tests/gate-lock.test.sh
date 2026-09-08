#!/usr/bin/env bash
# Property tests for the repository-wide gate mutex. Every concurrency case uses real processes.
set -uo pipefail
unset HARBORLINE_GATE_LOCK_PATH HARBORLINE_GATE_LOCK_REENTRY_TOKEN
here=$(cd "$(dirname "$0")" && pwd)
lock_source="$here/../gate-lock.sh"
fixture=$(mktemp -d)
repo="$fixture/repo"
worktree="$fixture/worktree"
events="$fixture/events"
mkdir -p "$events"
git init -q "$repo"
git -C "$repo" -c user.email=t@t -c user.name=t commit --allow-empty -qm init
git -C "$repo" worktree add -q -b lock-test "$worktree"
common=$(git -C "$repo" rev-parse --git-common-dir)
case "$common" in /*|[A-Za-z]:/*) ;; *) common="$repo/$common" ;; esac
lock="$common/harborline-gate.lock"
fails=0

cleanup() {
  local pid
  for pid in $(jobs -pr); do kill -TERM "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
  git -C "$repo" worktree remove --force "$worktree" >/dev/null 2>&1 || true
  rm -rf "$fixture"
}
trap cleanup EXIT

wait_for_file() {
  local path=$1 attempts=${2:-200}
  while [ ! -f "$path" ] && [ "$attempts" -gt 0 ]; do
    sleep 0.025
    attempts=$((attempts-1))
  done
  [ -f "$path" ]
}

make_pause() {
  rm -f "$1" "$1.ready"
  mkfifo "$1"
}

resume_pause() {
  printf 'continue\n' > "$1"
  rm -f "$1" "$1.ready"
}

write_dead_lock() {
  local command=$1 pid=${2:-} nonce="dead-$RANDOM-$RANDOM"
  if [ -z "$pid" ]; then
    ( exit 0 ) & pid=$!
    wait "$pid"
  fi
  mkdir "$lock"
  printf '%s\n%s\n%s\n%s\n%s\n' "$pid" not-the-process-start "$nonce" "$command" \
    2000-01-01T00:00:00Z > "$lock/owner"
  # Legacy companions keep these properties runnable against the rejected aefc852e protocol.
  printf '%s\n' "$pid" > "$lock/pid"
  printf '%s\n' not-the-process-start > "$lock/process-start"
  printf '%s\n' 2000-01-01T00:00:00Z > "$lock/started"
  printf '%s\n' "$command" > "$lock/command"
}

start_holder() {
  local cwd=$1 name=$2 hold=$3 holder_events=${4:-$events}
  (
    cd "$cwd" || exit 1
    exec bash -c '
      source "$1"
      gate_lock_acquire "$2"
      entered=0
      if mkdir "$3/critical" 2>/dev/null; then entered=1; else : > "$3/overlap"; fi
      date +%s%N > "$3/$2.start"
      sleep "$4"
      date +%s%N > "$3/$2.end"
      [ "$entered" -eq 0 ] || rmdir "$3/critical"
    ' gate-holder "$lock_source" "$name" "$holder_events" "$hold"
  )
}

if grep -Fq 'source "$root/eng/gate-lock.sh"' "$here/../verify.sh" &&
   grep -Eq '^gate_lock_acquire ' "$here/../verify.sh" &&
   grep -Fq 'source "$root/eng/gate-lock.sh"' "$here/../land.sh" &&
   grep -Eq '^gate_lock_acquire ' "$here/../land.sh"; then
  echo "ok   verify and land entry points acquire the lock"
else
  echo "FAIL verify or land entry point does not acquire the lock"
  fails=$((fails+1))
fi

(
  cd "$repo" || exit 1
  exec bash -c '
    source "$1"
    gate_lock_acquire outer-land
    [ -n "$HARBORLINE_GATE_LOCK_OWNER_START" ] && [ -n "$HARBORLINE_GATE_LOCK_OWNER_NONCE" ] || exit 1
    bash -c '\''
      source "$1"
      gate_lock_acquire nested-verify
      [ -d "$HARBORLINE_GATE_LOCK_PATH" ] && printf "nested\n"
    '\'' nested-gate "$1"
    nested_rc=$?
    [ -d "$2" ] || exit 1
    for invalid_field in PID START NONCE; do
      env "HARBORLINE_GATE_LOCK_OWNER_$invalid_field=wrong" timeout 2 bash -c '\''
        source "$1"
        gate_lock_acquire invalid-nested
      '\'' invalid-nested "$1" >/dev/null 2>&1
      invalid_nested_rc=$?
      [ "$invalid_nested_rc" -eq 124 ] && [ -d "$2" ] || exit 1
    done
    gate_lock_release
    exit "$nested_rc"
  ' outer-gate "$lock_source" "$lock"
) > "$events/nested.out" 2>&1
nested_rc=$?
nested_out=$(< "$events/nested.out")
if [ "$nested_rc" -eq 0 ] && [ "$nested_out" = nested ] && [ ! -d "$lock" ]; then
  echo "ok   nested reuse requires holder pid, start identity, and nonce"
else
  echo "FAIL nested gate: rc=$nested_rc output=$nested_out"
  fails=$((fails+1)); rm -rf "$lock"
fi

parent_events="$events/parent-reuse"
mkdir -p "$parent_events"
matrix_failures=0
source "$lock_source"
bash -c 'while :; do sleep 1; done' & matrix_live_pid=$!
matrix_live_start=$(bash -c 'source "$1"; _gate_lock_process_start "$2"' matrix "$lock_source" "$matrix_live_pid")
bash -c 'while :; do sleep 1; done' & matrix_dead_pid=$!
matrix_dead_start=$(bash -c 'source "$1"; _gate_lock_process_start "$2"' matrix "$lock_source" "$matrix_dead_pid")
kill -KILL "$matrix_dead_pid" 2>/dev/null || true
wait "$matrix_dead_pid" 2>/dev/null || true
for parent_state in live dead; do
  if [ "$parent_state" = live ]; then
    matrix_pid=$matrix_live_pid; matrix_start=$matrix_live_start
  else
    matrix_pid=$matrix_dead_pid; matrix_start=$matrix_dead_start
  fi
  for ppid_relation in match mismatch; do
    matrix_ppid=$matrix_pid
    [ "$ppid_relation" = match ] || matrix_ppid=not-the-parent
    for identity in match alias; do
      matrix_identity_start=$matrix_start
      [ "$identity" = match ] || matrix_identity_start="alias-$matrix_start"
      GATE_LOCK_HOLDER_PID=$matrix_pid
      GATE_LOCK_HOLDER_PROCESS_START=$matrix_identity_start
      GATE_LOCK_HOLDER_NONCE=matrix-nonce
      HARBORLINE_GATE_LOCK_OWNER_PID=$matrix_pid
      HARBORLINE_GATE_LOCK_OWNER_START=$matrix_identity_start
      HARBORLINE_GATE_LOCK_OWNER_NONCE=matrix-nonce
      if _gate_lock_may_reuse "$matrix_ppid"; then matrix_result=reuse; else matrix_result=reject; fi
      matrix_expected=reject
      if [ "$parent_state/$ppid_relation/$identity" = live/match/match ]; then matrix_expected=reuse; fi
      if [ "$matrix_result" != "$matrix_expected" ]; then
        matrix_failures=$((matrix_failures+1))
      fi
    done
  done
done
kill -KILL "$matrix_live_pid" 2>/dev/null || true
wait "$matrix_live_pid" 2>/dev/null || true
unset GATE_LOCK_HOLDER_PID GATE_LOCK_HOLDER_PROCESS_START GATE_LOCK_HOLDER_NONCE
unset HARBORLINE_GATE_LOCK_OWNER_PID HARBORLINE_GATE_LOCK_OWNER_START HARBORLINE_GATE_LOCK_OWNER_NONCE

# Reproduce the real parent-exit/PID-identity collision. The child must not reuse
# the unrelated live alias; after that alias exits, both competing processes must serialize.
bash -c 'printf "%s\n" "$$" > "$1/replacement.pid"; while :; do sleep 1; done' \
  replacement "$parent_events" & replacement_pid=$!
wait_for_file "$parent_events/replacement.pid"
replacement_start=$(bash -c 'source "$1"; _gate_lock_process_start "$2"' \
  identity "$lock_source" "$replacement_pid")
(
  cd "$repo" || exit 1
  exec bash -c '
    source "$1"
    gate_lock_acquire original-parent
    replacement_pid=$2; replacement_start=$3; events=$4
    nonce=$HARBORLINE_GATE_LOCK_OWNER_NONCE
    printf "%s\n%s\n%s\n%s\n%s\n" "$replacement_pid" "$replacement_start" \
      "$nonce" original-parent 2000-01-01T00:00:00Z > "$HARBORLINE_GATE_LOCK_PATH/owner"
    HARBORLINE_GATE_LOCK_OWNER_PID=$replacement_pid
    HARBORLINE_GATE_LOCK_OWNER_START=$replacement_start
    export HARBORLINE_GATE_LOCK_OWNER_PID HARBORLINE_GATE_LOCK_OWNER_START
    bash -c '\''
      while [ ! -f "$2/child.go" ]; do sleep 0.02; done
      source "$1"
      gate_lock_acquire inherited-child
      printf "%s\n" "$PPID" > "$2/child.ppid"
      if mkdir "$2/critical" 2>/dev/null; then
        : > "$2/child.enter"; sleep 4; rmdir "$2/critical" 2>/dev/null || true
      else
        : > "$2/overlap"
      fi
      : > "$2/child.done"
    '\'' child "$1" "$events" > "$events/child.out" 2>&1 &
    printf "%s\n" "$!" > "$events/child.pid"
    : > "$events/owner.ready"
    while :; do sleep 1; done
  ' owner "$lock_source" "$replacement_pid" "$replacement_start" "$parent_events"
) > "$parent_events/owner.out" 2>&1 & parent_owner_pid=$!
wait_for_file "$parent_events/owner.ready"
wait_for_file "$parent_events/child.pid"
kill -KILL "$parent_owner_pid" 2>/dev/null || true
wait "$parent_owner_pid" 2>/dev/null || true
: > "$parent_events/child.go"
if wait_for_file "$parent_events/child.enter" 40; then parent_premature=yes; else parent_premature=no; fi
kill -KILL "$replacement_pid" 2>/dev/null || true
wait "$replacement_pid" 2>/dev/null || true
start_holder "$worktree" parent-competitor 1 "$parent_events" > "$parent_events/competitor.out" 2>&1 & parent_competitor=$!
wait "$parent_competitor"; parent_competitor_rc=$?
if wait_for_file "$parent_events/child.done" 200; then parent_child_rc=0; else parent_child_rc=124; fi
parent_child_ppid=$(< "$parent_events/child.ppid")
parent_takeovers=$(grep -hF 'gate: taking over stale' \
  "$parent_events/child.out" "$parent_events/competitor.out" | wc -l | tr -d ' ')
if [ "$matrix_failures" -eq 0 ] && [ "$parent_premature" = no ] &&
   [ "$parent_child_ppid" != "$replacement_pid" ] &&
   [ "$parent_child_rc" -eq 0 ] && [ "$parent_competitor_rc" -eq 0 ] &&
   [ "$parent_takeovers" -eq 1 ] && [ ! -f "$parent_events/overlap" ]; then
  echo "ok   PARENT_REUSE permits only the live actual parent and preserves cardinality one"
else
  echo "FAIL PARENT_REUSE: matrix=$matrix_failures premature=$parent_premature rc=$parent_child_rc/$parent_competitor_rc takeovers=$parent_takeovers overlap=$([ -f "$parent_events/overlap" ] && echo yes || echo no)"
  fails=$((fails+1)); rm -rf "$lock"
fi

snapshot_lock() {
  (
    cd "$lock" || exit 1
    find . -mindepth 1 -printf 'entry %P\n' | LC_ALL=C sort
    while IFS= read -r path; do
      printf 'sha256 %s ' "$path"
      sha256sum "$path" | awk '{ print $1 }'
    done < <(find . -type f -printf '%P\n' | LC_ALL=C sort)
  )
}

layout_live_pid=$$
layout_live_start=$(bash -c 'source "$1"; _gate_lock_process_start "$2"' \
  layout-process-start "$lock_source" "$layout_live_pid")
layout_root=$(git -C "$repo" rev-parse --show-toplevel)
layout_common=$(git -C "$repo" rev-parse --git-common-dir)
case "$layout_common" in /*|[A-Za-z]:/*) ;; *) layout_common="$layout_root/$layout_common" ;; esac
layout_reported_lock="$layout_common/harborline-gate.lock"
layout_values=("$layout_live_pid" "$layout_live_start" property-nonce property-command 2000-01-01T00:00:00Z unexpected-sixth)
layout_failures=0
for layout_case in 0 1 2 3 4 5-empty 6; do
  layout_arity=${layout_case%%-*}
  mkdir -p "$lock/unknown/nested"
  : > "$lock/owner"
  if [ "$layout_arity" -gt 0 ]; then
    printf '%s\n' "${layout_values[@]:0:layout_arity}" > "$lock/owner"
  fi
  if [ "$layout_case" = 5-empty ]; then
    printf '%s\n%s\n\n%s\n%s\n' "$layout_live_pid" "$layout_live_start" \
      property-command 2000-01-01T00:00:00Z > "$lock/owner"
  fi
  printf '%s\n' companion-pid > "$lock/pid"
  printf '%s\n' companion-start > "$lock/process-start"
  printf '%s\n' companion-command > "$lock/command"
  printf '%s\n' companion-started > "$lock/started"
  printf '%s\n' unknown-root > "$lock/unknown-entry"
  printf '%s\n' unknown-nested > "$lock/unknown/nested/payload"
  layout_before="$events/layout-$layout_case.before"
  layout_after="$events/layout-$layout_case.after"
  layout_stdout="$events/layout-$layout_case.stdout"
  layout_stderr="$events/layout-$layout_case.stderr"
  snapshot_lock > "$layout_before"
  ( cd "$repo" && timeout 5 bash -c '
      source "$1"; gate_lock_acquire malformed-layout
    ' malformed-layout "$lock_source" ) > "$layout_stdout" 2> "$layout_stderr"
  layout_rc=$?
  snapshot_lock > "$layout_after"
  if [ "$layout_rc" -eq 0 ] || [ "$layout_rc" -eq 124 ] ||
     ! grep -Fq "unrecognized lock layout at $layout_reported_lock; remove it by hand if no gate is running" "$layout_stderr" ||
     ! cmp -s "$layout_before" "$layout_after"; then
    echo "FAIL unrecognized layout arity $layout_case: rc=$layout_rc path=$(grep -Fq "$layout_reported_lock" "$layout_stderr" && echo reported || echo missing) unchanged=$([ -f "$layout_after" ] && cmp -s "$layout_before" "$layout_after" && echo yes || echo no)"
    layout_failures=$((layout_failures+1))
  fi
  rm -rf "$lock"
done
if [ "$layout_failures" -eq 0 ]; then
  echo "ok   every unrecognized owner arity fails promptly and preserves all entries and bytes"
else
  fails=$((fails+1))
fi

mkdir "$lock"
printf '%s\n%s\n%s\n%s\n%s\n' "$layout_live_pid" "$layout_live_start" live-nonce \
  live-exact-five 2000-01-01T00:00:00Z > "$lock/owner"
live_five_out=$(cd "$repo" && timeout 5 bash -c '
  source "$1"; gate_lock_acquire live-five-waiter
' live-five "$lock_source" 2>&1)
live_five_rc=$?
if [ "$live_five_rc" -eq 124 ] && grep -Fq 'gate: waiting for live-exact-five' <<<"$live_five_out" &&
   [ -f "$lock/owner" ]; then
  echo "ok   live exact-five owner makes a competitor wait"
else
  echo "FAIL live exact-five owner: rc=$live_five_rc output=$live_five_out"
  fails=$((fails+1))
fi
rm -rf "$lock"

write_dead_lock dead-exact-five
dead_five_out=$(cd "$repo" && timeout 5 bash -c '
  source "$1"; gate_lock_acquire dead-five-reclaimer; gate_lock_release
' dead-five "$lock_source" 2>&1)
dead_five_rc=$?
if [ "$dead_five_rc" -eq 0 ] && grep -Fq 'gate: taking over stale dead-exact-five' <<<"$dead_five_out" &&
   [ ! -d "$lock" ]; then
  echo "ok   dead exact-five owner is reclaimed"
else
  echo "FAIL dead exact-five owner: rc=$dead_five_rc output=$dead_five_out"
  fails=$((fails+1)); rm -rf "$lock"
fi

rm -f "$events/overlap"
start_holder "$repo" first 1 & first_pid=$!
wait_for_file "$events/first.start"
start_holder "$worktree" second 0 & second_pid=$!
wait "$first_pid"; first_rc=$?
wait "$second_pid"; second_rc=$?
if [ "$first_rc" -eq 0 ] && [ "$second_rc" -eq 0 ] && [ ! -f "$events/overlap" ] &&
   [ "$(< "$events/second.start")" -ge "$(< "$events/first.end")" ]; then
  echo "ok   concurrent holders serialize"
else
  echo "FAIL concurrent holders: rc=$first_rc/$second_rc"
  fails=$((fails+1))
fi

write_dead_lock two-waiters
rm -f "$events/overlap"
start_holder "$repo" dead-a 1 > "$events/dead-a.out" 2>&1 & dead_a=$!
start_holder "$worktree" dead-b 1 > "$events/dead-b.out" 2>&1 & dead_b=$!
wait "$dead_a"; dead_a_rc=$?
wait "$dead_b"; dead_b_rc=$?
dead_takeovers=$(grep -hF 'gate: taking over stale' "$events/dead-a.out" "$events/dead-b.out" | wc -l | tr -d ' ')
if [ "$dead_a_rc" -eq 0 ] && [ "$dead_b_rc" -eq 0 ] && [ "$dead_takeovers" -eq 1 ] &&
   [ ! -f "$events/overlap" ]; then
  echo "ok   two waiters reclaim one dead holder into one live holder"
else
  echo "FAIL two-waiter takeover: rc=$dead_a_rc/$dead_b_rc takeovers=$dead_takeovers"
  fails=$((fails+1)); rm -rf "$lock"
fi

write_dead_lock stale-aba
aba_fifo="$events/aba.fifo"; make_pause "$aba_fifo"; rm -f "$events/overlap"
( export HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_INSPECT="$aba_fifo"; start_holder "$repo" aba-a 1 ) \
  > "$events/aba-a.out" 2>&1 & aba_a=$!
if wait_for_file "$aba_fifo.ready"; then
  start_holder "$worktree" aba-b 2 > "$events/aba-b.out" 2>&1 & aba_b=$!
  wait_for_file "$events/aba-b.start"
  resume_pause "$aba_fifo"
  wait "$aba_a"; aba_a_rc=$?
  wait "$aba_b"; aba_b_rc=$?
  aba_takeovers=$(grep -hF 'gate: taking over stale' "$events/aba-a.out" "$events/aba-b.out" | wc -l | tr -d ' ')
  if [ "$aba_a_rc" -eq 0 ] && [ "$aba_b_rc" -eq 0 ] && [ "$aba_takeovers" -eq 1 ] &&
     [ ! -f "$events/overlap" ] && [ "$(< "$events/aba-a.start")" -ge "$(< "$events/aba-b.end")" ]; then
    echo "ok   stale inspector cannot remove the replacement holder"
  else
    echo "FAIL STALE_ABA: rc=$aba_a_rc/$aba_b_rc takeovers=$aba_takeovers"
    fails=$((fails+1))
  fi
else
  echo "FAIL STALE_ABA pause point was not reached"
  fails=$((fails+1)); kill -TERM "$aba_a" 2>/dev/null || true; wait "$aba_a" 2>/dev/null || true
fi
rm -rf "$lock"

write_dead_lock nonce-original
nonce_fifo="$events/nonce.fifo"; make_pause "$nonce_fifo"
( export HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_INSPECT="$nonce_fifo"; start_holder "$repo" nonce-a 0 ) \
  > "$events/nonce-a.out" 2>&1 & nonce_a=$!
if wait_for_file "$nonce_fifo.ready"; then
  (
    cd "$worktree" || exit 1
    exec bash -c '
      source "$1"; gate_lock_acquire nonce-replacement
      : > "$2/nonce-replacement.enter"; kill -KILL "$$"
    ' nonce-replacement "$lock_source" "$events"
  ) > "$events/nonce-replacement.out" 2>&1 & nonce_replacement=$!
  wait_for_file "$events/nonce-replacement.enter"
  wait "$nonce_replacement" 2>/dev/null; nonce_replacement_rc=$?
  resume_pause "$nonce_fifo"
  wait "$nonce_a"; nonce_a_rc=$?
  if [ "$nonce_replacement_rc" -eq 137 ] && [ "$nonce_a_rc" -eq 0 ] &&
     grep -Fq 'gate: taking over stale nonce-replacement' "$events/nonce-a.out" &&
     ! grep -Fq 'gate: taking over stale nonce-original' "$events/nonce-a.out"; then
    echo "ok   takeover nonce binds reclamation to the inspected instance"
  else
    echo "FAIL takeover nonce: rc=$nonce_replacement_rc/$nonce_a_rc"
    fails=$((fails+1))
  fi
else
  echo "FAIL takeover nonce pause point was not reached"
  fails=$((fails+1)); kill -TERM "$nonce_a" 2>/dev/null || true; wait "$nonce_a" 2>/dev/null || true
fi
rm -rf "$lock"

write_dead_lock three-competitors
inspect_fifo="$events/third-inspect.fifo"; marker_fifo="$events/third-marker.fifo"
make_pause "$inspect_fifo"; make_pause "$marker_fifo"; rm -f "$events/overlap"
(
  export HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_INSPECT="$inspect_fifo"
  export HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_MARKER="$marker_fifo"
  start_holder "$repo" third-a 1
) > "$events/third-a.out" 2>&1 & third_a=$!
if wait_for_file "$inspect_fifo.ready"; then
  start_holder "$worktree" third-b 3 > "$events/third-b.out" 2>&1 & third_b=$!
  wait_for_file "$events/third-b.start"
  resume_pause "$inspect_fifo"
  if wait_for_file "$marker_fifo.ready"; then
    start_holder "$repo" third-c 0 > "$events/third-c.out" 2>&1 & third_c=$!
    sleep 0.2
    resume_pause "$marker_fifo"
    wait "$third_a"; third_a_rc=$?
    wait "$third_b"; third_b_rc=$?
    wait "$third_c"; third_c_rc=$?
    if [ "$third_a_rc" -eq 0 ] && [ "$third_b_rc" -eq 0 ] && [ "$third_c_rc" -eq 0 ] &&
       [ ! -f "$events/overlap" ] && [ "$(< "$events/third-c.start")" -ge "$(< "$events/third-b.end")" ]; then
      echo "ok   third competitor cannot enter beside a replacement holder"
    else
      echo "FAIL THREE_COMPETITORS: rc=$third_a_rc/$third_b_rc/$third_c_rc"
      fails=$((fails+1))
    fi
  else
    echo "FAIL third-competitor marker pause point was not reached"
    fails=$((fails+1))
  fi
else
  echo "FAIL third-competitor inspection pause point was not reached"
  fails=$((fails+1))
fi
for pid in ${third_a:-} ${third_b:-} ${third_c:-}; do kill -TERM "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; done
rm -rf "$lock"

publish_fifo="$events/publish.fifo"; make_pause "$publish_fifo"; rm -f "$events/overlap"
( export HARBORLINE_GATE_LOCK_TEST_PAUSE_BEFORE_PUBLISH="$publish_fifo"; start_holder "$repo" publish-a 1 ) \
  > "$events/publish-a.out" 2>&1 & publish_a=$!
if wait_for_file "$publish_fifo.ready"; then
  start_holder "$worktree" publish-b 2 > "$events/publish-b.out" 2>&1 & publish_b=$!
  wait_for_file "$events/publish-b.start"
  resume_pause "$publish_fifo"
  wait "$publish_a"; publish_a_rc=$?
  wait "$publish_b"; publish_b_rc=$?
  if [ "$publish_a_rc" -eq 0 ] && [ "$publish_b_rc" -eq 0 ] && [ ! -f "$events/overlap" ] &&
     [ "$(< "$events/publish-a.start")" -ge "$(< "$events/publish-b.end")" ]; then
    echo "ok   PARTIAL_PUBLISH exposes no incomplete canonical lock"
  else
    echo "FAIL PARTIAL_PUBLISH: rc=$publish_a_rc/$publish_b_rc"
    fails=$((fails+1))
  fi
else
  echo "FAIL PARTIAL_PUBLISH pause point was not reached"
  fails=$((fails+1)); kill -TERM "$publish_a" 2>/dev/null || true; wait "$publish_a" 2>/dev/null || true
fi
rm -rf "$lock"

write_dead_lock pid-reuse "$$"
pid_reuse_out=$(cd "$worktree" && timeout 5 bash -c '
  source "$1"; gate_lock_acquire pid-reuse; gate_lock_release
' pid-reuse "$lock_source" 2>&1)
pid_reuse_rc=$?
if [ "$pid_reuse_rc" -eq 0 ] && grep -Fq 'gate: taking over stale pid-reuse' <<<"$pid_reuse_out" &&
   [ ! -d "$lock" ]; then
  echo "ok   live PID with a different process start is reclaimed"
else
  echo "FAIL PID reuse: rc=$pid_reuse_rc output=$pid_reuse_out"
  fails=$((fails+1)); rm -rf "$lock"
fi

for signal in INT TERM; do
  (
    cd "$repo" || exit 1
    exec bash -c '
      source "$1"; gate_lock_acquire "$2"; printf "ready\n" > "$3/$2.ready"
      while :; do sleep 1; done
    ' signal-holder "$lock_source" "signal-$signal" "$events"
  ) & signal_pid=$!
  wait_for_file "$events/signal-$signal.ready"
  kill -"$signal" "$signal_pid"
  wait "$signal_pid" 2>/dev/null; signal_rc=$?
  expected=130; [ "$signal" = TERM ] && expected=143
  if [ "$signal_rc" -eq "$expected" ] && [ ! -d "$lock" ]; then
    echo "ok   $signal releases the held lock"
  else
    echo "FAIL $signal release: rc=$signal_rc expected=$expected"
    fails=$((fails+1)); rm -rf "$lock"
  fi
done

( cd "$repo" && bash -c 'set -e; source "$1"; gate_lock_acquire error; false' error "$lock_source" ) \
  >/dev/null 2>&1
error_rc=$?
if [ "$error_rc" -ne 0 ] && [ ! -d "$lock" ]; then
  echo "ok   uncaught error releases the held lock"
else
  echo "FAIL uncaught error release: rc=$error_rc"
  fails=$((fails+1)); rm -rf "$lock"
fi

rm -f "$events/overlap"
start_holder "$repo" long-live 4 > "$events/long-live.out" 2>&1 & live_pid=$!
wait_for_file "$events/long-live.start"
wait_started=$(date +%s)
start_holder "$worktree" live-waiter 0 > "$events/live-waiter.out" 2>&1 & live_waiter_pid=$!
wait "$live_pid"; live_rc=$?
wait "$live_waiter_pid"; live_waiter_rc=$?
wait_elapsed=$(($(date +%s) - wait_started))
if [ "$live_rc" -eq 0 ] && [ "$live_waiter_rc" -eq 0 ] && [ "$wait_elapsed" -ge 3 ] &&
   [ ! -f "$events/overlap" ] && ! grep -Fq 'gate: taking over stale' "$events/live-waiter.out"; then
  echo "ok   long-lived live holder is never taken over"
else
  echo "FAIL live holder: rc=$live_rc/$live_waiter_rc elapsed=$wait_elapsed"
  fails=$((fails+1))
fi

receipt_repo="$fixture/receipt-repo"
mkdir -p "$receipt_repo/eng" "$receipt_repo/packages/contracts" "$receipt_repo/fake-bin"
git init -q "$receipt_repo"
cp "$here/../verify.sh" "$here/../gate-lock.sh" "$receipt_repo/eng/"
printf '#!/usr/bin/env bash\nexit 0\n' > "$receipt_repo/eng/verify-boundaries.sh"
printf '#!/usr/bin/env bash\nexit 0\n' > "$receipt_repo/eng/identity-r3-scan.sh"
printf '#!/usr/bin/env bash\nexit 0\n' > "$receipt_repo/eng/verify-packages.sh"
for tool in dotnet cargo npm; do printf '#!/usr/bin/env bash\nexit 0\n' > "$receipt_repo/fake-bin/$tool"; done
printf '%s\n' '#!/usr/bin/env bash' \
  'if [ "$1" = eng/verify-receipt.mjs ] && [ "$2" = --record ]; then' \
  '  lock="$(git rev-parse --git-common-dir)/harborline-gate.lock"' \
  '  read -r owner_pid < "$lock/owner"' \
  '  [ -d "$lock" ] && [ "$owner_pid" = "$PPID" ] || exit 9' \
  '  : > receipt-while-held' 'fi' 'exit 0' > "$receipt_repo/fake-bin/node"
chmod +x "$receipt_repo/eng/"*.sh "$receipt_repo/fake-bin/"*
( cd "$receipt_repo" && PATH="$receipt_repo/fake-bin:$PATH" bash eng/verify.sh ) > "$events/receipt.out" 2>&1
receipt_rc=$?
receipt_common=$(git -C "$receipt_repo" rev-parse --git-common-dir)
if [ "$receipt_rc" -eq 0 ] && [ -f "$receipt_repo/receipt-while-held" ] &&
   [ ! -d "$receipt_repo/$receipt_common/harborline-gate.lock" ]; then
  echo "ok   verification receipt is written while the lock is held"
else
  echo "FAIL receipt timing: rc=$receipt_rc"
  fails=$((fails+1))
fi

echo "19 cases, $fails failures"
[ "$fails" -eq 0 ]
