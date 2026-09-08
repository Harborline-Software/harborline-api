#!/usr/bin/env bash
# Shared repository gate lock. Source this file, then call gate_lock_acquire <command>.
# The lock lives in git's common directory so linked worktrees serialize with the main worktree.

_gate_lock_process_start() {
  local pid=$1
  if [ -r "/proc/$pid/stat" ]; then
    sed 's/^.*) //' "/proc/$pid/stat" | awk '{ print $20 }'
  else
    ps -p "$pid" -o lstart= 2>/dev/null | sed 's/^[[:space:]]*//'
  fi
}

_gate_lock_is_same_process() {
  local pid=$1 expected_start=$2 actual_start
  [ -n "$pid" ] && [ -n "$expected_start" ] && kill -0 "$pid" 2>/dev/null || return 1
  actual_start=$(_gate_lock_process_start "$pid") || return 1
  [ -n "$actual_start" ] && [ "$actual_start" = "$expected_start" ]
}

_gate_lock_nonce() {
  od -An -N16 -tx1 /dev/urandom | tr -d ' \n'
}

# Portable stand-in for `mv -T`, which is GNU-only: BSD/macOS mv rejects the flag, so on macOS the
# publish never happened and gate_lock_acquire retried forever without printing a thing (ticket 286).
# Plain mv would nest SRC inside DST when DST is an existing directory, which is exactly what -T
# existed to prevent, so the nesting is refused twice: the pre-check rejects the common case, and the
# post-check catches the race where DST appeared between the check and the rename. SRC basenames
# carry a pid and a nonce, so a directory of that name inside DST can only be the one we just moved.
_gate_lock_move_dir() {
  local src=$1 dst=$2 nested="$2/${1##*/}"
  [ -e "$dst" ] && return 1
  mv "$src" "$dst" 2>/dev/null || return 1
  if [ -e "$nested" ]; then
    mv "$nested" "$src" 2>/dev/null || true
    return 1
  fi
  return 0
}

_gate_lock_may_reuse() {
  local caller_parent_pid=$1
  # An inherited token admits descendants through any subshell topology. Bind it
  # to the complete owner record and a live process with the same start time;
  # stale environments and recycled PIDs must never grant re-entry.
  [ -n "${HARBORLINE_GATE_LOCK_OWNER_PID:-}" ] &&
    [ -n "${HARBORLINE_GATE_LOCK_OWNER_START:-}" ] &&
    [ -n "${HARBORLINE_GATE_LOCK_OWNER_NONCE:-}" ] &&
    [ "$GATE_LOCK_HOLDER_PID" = "$HARBORLINE_GATE_LOCK_OWNER_PID" ] &&
    [ "$GATE_LOCK_HOLDER_PROCESS_START" = "$HARBORLINE_GATE_LOCK_OWNER_START" ] &&
    [ "$GATE_LOCK_HOLDER_NONCE" = "$HARBORLINE_GATE_LOCK_OWNER_NONCE" ] &&
    _gate_lock_is_same_process "$HARBORLINE_GATE_LOCK_OWNER_PID" "$HARBORLINE_GATE_LOCK_OWNER_START" || return 1
  # Keep the original parent-pid path for callers carrying only the older fields.
  [ "$caller_parent_pid" = "$HARBORLINE_GATE_LOCK_OWNER_PID" ] ||
    [ "${HARBORLINE_GATE_LOCK_REENTRY_TOKEN:-}" = "$GATE_LOCK_HOLDER_PID:$GATE_LOCK_HOLDER_PROCESS_START:$GATE_LOCK_HOLDER_NONCE" ]
}

