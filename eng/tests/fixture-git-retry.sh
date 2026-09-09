#!/usr/bin/env bash
# Shared retry for writes made while creating disposable git fixture repositories.
git_r() {
  local stderr_file rc retry=0
  stderr_file=$(mktemp)
  while :; do
    git "$@" 2>"$stderr_file"
    rc=$?
    if [ "$rc" -eq 0 ]; then
      rm -f "$stderr_file"
      return 0
    fi
    if [ "$rc" -eq 128 ] && grep -E -q 'Permission denied|unable to write new index file|could not write config file|Unable to create .*index.lock' "$stderr_file" && [ "$retry" -lt 5 ]; then
      retry=$((retry + 1))
      printf 'fixture: retried git %s (%s)\n' "$*" "$retry"
      sleep 0.5
      continue
    fi
    cat "$stderr_file" >&2
    rm -f "$stderr_file"
    return "$rc"
  done
}
