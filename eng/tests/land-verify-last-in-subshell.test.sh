#!/usr/bin/env bash
# Ticket 333: every shell script which holds the gate lock and nests verify.sh must end that
# subshell with verify. The owner nonce now protects a forked child too, but keeping this shape
# prevents a needless extra process and preserves the old parent-pid fallback.
set -euo pipefail
root=$(git rev-parse --show-toplevel)

# `grep -rn "verify.sh" eng/ .github/` is the inventory prescribed by the ticket. A candidate
# is a shell file that both acquires the held lock and invokes the nested gate. Today that is
# eng/land.sh; do not turn the expected count into an allow-list, because a new landing route must
# be checked automatically.
inventory=$(mktemp)
trap 'rm -f "$inventory"' EXIT
# find + grep -l rather than grep -r --include/--exclude-dir: those two are GNU options, and this runs on the macOS gate host.
find "$root/eng" "$root/.github" -name '*.sh' -not -path '*/tests/*' 2>/dev/null | while IFS= read -r script; do
  grep -q 'gate_lock_acquire' "$script" || continue
  # Ignore comments and a usage string in verify.sh; the remaining matches are shell commands.
  if grep -n 'bash eng/verify.sh' "$script" | grep -Ev ':[[:space:]]*(#|printf )' | grep -q .; then
    printf '%s\n' "$script"
  fi
done > "$inventory" || true
expected=1
found=$(wc -l < "$inventory" | tr -d ' ')
[ "$found" -eq "$expected" ] || { echo "FAIL: held-lock nested-verify scripts expected $expected, found $found"; cat "$inventory"; exit 1; }

failures=0
while IFS= read -r script; do
  # `exec bash eng/verify.sh; fi )` permits nothing after verify in the branch or containing
  # subshell. Print the precise source location, so the positive-control mutation is actionable.
  while IFS=: read -r line text; do
    if [[ ! "$text" =~ exec[[:space:]]+bash[[:space:]]+eng/verify\.sh\;[[:space:]]*fi[[:space:]]*\) ]]; then
      echo "FAIL: $script:$line: nested bash eng/verify.sh is not the last command in its subshell"
      failures=$((failures + 1))
    fi
  done < <(grep -n 'bash eng/verify.sh' "$script" || true)
done < "$inventory"
[ "$failures" -eq 0 ] || exit 1
echo "PASS land-verify-last-in-subshell (held-lock scripts expected $expected, found $found)"