_gate_lock_read() {
  local owner_path=${1:-"$HARBORLINE_GATE_LOCK_PATH/owner"}
  GATE_LOCK_HOLDER_PID=""
  GATE_LOCK_HOLDER_PROCESS_START=""
  GATE_LOCK_HOLDER_NONCE=""
  GATE_LOCK_HOLDER_COMMAND="unknown"
  GATE_LOCK_HOLDER_STARTED="unknown"
  [ -f "$owner_path" ] || return 1
  {
    IFS= read -r GATE_LOCK_HOLDER_PID &&
      IFS= read -r GATE_LOCK_HOLDER_PROCESS_START &&
      IFS= read -r GATE_LOCK_HOLDER_NONCE &&
      IFS= read -r GATE_LOCK_HOLDER_COMMAND &&
      IFS= read -r GATE_LOCK_HOLDER_STARTED &&
      ! IFS= read -r _
  } < "$owner_path" || return 1
  [ -n "$GATE_LOCK_HOLDER_PID" ] &&
    [ -n "$GATE_LOCK_HOLDER_PROCESS_START" ] &&
    [ -n "$GATE_LOCK_HOLDER_NONCE" ]
}

_gate_lock_remove_known_directory() {
  local path=$1
  rmdir "$path/takeover" 2>/dev/null || true
  rm -f "$path/owner" "$path/pid" "$path/process-start" "$path/started" "$path/command"
  rmdir "$path" 2>/dev/null || true
}

# A named-pipe pause is reachable only when a test explicitly supplies an existing FIFO.
_gate_lock_test_pause() {
  local variable=$1 pause_path
  pause_path=${!variable:-}
  [ -n "$pause_path" ] && [ -p "$pause_path" ] || return 0
  : > "$pause_path.ready"
  IFS= read -r _ < "$pause_path"
}

gate_lock_release() {
  local tombstone
  if [ -n "${HARBORLINE_GATE_LOCK_CANDIDATE:-}" ]; then
    _gate_lock_remove_known_directory "$HARBORLINE_GATE_LOCK_CANDIDATE"
    HARBORLINE_GATE_LOCK_CANDIDATE=""
  fi
  [ "${HARBORLINE_GATE_LOCK_OWNED:-0}" = 1 ] || return 0
  if _gate_lock_read &&
     [ "$GATE_LOCK_HOLDER_PID" = "$$" ] &&
     [ "$GATE_LOCK_HOLDER_PROCESS_START" = "$HARBORLINE_GATE_LOCK_OWNER_START" ] &&
     [ "$GATE_LOCK_HOLDER_NONCE" = "$HARBORLINE_GATE_LOCK_OWNER_NONCE" ]; then
    tombstone="${HARBORLINE_GATE_LOCK_PATH}.release.$$.$HARBORLINE_GATE_LOCK_OWNER_NONCE"
    if _gate_lock_move_dir "$HARBORLINE_GATE_LOCK_PATH" "$tombstone"; then
      _gate_lock_remove_known_directory "$tombstone"
    fi
  fi
  HARBORLINE_GATE_LOCK_OWNED=0
}

_gate_lock_signal() {
  local status=$1
  trap - INT TERM
  exit "$status"
}

_gate_lock_take_stale() {
  local observed_pid=$GATE_LOCK_HOLDER_PID
  local observed_nonce=$GATE_LOCK_HOLDER_NONCE
  local observed_command=$GATE_LOCK_HOLDER_COMMAND
  local observed_started=$GATE_LOCK_HOLDER_STARTED
  local tombstone="${HARBORLINE_GATE_LOCK_PATH}.tombstone.$$.$(_gate_lock_nonce)"

  _gate_lock_test_pause HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_INSPECT
  mkdir "$HARBORLINE_GATE_LOCK_PATH/takeover" 2>/dev/null || return 1
  _gate_lock_test_pause HARBORLINE_GATE_LOCK_TEST_PAUSE_AFTER_MARKER

  if _gate_lock_read &&
     [ -n "$observed_nonce" ] &&
     [ "$GATE_LOCK_HOLDER_NONCE" = "$observed_nonce" ] &&
     ! _gate_lock_is_same_process "$GATE_LOCK_HOLDER_PID" "$GATE_LOCK_HOLDER_PROCESS_START"; then
    if _gate_lock_move_dir "$HARBORLINE_GATE_LOCK_PATH" "$tombstone"; then
      printf 'gate: taking over stale %s pid %s since %s\n' \
        "$observed_command" "$observed_pid" "$observed_started"
      _gate_lock_remove_known_directory "$tombstone"
      return 0
    fi
  fi
  rmdir "$HARBORLINE_GATE_LOCK_PATH/takeover" 2>/dev/null || true
  return 1
}

gate_lock_acquire() {
  local gate_command=${1:?gate_lock_acquire requires a command name}
  local gate_root gate_common now self_start started candidate last_wait=0
  gate_root=$(git rev-parse --show-toplevel) || return 1
  gate_common=$(git rev-parse --git-common-dir) || return 1
  case "$gate_common" in /*|[A-Za-z]:/*) ;; *) gate_common="$gate_root/$gate_common" ;; esac
  # Tests override this with a disposable path; normal gates share git's common dir.
  HARBORLINE_GATE_LOCK_PATH=${HARBORLINE_GATE_LOCK_PATH:-"$gate_common/harborline-gate.lock"}
  self_start=$(_gate_lock_process_start "$$") || return 1
  [ -n "$self_start" ] || return 1

  if _gate_lock_read && _gate_lock_may_reuse "$PPID"; then
    return 0
  fi

  HARBORLINE_GATE_LOCK_OWNER_PID=$$
  HARBORLINE_GATE_LOCK_OWNER_START=$self_start
  HARBORLINE_GATE_LOCK_OWNER_NONCE=$(_gate_lock_nonce) || return 1
  [ -n "$HARBORLINE_GATE_LOCK_OWNER_NONCE" ] || return 1
  started=$(date -u +%Y-%m-%dT%H:%M:%SZ) || return 1
  candidate="${HARBORLINE_GATE_LOCK_PATH}.candidate.$$.$HARBORLINE_GATE_LOCK_OWNER_NONCE"
  export HARBORLINE_GATE_LOCK_PATH HARBORLINE_GATE_LOCK_OWNER_PID \
    HARBORLINE_GATE_LOCK_OWNER_START HARBORLINE_GATE_LOCK_OWNER_NONCE
  trap gate_lock_release EXIT
  trap '_gate_lock_signal 130' INT
  trap '_gate_lock_signal 143' TERM

  while :; do
    if mkdir "$candidate" 2>/dev/null; then
      HARBORLINE_GATE_LOCK_CANDIDATE=$candidate
      if printf '%s\n%s\n%s\n%s\n%s\n' "$$" "$self_start" \
           "$HARBORLINE_GATE_LOCK_OWNER_NONCE" "$gate_command" "$started" > "$candidate/owner"; then
        _gate_lock_test_pause HARBORLINE_GATE_LOCK_TEST_PAUSE_BEFORE_PUBLISH
        if _gate_lock_move_dir "$candidate" "$HARBORLINE_GATE_LOCK_PATH"; then
          HARBORLINE_GATE_LOCK_CANDIDATE=""
          HARBORLINE_GATE_LOCK_OWNED=1
          export HARBORLINE_GATE_LOCK_REENTRY_TOKEN="$$:$self_start:$HARBORLINE_GATE_LOCK_OWNER_NONCE"
          return 0
        fi
      fi
      _gate_lock_remove_known_directory "$candidate"
      HARBORLINE_GATE_LOCK_CANDIDATE=""
    fi

    if _gate_lock_read; then
      if ! _gate_lock_is_same_process "$GATE_LOCK_HOLDER_PID" "$GATE_LOCK_HOLDER_PROCESS_START"; then
        _gate_lock_take_stale
        continue
      fi
    elif [ -d "$HARBORLINE_GATE_LOCK_PATH" ]; then
      printf 'gate: unrecognized lock layout at %s; remove it by hand if no gate is running\n' \
        "$HARBORLINE_GATE_LOCK_PATH" >&2
      return 1
    else
      continue
    fi
    now=$(date +%s)
    if [ "$last_wait" -eq 0 ] || [ $((now - last_wait)) -ge 60 ]; then
      printf 'gate: waiting for %s pid %s since %s\n' \
        "$GATE_LOCK_HOLDER_COMMAND" "${GATE_LOCK_HOLDER_PID:-unknown}" "$GATE_LOCK_HOLDER_STARTED"
      last_wait=$now
    fi
    sleep 1
  done
}
